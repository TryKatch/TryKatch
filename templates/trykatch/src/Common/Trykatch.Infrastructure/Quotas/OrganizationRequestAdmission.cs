using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Trykatch.Application.Organizations;
using Trykatch.Application.Quotas;

namespace Trykatch.Infrastructure.Quotas;

internal sealed record OrganizationRequestLimitSettings(bool Enabled, int PermitLimit, int WindowSeconds)
{
    internal static OrganizationRequestLimitSettings Create(IConfiguration configuration)
    {
        IConfigurationSection section = configuration.GetSection("OrganizationRequestLimits");
        IConfigurationSection[] properties = section.GetChildren().ToArray();
        if (section.Value is not null && (section.Value.Length != 0 || properties.Length == 0))
            throw InvalidConfiguration();
        bool enabled = false;
        int permits = 300;
        int seconds = 60;
        foreach (IConfigurationSection property in properties)
        {
            if (property.Value is null || property.GetChildren().Any()) throw InvalidConfiguration();
            switch (property.Key)
            {
                case "Enabled" when bool.TryParse(property.Value, out bool value): enabled = value; break;
                case "PermitLimit" when int.TryParse(property.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int value)
                    && value is >= 1 and <= 1_000_000: permits = value; break;
                case "WindowSeconds" when int.TryParse(property.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int value)
                    && value is >= 1 and <= 3_600: seconds = value; break;
                default: throw InvalidConfiguration();
            }
        }
        return new(enabled, permits, seconds);
    }

    private static InvalidOperationException InvalidConfiguration() => new("Organization request limit configuration is invalid.");
}

internal sealed class OrganizationRequestLimitStartupValidator(OrganizationRequestLimitSettings settings) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = settings.Enabled;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Independent database admission on the same captured profile as the host's organization contexts.</summary>
internal sealed class OrganizationRequestAdmission(IOrganizationContext organization, OrganizationRequestLimitSettings settings,
    string organizationConnection) : IOrganizationRequestAdmission
{
    public bool IsEnabled => settings.Enabled;

    public async Task<OrganizationRequestAdmissionResult> TryAdmitAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsEnabled) return new(OrganizationRequestAdmissionDisposition.Allowed);
        if (!organization.IsResolved || organization.OrganizationId == Guid.Empty || organization.ActorId == Guid.Empty)
            return new(OrganizationRequestAdmissionDisposition.Unavailable);
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(4));
        CancellationToken token = deadline.Token;
        try
        {
            await using NpgsqlConnection connection = new(organizationConnection);
            await connection.OpenAsync(token);
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
            await using (NpgsqlCommand scope = new("""
                SET LOCAL statement_timeout = '2000ms'; SET LOCAL lock_timeout = '500ms';
                SELECT set_config('app.organization_id', @organization, true), set_config('app.actor_id', @actor, true)
                """, connection, transaction) { CommandTimeout = 3 })
            {
                scope.Parameters.AddWithValue("organization", organization.OrganizationId.ToString());
                scope.Parameters.AddWithValue("actor", organization.ActorId.ToString());
                await scope.ExecuteNonQueryAsync(token);
            }
            // The conditional upsert owns the row lock even when the allowance is exhausted.
            // The separate request transaction commits before any business/replay effects run.
            await using NpgsqlCommand admit = new("""
                INSERT INTO app.organization_request_limits AS counters
                    ("OrganizationId", "Policy", "WindowStartUtc", "Attempts")
                VALUES (@organization, 'organization-api', statement_timestamp(), 1)
                ON CONFLICT ("OrganizationId", "Policy") DO UPDATE
                SET "WindowStartUtc" = CASE
                        WHEN counters."WindowStartUtc" + make_interval(secs => @seconds) <= EXCLUDED."WindowStartUtc"
                        THEN EXCLUDED."WindowStartUtc" ELSE counters."WindowStartUtc" END,
                    "Attempts" = CASE
                        WHEN counters."WindowStartUtc" + make_interval(secs => @seconds) <= EXCLUDED."WindowStartUtc"
                        THEN 1 ELSE counters."Attempts" + 1 END
                WHERE counters."WindowStartUtc" + make_interval(secs => @seconds) <= EXCLUDED."WindowStartUtc"
                    OR counters."Attempts" < @limit
                RETURNING 1
                """, connection, transaction) { CommandTimeout = 3 };
            admit.Parameters.AddWithValue("organization", organization.OrganizationId);
            admit.Parameters.AddWithValue("seconds", settings.WindowSeconds);
            admit.Parameters.AddWithValue("limit", settings.PermitLimit);
            bool allowed = await admit.ExecuteScalarAsync(token) is not null;
            int retryAfter = 0;
            if (!allowed)
            {
                await using NpgsqlCommand retry = new("""
                    SELECT GREATEST(1, CEIL(EXTRACT(EPOCH FROM
                        ("WindowStartUtc" + make_interval(secs => @seconds) - statement_timestamp()))))::integer
                    FROM app.organization_request_limits
                    WHERE "OrganizationId" = @organization AND "Policy" = 'organization-api'
                    """, connection, transaction) { CommandTimeout = 3 };
                retry.Parameters.AddWithValue("organization", organization.OrganizationId);
                retry.Parameters.AddWithValue("seconds", settings.WindowSeconds);
                if (await retry.ExecuteScalarAsync(token) is not int authoritativeSeconds)
                    return new(OrganizationRequestAdmissionDisposition.Unavailable);
                retryAfter = authoritativeSeconds;
            }
            await transaction.CommitAsync(token);
            return allowed ? new(OrganizationRequestAdmissionDisposition.Allowed)
                : new(OrganizationRequestAdmissionDisposition.Exhausted, retryAfter);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(OrganizationRequestAdmissionDisposition.Unavailable);
        }
        catch (Exception failure) when (failure is DbException or TimeoutException)
        {
            return new(OrganizationRequestAdmissionDisposition.Unavailable);
        }
    }
}

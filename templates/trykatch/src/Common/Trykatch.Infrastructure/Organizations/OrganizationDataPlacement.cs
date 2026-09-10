using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using System.Data;
using Trykatch.Application.Organizations;
using Trykatch.Domain.Organizations;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Modules;

namespace Trykatch.Infrastructure.Organizations;

internal interface IOrganizationDataPlacementAdapter
{
    OrganizationDataPlacementKind Kind { get; }
    Task<OrganizationDataRoute> ProvisionAsync(OrganizationDataPlacementRequest request, CancellationToken cancellationToken);
}

internal sealed class OrganizationDataPlacement(
    PlatformDbContext controlPlane,
    IEnumerable<IOrganizationDataPlacementAdapter> adapters,
    TimeProvider timeProvider) : IOrganizationDataPlacement
{
    private readonly Dictionary<OrganizationDataPlacementKind, IOrganizationDataPlacementAdapter> adapters =
        adapters.ToDictionary(adapter => adapter.Kind);

    public async Task<OrganizationDataPlacementResult> ProvisionAsync(
        OrganizationDataPlacementRequest request,
        CancellationToken cancellationToken)
    {
        bool ownsConnection = controlPlane.Database.GetDbConnection().State != ConnectionState.Open;
        if (ownsConnection)
            await controlPlane.Database.OpenConnectionAsync(cancellationToken);
        bool lockAcquired = false;
        string lockKey = request.OrganizationId.ToString();
        bool transactionScopedLock = controlPlane.Database.CurrentTransaction is not null;
        try
        {
            if (transactionScopedLock)
            {
                await controlPlane.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 20260910))",
                    cancellationToken);
            }
            else
            {
                await controlPlane.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT pg_advisory_lock(hashtextextended({lockKey}, 20260910))",
                    cancellationToken);
                lockAcquired = true;
            }

            OrganizationDataPlacementRecord? record = await controlPlane.OrganizationDataPlacements
                .SingleOrDefaultAsync(item => item.OrganizationId == request.OrganizationId, cancellationToken);
            if (record is not null && record.Placement != request.Placement)
                throw new InvalidOperationException("Organization data placement is immutable after provisioning begins.");
            if (record?.State == OrganizationProvisioningState.Ready)
                return new(OrganizationProvisioningState.Ready, ToRoute(record));

            if (record is null)
            {
                record = OrganizationDataPlacementRecord.Begin(
                    request.OrganizationId,
                    request.Placement,
                    request.Provider ?? "postgres",
                    request.RegionOrStamp);
                await controlPlane.OrganizationDataPlacements.AddAsync(record, cancellationToken);
            }
            else if (record.State == OrganizationProvisioningState.Failed)
            {
                record.BeginRetry(timeProvider.GetUtcNow());
            }
            await controlPlane.SaveChangesAsync(cancellationToken);

            if (!adapters.TryGetValue(request.Placement, out IOrganizationDataPlacementAdapter? adapter))
                throw new InvalidOperationException($"No data-placement adapter is registered for '{request.Placement}'.");

            try
            {
                OrganizationDataRoute route = await adapter.ProvisionAsync(request, cancellationToken);
                record.MarkReady(route.DatabaseIdentifier, route.SecretReference, route.SchemaVersion, timeProvider.GetUtcNow());
                await controlPlane.SaveChangesAsync(cancellationToken);
                return new(OrganizationProvisioningState.Ready, route);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                string failureCode = exception is PostgresException ? "postgres_provisioning_failed" : "provisioning_failed";
                record.MarkFailed(failureCode, timeProvider.GetUtcNow());
                await controlPlane.SaveChangesAsync(CancellationToken.None);
                return new(OrganizationProvisioningState.Failed, FailureCode: failureCode);
            }
        }
        finally
        {
            if (lockAcquired)
                await ReleaseLockAsync(lockKey);
            if (ownsConnection)
                await controlPlane.Database.CloseConnectionAsync();
        }
    }

    public async Task<OrganizationDataRoute> ResolveAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        OrganizationDataPlacementRecord record = await controlPlane.OrganizationDataPlacements
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.OrganizationId == organizationId, cancellationToken)
            ?? throw new KeyNotFoundException($"No data placement is registered for organization '{organizationId}'.");
        if (record.State != OrganizationProvisioningState.Ready)
            throw new InvalidOperationException($"Organization '{organizationId}' data placement is not ready.");
        return ToRoute(record);
    }

    private static OrganizationDataRoute ToRoute(OrganizationDataPlacementRecord record) => new(
        record.OrganizationId,
        record.Placement,
        record.Provider,
        record.DatabaseIdentifier!,
        record.SecretReference!,
        record.SchemaVersion!);

    private async Task ReleaseLockAsync(string lockKey)
    {
        try
        {
            await controlPlane.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_unlock(hashtextextended({lockKey}, 20260910))",
                CancellationToken.None);
        }
        catch (Exception)
        {
            // Discarding the connection guarantees PostgreSQL releases the session lock.
            await controlPlane.Database.CloseConnectionAsync();
        }
    }
}

internal sealed class SharedPostgresDataPlacement(
    IConfiguration configuration,
    ModuleCatalog modules) : IOrganizationDataPlacementAdapter
{
    public OrganizationDataPlacementKind Kind => OrganizationDataPlacementKind.Shared;

    public async Task<OrganizationDataRoute> ProvisionAsync(
        OrganizationDataPlacementRequest request,
        CancellationToken cancellationToken)
    {
        string connectionString = RuntimeDatabaseConnectionContract.Get(
            configuration,
            RuntimeDatabaseConnectionContract.Organization);
        NpgsqlConnectionStringBuilder builder = new(connectionString);
        string runtimeRole = builder.Username
            ?? throw new InvalidOperationException("Shared application connection must identify its runtime role.");
        (await PostgresIsolationInspector.InspectAsync(connectionString, runtimeRole, modules.Descriptors, cancellationToken))
            .ThrowIfInvalid();
        await using (NpgsqlConnection probe = new(connectionString))
        {
            await probe.OpenAsync(cancellationToken);
            await using NpgsqlTransaction transaction = await probe.BeginTransactionAsync(cancellationToken);
            await using NpgsqlCommand command = new("""
                SELECT 1;
                INSERT INTO platform.outbox_messages
                  ("Id", "Type", "Payload", "OccurredAt", "ProcessedAt", "Attempts", "LastError")
                VALUES (@id, 'organization.placement.probe', '{}'::jsonb, now(), NULL, 0, NULL)
                """, probe, transaction);
            command.Parameters.AddWithValue("id", Guid.CreateVersion7());
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.RollbackAsync(cancellationToken);
        }
        return new(
            request.OrganizationId,
            Kind,
            "postgres",
            builder.Database ?? throw new InvalidOperationException("Shared application connection must identify its database."),
            configuration["DataPlacement:Shared:SecretReference"] ?? "connection-string:trykatch-organization",
            modules.Descriptors.OrderBy(module => module.Version, StringComparer.Ordinal).LastOrDefault()?.Version ?? "0.0.0");
    }
}

/// <summary>
/// Dedicated provisioning fails closed unless a host supplies the separately
/// secured provider implementation. This prevents a UI preference from being
/// mistaken for physical isolation.
/// </summary>
internal sealed class DedicatedPostgresDataPlacement(
    IDedicatedPostgresProvisioner provisioner) : IOrganizationDataPlacementAdapter
{
    public OrganizationDataPlacementKind Kind => OrganizationDataPlacementKind.Dedicated;
    public Task<OrganizationDataRoute> ProvisionAsync(
        OrganizationDataPlacementRequest request,
        CancellationToken cancellationToken) => provisioner.ProvisionAsync(request, cancellationToken);
}

public interface IDedicatedPostgresProvisioner
{
    Task<OrganizationDataRoute> ProvisionAsync(
        OrganizationDataPlacementRequest request,
        CancellationToken cancellationToken);
}

internal sealed class UnconfiguredDedicatedPostgresProvisioner : IDedicatedPostgresProvisioner
{
    public Task<OrganizationDataRoute> ProvisionAsync(
        OrganizationDataPlacementRequest request,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException(
            "Dedicated PostgreSQL provisioning is not configured. No route or owner invitation may be issued.");
}

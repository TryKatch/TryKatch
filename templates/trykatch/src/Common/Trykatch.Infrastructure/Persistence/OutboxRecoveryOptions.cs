using System.Net.Sockets;
using System.Security.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Trykatch.Infrastructure.Persistence;

public sealed class OutboxRecoveryOptions
{
    public const string SectionName = "OutboxRecovery";
    public int BatchSize { get; init; } = 50;
    public int MaximumAttempts { get; init; } = 10;
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan RetryMaximumDelay { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan TransportTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan DatabaseCommandTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan DatabaseLockTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan BatchWorkBudget { get; init; } = TimeSpan.FromSeconds(60);
}

internal sealed class OutboxRecoveryOptionsValidator : IValidateOptions<OutboxRecoveryOptions>
{
    public ValidateOptionsResult Validate(string? name, OutboxRecoveryOptions options)
    {
        List<string> failures = [];
        Check(options.BatchSize is >= 1 and <= 500, "BatchSize must be between 1 and 500.");
        Check(options.MaximumAttempts is >= 1 and <= 100, "MaximumAttempts must be between 1 and 100.");
        Check(options.PollInterval >= TimeSpan.FromMilliseconds(100) && options.PollInterval <= TimeSpan.FromMinutes(5), "PollInterval must be between 100 milliseconds and 5 minutes.");
        Check(options.RetryBaseDelay >= TimeSpan.FromMilliseconds(100) && options.RetryBaseDelay <= TimeSpan.FromSeconds(30), "RetryBaseDelay must be between 100 milliseconds and 30 seconds.");
        Check(options.RetryMaximumDelay >= options.RetryBaseDelay && options.RetryMaximumDelay <= TimeSpan.FromMinutes(5), "RetryMaximumDelay must be between RetryBaseDelay and 5 minutes.");
        Check(options.TransportTimeout >= TimeSpan.FromSeconds(1) && options.TransportTimeout <= TimeSpan.FromSeconds(120), "TransportTimeout must be between 1 and 120 seconds.");
        Check(options.DatabaseCommandTimeout >= TimeSpan.FromSeconds(1) && options.DatabaseCommandTimeout <= TimeSpan.FromSeconds(120), "DatabaseCommandTimeout must be between 1 and 120 seconds.");
        Check(options.DatabaseLockTimeout >= TimeSpan.FromMilliseconds(100) && options.DatabaseLockTimeout <= TimeSpan.FromSeconds(10), "DatabaseLockTimeout must be between 100 milliseconds and 10 seconds.");
        Check(options.BatchWorkBudget >= TimeSpan.FromSeconds(1) && options.BatchWorkBudget <= TimeSpan.FromSeconds(300), "BatchWorkBudget must be between 1 and 300 seconds.");
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);

        void Check(bool condition, string failure)
        {
            if (!condition) failures.Add(failure);
        }
    }
}

internal sealed class OutboxRetryBackoff(OutboxRecoveryOptions options, Func<double>? jitter = null)
{
    private int failures;
    private readonly Func<double> jitterSource = jitter ?? Random.Shared.NextDouble;

    public TimeSpan NextDelay()
    {
        int exponent = Math.Min(failures++, 30);
        double factor = Math.Pow(2, exponent);
        double capped = Math.Min(options.RetryMaximumDelay.TotalMilliseconds,
            options.RetryBaseDelay.TotalMilliseconds * factor);
        double jittered = options.RetryBaseDelay.TotalMilliseconds
            + (capped - options.RetryBaseDelay.TotalMilliseconds) * Math.Clamp(jitterSource(), 0, 1);
        return TimeSpan.FromMilliseconds(Math.Min(jittered, options.RetryMaximumDelay.TotalMilliseconds));
    }

    public void Reset() => failures = 0;
}

internal enum OutboxDatabaseFaultKind { Transient, Permanent }

internal static class OutboxDatabaseFaultClassifier
{
    private static readonly HashSet<string> PermanentSqlStates =
    [
        PostgresErrorCodes.InvalidAuthorizationSpecification,
        PostgresErrorCodes.InvalidPassword,
        PostgresErrorCodes.InvalidCatalogName,
        PostgresErrorCodes.UndefinedTable,
        PostgresErrorCodes.UndefinedColumn,
        PostgresErrorCodes.InsufficientPrivilege,
        PostgresErrorCodes.SyntaxError
    ];

    public static OutboxDatabaseFaultKind Classify(Exception exception)
    {
        for (Exception? cause = exception; cause is not null; cause = cause.InnerException)
            if (cause is AuthenticationException or OptionsValidationException)
                return OutboxDatabaseFaultKind.Permanent;
        Exception current = exception;
        while (current is DbUpdateException && current.InnerException is not null)
            current = current.InnerException;
        if (current is PostgresException postgres)
        {
            if (PermanentSqlStates.Contains(postgres.SqlState) || postgres.SqlState.StartsWith("42", StringComparison.Ordinal))
                return OutboxDatabaseFaultKind.Permanent;
            // Host cancellation is handled by the worker before classification. A server
            // statement deadline must recover just like a client command timeout.
            if (postgres.SqlState == PostgresErrorCodes.QueryCanceled)
                return OutboxDatabaseFaultKind.Transient;
            return postgres.IsTransient ? OutboxDatabaseFaultKind.Transient : OutboxDatabaseFaultKind.Permanent;
        }
        if (current is NpgsqlException npgsql)
            return npgsql.IsTransient || npgsql.InnerException is IOException or SocketException
                ? OutboxDatabaseFaultKind.Transient
                : OutboxDatabaseFaultKind.Permanent;
        if (current is TimeoutException or IOException or SocketException)
            return OutboxDatabaseFaultKind.Transient;
        return OutboxDatabaseFaultKind.Permanent;
    }
}

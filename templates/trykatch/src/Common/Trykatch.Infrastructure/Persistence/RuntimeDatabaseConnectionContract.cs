using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Trykatch.Infrastructure.Persistence;

public static class RuntimeDatabaseConnectionContract
{
    public const string Organization = "trykatch-organization";
    public const string Platform = "trykatch-platform";
    public const string Identity = "trykatch-identity";
    public const string Outbox = "trykatch-outbox";
    public const string WorkCoordinator = "trykatch-work";

    public static void Validate(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        Dictionary<string, string> users = new(StringComparer.Ordinal);
        foreach (string name in new[] { Organization, Platform, Identity, Outbox, WorkCoordinator })
        {
            string connectionString = configuration.GetConnectionString(name)
                ?? throw new InvalidOperationException($"Production connection string '{name}' is required.");
            NpgsqlConnectionStringBuilder builder = new(connectionString);
            if (string.IsNullOrWhiteSpace(builder.Username))
                throw new InvalidOperationException($"Connection string '{name}' must identify its least-privilege PostgreSQL role.");
            users.Add(name, builder.Username);
        }

        if (users.Values.Distinct(StringComparer.Ordinal).Count() != users.Count)
            throw new InvalidOperationException("Organization, platform, identity, outbox, and work coordinator connections must use distinct PostgreSQL roles.");
        if (!string.Equals(users[Organization], "trykatch_org_runtime", StringComparison.Ordinal)
            || !string.Equals(users[Platform], "trykatch_platform_runtime", StringComparison.Ordinal)
            || !string.Equals(users[Identity], "trykatch_identity_runtime", StringComparison.Ordinal)
            || !string.Equals(users[Outbox], "trykatch_outbox_worker", StringComparison.Ordinal)
            || !string.Equals(users[WorkCoordinator], "trykatch_work_coordinator", StringComparison.Ordinal))
            throw new InvalidOperationException("Runtime connections must use the Trykatch least-privilege role contract.");
    }

    internal static string Get(IConfiguration configuration, string name)
    {
        string? connectionString = configuration.GetConnectionString(name);
        if (connectionString is null && !string.Equals(name, WorkCoordinator, StringComparison.Ordinal))
        {
            connectionString = configuration.GetConnectionString("trykatchdb");
        }

        return connectionString
            ?? throw new InvalidOperationException($"Connection string '{name}' is required.");
    }
}

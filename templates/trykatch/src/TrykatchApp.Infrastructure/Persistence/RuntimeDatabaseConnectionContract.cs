using Microsoft.Extensions.Configuration;
using Npgsql;

namespace TrykatchApp.Infrastructure.Persistence;

public static class RuntimeDatabaseConnectionContract
{
    public const string Organization = "trykatch-organization";
    public const string Platform = "trykatch-platform";
    public const string Identity = "trykatch-identity";
    public const string Outbox = "trykatch-outbox";

    public static void Validate(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        Dictionary<string, string> users = new(StringComparer.Ordinal);
        foreach (string name in new[] { Organization, Platform, Identity, Outbox })
        {
            string connectionString = configuration.GetConnectionString(name)
                ?? throw new InvalidOperationException($"Production connection string '{name}' is required.");
            NpgsqlConnectionStringBuilder builder = new(connectionString);
            if (string.IsNullOrWhiteSpace(builder.Username))
                throw new InvalidOperationException($"Connection string '{name}' must identify its least-privilege PostgreSQL role.");
            users.Add(name, builder.Username);
        }

        if (users.Values.Distinct(StringComparer.Ordinal).Count() != users.Count)
            throw new InvalidOperationException("Organization, platform, identity, and outbox connections must use distinct PostgreSQL roles.");
        if (!string.Equals(users[Organization], "trykatch_org_runtime", StringComparison.Ordinal)
            || !string.Equals(users[Platform], "trykatch_platform_runtime", StringComparison.Ordinal)
            || !string.Equals(users[Identity], "trykatch_identity_runtime", StringComparison.Ordinal)
            || !string.Equals(users[Outbox], "trykatch_outbox_worker", StringComparison.Ordinal))
            throw new InvalidOperationException("Runtime connections must use the Trykatch least-privilege role contract.");
    }

    internal static string Get(IConfiguration configuration, string name) =>
        configuration.GetConnectionString(name)
        ?? configuration.GetConnectionString("trykatchdb")
        ?? throw new InvalidOperationException($"Connection string '{name}' is required.");
}

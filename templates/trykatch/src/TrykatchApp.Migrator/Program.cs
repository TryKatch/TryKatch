using System.Text.RegularExpressions;
using TrykatchApp.Identity;
using TrykatchApp.Infrastructure.Persistence;
using TrykatchApp.Migrator;
using TrykatchApp.Migrator.Modules;
using TrykatchApp.Modules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
string connectionString = builder.Configuration.GetConnectionString("trykatchdb")
    ?? throw new InvalidOperationException("Connection string 'trykatchdb' is required.");

DbContextOptions<PlatformDbContext> platformOptions = new DbContextOptionsBuilder<PlatformDbContext>()
    .UseNpgsql(connectionString)
    .Options;
DbContextOptions<ApplicationDbContext> applicationOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
    .UseNpgsql(connectionString)
    .Options;
DbContextOptions<IdentityDbContext> identityOptions = new DbContextOptionsBuilder<IdentityDbContext>()
    .UseNpgsql(connectionString)
    .Options;

ServiceCollection moduleServices = new();
TrykatchModuleCatalog moduleCatalog = moduleServices.AddTrykatchModules(builder.Configuration, EnabledModules.All);
await using ServiceProvider moduleProvider = moduleServices.BuildServiceProvider(validateScopes: true);
IApplicationModelContributor[] modelContributors = moduleProvider
    .GetServices<IApplicationModelContributor>()
    .ToArray();

// Identity must exist before application migrations enrich audit data with actor names.
await using (IdentityDbContext identity = new(identityOptions))
{
    await identity.Database.MigrateAsync();
}

await using (PlatformDbContext platform = new(platformOptions))
{
    await platform.Database.MigrateAsync();
}

// The generated registry gives the API and migrator the same ordered module
// graph. No assembly scanning or second hand-maintained module list is allowed.
await using (ApplicationDbContext application = new(applicationOptions, modelContributors))
{
    await application.Database.MigrateAsync();
}

// Optional module-owned SQL migrations are forward-only, serialized with a
// PostgreSQL advisory lock, and checksum-verified against durable history.
await ModuleMigrationExecutor.ApplyAsync(connectionString, moduleCatalog.Modules);

string? runtimeRole = builder.Configuration["Database:RuntimeRole"];
if (!string.IsNullOrWhiteSpace(runtimeRole))
{
    await RuntimeRoleProvisioner.ProvisionAsync(connectionString, runtimeRole);
}

internal static partial class RuntimeRoleProvisioner
{
    public static async Task ProvisionAsync(string connectionString, string roleName)
    {
        if (!RoleNamePattern().IsMatch(roleName))
            throw new InvalidOperationException("Database runtime role must be a lowercase PostgreSQL identifier.");

        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();

        await using NpgsqlCommand verify = connection.CreateCommand();
        verify.CommandText = "SELECT rolbypassrls OR rolsuper FROM pg_roles WHERE rolname = @role_name";
        verify.Parameters.AddWithValue("role_name", roleName);
        if (await verify.ExecuteScalarAsync() is not bool bypassesRls)
            throw new InvalidOperationException("Database runtime role must be created by the bootstrap administrator before migrations run.");
        if (bypassesRls)
            throw new InvalidOperationException("Database runtime role must exist without superuser or BYPASSRLS privileges.");

        await using NpgsqlCommand provision = connection.CreateCommand();
        provision.CommandText = $"""
            GRANT CONNECT ON DATABASE {QuoteIdentifier(connection.Database)} TO {roleName};
            GRANT USAGE ON SCHEMA identity, platform, app TO {roleName};
            GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA identity, platform, app TO {roleName};
            GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA identity, platform, app TO {roleName};
            ALTER DEFAULT PRIVILEGES IN SCHEMA identity, platform, app
                GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO {roleName};
            ALTER DEFAULT PRIVILEGES IN SCHEMA identity, platform, app
                GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO {roleName};
            """;
        await provision.ExecuteNonQueryAsync();
    }

    private static string QuoteIdentifier(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex RoleNamePattern();
}

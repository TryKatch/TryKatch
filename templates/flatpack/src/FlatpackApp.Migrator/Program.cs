using System.Text.RegularExpressions;
using FlatpackApp.Identity;
using FlatpackApp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Npgsql;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
string connectionString = builder.Configuration.GetConnectionString("flatpackdb")
    ?? throw new InvalidOperationException("Connection string 'flatpackdb' is required.");

DbContextOptions<PlatformDbContext> platformOptions = new DbContextOptionsBuilder<PlatformDbContext>()
    .UseNpgsql(connectionString)
    .Options;
DbContextOptions<ApplicationDbContext> applicationOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
    .UseNpgsql(connectionString)
    .Options;
DbContextOptions<IdentityDbContext> identityOptions = new DbContextOptionsBuilder<IdentityDbContext>()
    .UseNpgsql(connectionString)
    .Options;

await using (PlatformDbContext platform = new(platformOptions))
{
    await platform.Database.MigrateAsync();
}

await using (ApplicationDbContext application = new(applicationOptions))
{
    await application.Database.MigrateAsync();
}

await using (IdentityDbContext identity = new(identityOptions))
{
    await identity.Database.MigrateAsync();
}

string? runtimeRole = builder.Configuration["Database:RuntimeRole"];
string? runtimePassword = builder.Configuration["Database:RuntimePassword"];
if (!string.IsNullOrWhiteSpace(runtimeRole) || !string.IsNullOrWhiteSpace(runtimePassword))
{
    if (string.IsNullOrWhiteSpace(runtimeRole) || string.IsNullOrWhiteSpace(runtimePassword))
        throw new InvalidOperationException("Database runtime role and password must be supplied together.");

    await RuntimeRoleProvisioner.ProvisionAsync(connectionString, runtimeRole, runtimePassword);
}

internal static partial class RuntimeRoleProvisioner
{
    public static async Task ProvisionAsync(string connectionString, string roleName, string password)
    {
        if (!RoleNamePattern().IsMatch(roleName))
            throw new InvalidOperationException("Database runtime role must be a lowercase PostgreSQL identifier.");
        if (password.Length < 24)
            throw new InvalidOperationException("Database runtime password must contain at least 24 characters.");

        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();

        await using NpgsqlCommand renderRoleCommand = connection.CreateCommand();
        renderRoleCommand.CommandText = """
            SELECT CASE WHEN EXISTS (SELECT 1 FROM pg_roles WHERE rolname = @role_name)
                THEN format('ALTER ROLE %I WITH LOGIN PASSWORD %L NOINHERIT NOBYPASSRLS', @role_name, @password)
                ELSE format('CREATE ROLE %I WITH LOGIN PASSWORD %L NOINHERIT NOBYPASSRLS', @role_name, @password)
            END;
            """;
        renderRoleCommand.Parameters.AddWithValue("role_name", roleName);
        renderRoleCommand.Parameters.AddWithValue("password", password);
        string roleStatement = (string)(await renderRoleCommand.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("Unable to provision the database runtime role."));

        await using NpgsqlCommand provision = connection.CreateCommand();
        provision.CommandText = $"""
            {roleStatement};
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

        await using NpgsqlCommand verify = connection.CreateCommand();
        verify.CommandText = "SELECT rolbypassrls OR rolsuper FROM pg_roles WHERE rolname = @role_name";
        verify.Parameters.AddWithValue("role_name", roleName);
        if (await verify.ExecuteScalarAsync() is not bool bypassesRls || bypassesRls)
            throw new InvalidOperationException("Database runtime role must exist without superuser or BYPASSRLS privileges.");
    }

    private static string QuoteIdentifier(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex RoleNamePattern();
}

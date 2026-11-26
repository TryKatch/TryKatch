using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Trykatch.Identity;
using Trykatch.Infrastructure.Organizations;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Migrator.Modules;
using Trykatch.Modules;

namespace Trykatch.Migrator;

internal static class MigratorHostingExtensions
{
    private const string MigrationLockName = "trykatch:database-migrations:v1";

    private static readonly JsonSerializerOptions MaintenanceJsonOptions =
        new(JsonSerializerDefaults.Web);

    public static async Task<int> RunDatabaseMigrationsAsync(this HostApplicationBuilder builder)
    {
        string connectionString = RuntimeDatabaseConnectionContract.ResolveConnectionString(
            builder.Configuration,
            "trykatchdb",
            "Database:MigratorPassword");

        if (builder.Configuration["data-protection-keys"] is { } keyMaintenance)
        {
            return await RunDataProtectionKeyMaintenanceAsync(builder, connectionString, keyMaintenance);
        }

        RuntimeRoleSettings roleSettings = ReadRuntimeRoles(builder.Configuration);
        bool isProduction = builder.Environment.IsProduction();
        RuntimeDatabaseRoles? requiredRoles = roleSettings.IsConfigured
            ? roleSettings.RequireAll()
            : null;
        if (isProduction)
        {
            if (requiredRoles is null)
                throw new InvalidOperationException(
                    "All five Database runtime role settings are required in Production.");
            RuntimeRoleSettings.ValidateProductionContract(requiredRoles);
        }

        ServiceCollection moduleServices = new();
        ModuleCatalog moduleCatalog = moduleServices.AddModules(builder.Configuration, EnabledModules.All);
        await using ServiceProvider moduleProvider = moduleServices.BuildServiceProvider(validateScopes: true);
        IApplicationModelContributor[] modelContributors = moduleProvider
            .GetServices<IApplicationModelContributor>()
            .ToArray();

        // The physical session owns the deployment lock. Disposing it releases the lock on every exit.
        NpgsqlConnectionStringBuilder ownerConnection = new(connectionString) { Pooling = false };
        await using NpgsqlConnection connection = new(ownerConnection.ConnectionString);
        await connection.OpenAsync();
        await AcquireMigrationLockAsync(connection);
        if (isProduction)
        {
            await ValidateProductionOwnerAsync(connection, ownerConnection.Database ?? string.Empty);
            await RuntimeRoleProvisioner.ValidateRolesAsync(connection, requiredRoles!);
        }
        else if (requiredRoles is not null)
        {
            await RuntimeRoleProvisioner.ValidateRolesAsync(connection, requiredRoles);
        }

        DbContextOptions<PlatformDbContext> platformOptions = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseNpgsql(connection)
            .Options;
        DbContextOptions<ApplicationDbContext> applicationOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(connection)
            .Options;
        DbContextOptions<IdentityDbContext> identityOptions = new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNpgsql(connection)
            .Options;

        await ApplyMigrationsAsync(
            connection,
            identityOptions,
            platformOptions,
            applicationOptions,
            modelContributors,
            moduleCatalog);
        await ValidateInstalledSchemaAsync(
            connection,
            identityOptions,
            platformOptions,
            applicationOptions,
            modelContributors,
            moduleCatalog);
        if (requiredRoles is not null)
        {
            await RuntimeRoleProvisioner.ProvisionAsync(connection, requiredRoles);
            (await PostgresIsolationInspector.InspectAsync(
                connection,
                requiredRoles,
                moduleCatalog.Descriptors)).ThrowIfInvalid();
        }

        return 0;
    }

    private static async Task<int> RunDataProtectionKeyMaintenanceAsync(
        HostApplicationBuilder builder,
        string connectionString,
        string keyMaintenance)
    {
        try
        {
            if (keyMaintenance is not ("dry-run" or "apply")
                || (keyMaintenance == "apply" && builder.Configuration["confirm-key-backup"] != "true"))
            {
                throw new InvalidOperationException(
                    "Use --data-protection-keys dry-run or apply; apply also requires --confirm-key-backup true after a protected backup and quiescing API writers.");
            }

            KeyMaintenanceResult result = await DataProtectionKeyMaintenance.RunAsync(
                connectionString,
                builder.Configuration,
                keyMaintenance == "apply");
            Console.WriteLine(JsonSerializer.Serialize(result, MaintenanceJsonOptions));
            return 0;
        }
        catch (Exception failure) when (failure is InvalidOperationException
                                        or Microsoft.Extensions.Options.OptionsValidationException)
        {
            // Maintenance errors are deliberately value-free; never emit XML or an inner exception.
            Console.Error.WriteLine(failure.Message);
            return 1;
        }
    }

    private static async Task ApplyMigrationsAsync(
        NpgsqlConnection connection,
        DbContextOptions<IdentityDbContext> identityOptions,
        DbContextOptions<PlatformDbContext> platformOptions,
        DbContextOptions<ApplicationDbContext> applicationOptions,
        IApplicationModelContributor[] modelContributors,
        ModuleCatalog moduleCatalog)
    {
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
        await using (ApplicationDbContext application = new(
                         applicationOptions,
                         modelContributors,
                         moduleCatalog: moduleCatalog))
        {
            await application.Database.MigrateAsync();
        }

        // Optional module-owned SQL migrations are forward-only, serialized with a
        // PostgreSQL advisory lock, and checksum-verified against durable history.
        await ModuleMigrationExecutor.ApplyAsync(
            connection,
            moduleCatalog.Modules);
    }

    private static async Task ValidateInstalledSchemaAsync(
        NpgsqlConnection connection,
        DbContextOptions<IdentityDbContext> identityOptions,
        DbContextOptions<PlatformDbContext> platformOptions,
        DbContextOptions<ApplicationDbContext> applicationOptions,
        IApplicationModelContributor[] modelContributors,
        ModuleCatalog moduleCatalog)
    {
        await InstalledSchemaCatalog.SynchronizeAsync(
            connection,
            EnabledModules.InstalledDataResources);

        HashSet<string> declaredRelations = new(RuntimeDatabaseAccessProfiles.HostOwnedRelations, StringComparer.Ordinal);
        await using (IdentityDbContext identity = new(identityOptions))
        {
            AddModelRelations(identity, declaredRelations);
        }

        await using (PlatformDbContext platform = new(platformOptions))
        {
            AddModelRelations(platform, declaredRelations);
        }

        await using (ApplicationDbContext application = new(
                         applicationOptions,
                         modelContributors,
                         moduleCatalog: moduleCatalog))
        {
            AddModelRelations(application, declaredRelations);
        }

        foreach (DataResourceDescriptor resource in await InstalledSchemaCatalog.ReadAsync(connection))
        {
            declaredRelations.Add($"{resource.Schema}.{resource.Table}");
        }

        await InstalledSchemaCatalog.ValidateDeclaredObjectsAsync(connection, declaredRelations);
    }

    private static RuntimeRoleSettings ReadRuntimeRoles(ConfigurationManager configuration)
    {
        return new RuntimeRoleSettings(
            configuration["Database:OrganizationRuntimeRole"],
            configuration["Database:PlatformRuntimeRole"],
            configuration["Database:IdentityRuntimeRole"],
            configuration["Database:OutboxWorkerRole"],
            configuration["Database:WorkCoordinatorRole"]);
    }

    private static async Task AcquireMigrationLockAsync(NpgsqlConnection connection)
    {
        await using NpgsqlCommand command = new(
            "SELECT pg_advisory_lock(hashtext(@lock_name))", connection);
        command.Parameters.AddWithValue("lock_name", MigrationLockName);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ValidateProductionOwnerAsync(
        NpgsqlConnection connection,
        string configuredDatabase)
    {
        await using NpgsqlCommand command = new("""
            SELECT current_user = 'trykatch_migrator'
               AND session_user = 'trykatch_migrator'
               AND owner.rolname = 'trykatch_migrator'
               AND owner.rolcanlogin
               AND NOT (owner.rolsuper OR owner.rolcreatedb OR owner.rolcreaterole
                        OR owner.rolreplication OR owner.rolbypassrls OR owner.rolinherit)
               AND NOT EXISTS (SELECT 1 FROM pg_auth_members m WHERE m.member = owner.oid)
            FROM pg_database d
            JOIN pg_roles owner ON owner.oid = d.datdba
            WHERE d.datname = current_database()
              AND (@configured_database = '' OR d.datname = @configured_database)
            """, connection);
        command.Parameters.AddWithValue("configured_database", configuredDatabase);
        if (await command.ExecuteScalarAsync() is not true)
            throw new InvalidOperationException(
                "Production migrations must connect as the restricted trykatch_migrator login that owns the target database.");
    }

    private static void AddModelRelations(DbContext context, HashSet<string> relations)
    {
        foreach (Microsoft.EntityFrameworkCore.Metadata.IEntityType entity in context.Model.GetEntityTypes())
        {
            string? table = entity.GetTableName();
            string? schema = entity.GetSchema();
            if (table is not null
                && schema is not null
                && RuntimeDatabaseAccessProfiles.ManagedSchemas.Contains(schema))
            {
                relations.Add($"{schema}.{table}");
            }
        }
    }

    private sealed record RuntimeRoleSettings(
        string? Organization,
        string? Platform,
        string? Identity,
        string? Outbox,
        string? WorkCoordinator)
    {
        public bool IsConfigured => new[] { Organization, Platform, Identity, Outbox, WorkCoordinator }
            .Any(role => !string.IsNullOrWhiteSpace(role));

        public RuntimeDatabaseRoles RequireAll() => new(
            Required(Organization, "organization"),
            Required(Platform, "platform"),
            Required(Identity, "identity"),
            Required(Outbox, "outbox"),
            Required(WorkCoordinator, "work coordinator"));

        public static void ValidateProductionContract(RuntimeDatabaseRoles roles)
        {
            if (roles.Organization != "trykatch_org_runtime"
                || roles.Platform != "trykatch_platform_runtime"
                || roles.Identity != "trykatch_identity_runtime"
                || roles.Outbox != "trykatch_outbox_worker"
                || roles.WorkCoordinator != "trykatch_work_coordinator")
                throw new InvalidOperationException(
                    "Production Database runtime roles must use the Trykatch role contract: trykatch_org_runtime, trykatch_platform_runtime, trykatch_identity_runtime, trykatch_outbox_worker, and trykatch_work_coordinator.");
        }

        private static string Required(string? role, string purpose) =>
            !string.IsNullOrWhiteSpace(role)
                ? role
                : throw new InvalidOperationException(
                    $"Database {purpose} runtime role is required when role provisioning is enabled.");
    }
}

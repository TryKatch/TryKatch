using Trykatch.Identity;
using Trykatch.Infrastructure.Organizations;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Migrator;
using Trykatch.Migrator.Modules;
using Trykatch.Modules;
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
ModuleCatalog moduleCatalog = moduleServices.AddModules(builder.Configuration, EnabledModules.All);
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
await using (ApplicationDbContext application = new(applicationOptions, modelContributors, moduleCatalog: moduleCatalog))
{
    await application.Database.MigrateAsync();
}

// Optional module-owned SQL migrations are forward-only, serialized with a
// PostgreSQL advisory lock, and checksum-verified against durable history.
await ModuleMigrationExecutor.ApplyAsync(connectionString, moduleCatalog.Modules);
await Trykatch.Infrastructure.Organizations.InstalledSchemaCatalog.SynchronizeAsync(
    connectionString, EnabledModules.InstalledDataResources);

HashSet<string> declaredRelations = new(RuntimeDatabaseAccessProfiles.HostOwnedRelations, StringComparer.Ordinal);
await using (IdentityDbContext identity = new(identityOptions))
    AddModelRelations(identity, declaredRelations);
await using (PlatformDbContext platform = new(platformOptions))
    AddModelRelations(platform, declaredRelations);
await using (ApplicationDbContext application = new(applicationOptions, modelContributors, moduleCatalog: moduleCatalog))
    AddModelRelations(application, declaredRelations);
foreach (DataResourceDescriptor resource in await InstalledSchemaCatalog.ReadAsync(connectionString))
    declaredRelations.Add($"{resource.Schema}.{resource.Table}");
await InstalledSchemaCatalog.ValidateDeclaredObjectsAsync(connectionString, declaredRelations);

RuntimeRoleSettings runtimeRoles = new(
    builder.Configuration["Database:OrganizationRuntimeRole"],
    builder.Configuration["Database:PlatformRuntimeRole"],
    builder.Configuration["Database:IdentityRuntimeRole"],
    builder.Configuration["Database:OutboxWorkerRole"]);
if (runtimeRoles.IsConfigured)
{
    RuntimeDatabaseRoles requiredRoles = runtimeRoles.RequireAll();
    await RuntimeRoleProvisioner.ProvisionAsync(connectionString, requiredRoles);
    (await Trykatch.Infrastructure.Organizations.PostgresIsolationInspector.InspectAsync(
        connectionString,
        requiredRoles,
        moduleCatalog.Descriptors))
        .ThrowIfInvalid();
}

static void AddModelRelations(DbContext context, ISet<string> relations)
{
    foreach (Microsoft.EntityFrameworkCore.Metadata.IEntityType entity in context.Model.GetEntityTypes())
    {
        string? table = entity.GetTableName();
        string? schema = entity.GetSchema();
        if (table is not null && schema is not null && RuntimeDatabaseAccessProfiles.ManagedSchemas.Contains(schema))
            relations.Add($"{schema}.{table}");
    }
}

internal sealed record RuntimeRoleSettings(string? Organization, string? Platform, string? Identity, string? Outbox)
{
    public bool IsConfigured => new[] { Organization, Platform, Identity, Outbox }.Any(role => !string.IsNullOrWhiteSpace(role));

    public RuntimeDatabaseRoles RequireAll() => new(
        Required(Organization, "organization"),
        Required(Platform, "platform"),
        Required(Identity, "identity"),
        Required(Outbox, "outbox"));

    private static string Required(string? role, string purpose) =>
        !string.IsNullOrWhiteSpace(role)
            ? role
            : throw new InvalidOperationException($"Database {purpose} runtime role is required when role provisioning is enabled.");
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Trykatch.Application.Authorization;
using Trykatch.Application.Auditing;
using Trykatch.Application.Outbox;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Modules;
using Trykatch.Modules.Documents.Domain;
using Trykatch.Modules.Documents.Infrastructure;

namespace Trykatch.IntegrationTests;

public sealed partial class ModuleWorkExecutionIntegrationTests
{
    [TestMethod]
    public async Task DocumentInventoryPagesAllLifecycleStatesWithinCurrentOrganization()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        ProjectsSeededUser foreign = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        List<ModuleQuotaInventoryEntry> expected = [];
        for (int index = 0; index < 5; index++)
        {
            Guid id = Guid.NewGuid();
            Guid? operation = index % 2 == 0 ? null : Guid.NewGuid();
            await fixture.SeedInventoryDocumentAsync(user, id, operation, index + 1, lifecycle: index % 3);
            expected.Add(new(id, operation ?? id, index + 1));
        }
        await fixture.SeedInventoryDocumentAsync(user, Guid.NewGuid(), null, null, metadataOnly: true);
        await fixture.SeedInventoryDocumentAsync(foreign, Guid.NewGuid(), Guid.NewGuid(), 999);

        await using MutationScope scope = await fixture.BeginMutationScopeAsync(user);
        ModuleQuotaInventorySourceBinding binding = DocumentInventoryBinding(scope.Services);
        binding.Source.ShouldBe(new("documents", "documents.inventory", 1));
        ApplicationDbContext database = scope.Services.GetRequiredService<ApplicationDbContext>();
        (await database.Set<DocumentRecord>().CountAsync()).ShouldBe(3,
            "The ordinary query still hides archived/deleted rows and foreign organizations.");
        List<ModuleQuotaInventoryEntry> actual = [];
        string? cursor = null;
        for (int pageNumber = 0; pageNumber < 3; pageNumber++)
        {
            ModuleQuotaInventoryPage page = await binding.Adapter.ReadPageAsync(cursor, 2);
            page.Entries.Count.ShouldBeLessThanOrEqualTo(2);
            actual.AddRange(page.Entries);
            if (pageNumber < 2)
            {
                page.IsComplete.ShouldBeFalse();
                page.NextCursor.ShouldNotBeNullOrEmpty();
                page.NextCursor.ShouldNotBe(cursor);
            }
            else
            {
                page.IsComplete.ShouldBeTrue();
                page.NextCursor.ShouldBeNull();
            }
            cursor = page.NextCursor;
        }
        actual.OrderBy(entry => entry.SourceId).ShouldBe(expected.OrderBy(entry => entry.SourceId));
        database.ChangeTracker.Entries<DocumentRecord>().ShouldBeEmpty();
        ModuleQuotaInventoryPage repeated = await binding.Adapter.ReadPageAsync(null, 2);
        repeated.Entries.ShouldBe(actual.Take(2));
    }

    [TestMethod]
    public async Task DocumentInventoryKeepsOrganizationScopeEvenWithOwnerConnection()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        ProjectsSeededUser foreign = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        Guid ownId = Guid.NewGuid();
        Guid foreignId = Guid.NewGuid();
        await fixture.SeedInventoryDocumentAsync(user, ownId, null, 7, lifecycle: 1);
        await fixture.SeedInventoryDocumentAsync(foreign, foreignId, null, 9, lifecycle: 1);
        await using MutationScope scope = await fixture.BeginMutationScopeAsync(user);
        await using ApplicationDbContext database = fixture.CreateInventoryOwnerDatabase(scope);
        (await database.Set<DocumentRecord>().IgnoreQueryFilters()
            .CountAsync(row => row.Id == ownId || row.Id == foreignId)).ShouldBe(2,
                "The owner connection bypasses RLS, so this case independently detects an unscoped inventory query.");
        OrganizationModuleData data = new(database, scope.OrganizationContext,
            scope.Services.GetRequiredService<IAuditWriter>(), scope.Services.GetRequiredService<IOutboxWriter>());
        ServiceCollection moduleServices = new();
        new DocumentsModule().Register(moduleServices, new ConfigurationBuilder().Build());
        moduleServices.AddSingleton<IOrganizationModuleData>(data);
        await using ServiceProvider provider = moduleServices.BuildServiceProvider();
        ModuleQuotaInventoryPage page = await DocumentInventoryBinding(provider).Adapter.ReadPageAsync(null, 500);
        page.IsComplete.ShouldBeTrue();
        page.Entries.ShouldBe([new ModuleQuotaInventoryEntry(ownId, ownId, 7)]);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow(0L)]
    [DataRow(-1L)]
    public async Task DocumentInventoryRefusesUnresolvedUploadedSizes(long? units)
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        await fixture.SeedInventoryDocumentAsync(user, Guid.NewGuid(), null, units, lifecycle: 2);
        await using MutationScope scope = await fixture.BeginMutationScopeAsync(user);
        IModuleQuotaInventorySource inventory = DocumentInventoryBinding(scope.Services).Adapter;
        await Should.ThrowAsync<OrganizationQuotaUnavailableException>(() => inventory.ReadPageAsync(null, 500));
    }

    [TestMethod]
    public async Task DocumentInventoryRejectsInvalidCursorAndUnboundedPages()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        await using MutationScope scope = await fixture.BeginMutationScopeAsync(user);
        IModuleQuotaInventorySource inventory = DocumentInventoryBinding(scope.Services).Adapter;
        foreach (int size in new[] { 0, -1, 501, int.MaxValue })
            await Should.ThrowAsync<ArgumentOutOfRangeException>(() => inventory.ReadPageAsync(null, size));
        foreach (string cursor in new[] { "", "invalid", Guid.Empty.ToString("N"), Guid.NewGuid().ToString("D"), new string('a', 1000) })
            await Should.ThrowAsync<ArgumentException>(() => inventory.ReadPageAsync(cursor, 2));
        ModuleQuotaInventoryPage empty = await inventory.ReadPageAsync(null, 1);
        empty.Entries.ShouldBeEmpty();
        empty.NextCursor.ShouldBeNull();
        empty.IsComplete.ShouldBeTrue();
    }

    [TestMethod]
    public async Task DocumentOperationIdentityIsUniqueWithinOrganizationAndLegacyStaysUnbound()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        ProjectsSeededUser foreign = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        Guid operation = Guid.NewGuid();
        await fixture.SeedInventoryDocumentAsync(user, Guid.NewGuid(), null, 1);
        await fixture.SeedInventoryDocumentAsync(user, Guid.NewGuid(), null, 2);
        await fixture.SeedInventoryDocumentAsync(user, Guid.NewGuid(), operation, 3);
        await fixture.SeedInventoryDocumentAsync(foreign, Guid.NewGuid(), operation, 4);
        PostgresException duplicate = await Should.ThrowAsync<PostgresException>(() =>
            fixture.SeedInventoryDocumentAsync(user, Guid.NewGuid(), operation, 5));
        duplicate.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
        PostgresException empty = await Should.ThrowAsync<PostgresException>(() =>
            fixture.SeedInventoryDocumentAsync(user, Guid.NewGuid(), Guid.Empty, 5));
        empty.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        await using MutationScope scope = await fixture.BeginMutationScopeAsync(user);
        DocumentRecord[] rows = await scope.Services.GetRequiredService<ApplicationDbContext>()
            .Set<DocumentRecord>().AsNoTracking().ToArrayAsync();
        rows.Count(row => row.UploadOperationId is null).ShouldBe(2);
        rows.Single(row => row.UploadOperationId is not null).UploadOperationId.ShouldBe(operation);
    }

    private static ModuleQuotaInventorySourceBinding DocumentInventoryBinding(IServiceProvider services)
    {
        ModuleQuotaInventorySourceBinding? binding = services.GetServices<ModuleQuotaInventorySourceBinding>()
            .SingleOrDefault(binding => binding.Source.OwnerModuleId == "documents");
        binding.ShouldNotBeNull("Documents must register its declared owner inventory source.");
        return binding;
    }

    private sealed partial class ExecutionFixture
    {
        public ApplicationDbContext CreateInventoryOwnerDatabase(MutationScope scope) => new(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(owner).Options,
            scope.Services.GetServices<IApplicationModelContributor>(), scope.OrganizationContext,
            scope.Services.GetRequiredService<ModuleCatalog>());

        public Task SeedInventoryDocumentAsync(ProjectsSeededUser user, Guid id, Guid? operation,
            long? units, int lifecycle = 0, bool metadataOnly = false) => ExecuteOwnerAsync("""
            INSERT INTO app.documents
              ("Id", "OrganizationId", "Title", "Content", "CreatedBy", "CreatedAt", "ArchivedAt", "DeletedAt",
               "ObjectKey", "SizeBytes", "UploadOperationId")
            VALUES (@id, @organization, 'Inventory fixture', '', @actor, CURRENT_TIMESTAMP,
                    CASE WHEN @lifecycle > 0 THEN CURRENT_TIMESTAMP ELSE NULL END,
                    CASE WHEN @lifecycle = 2 THEN CURRENT_TIMESTAMP ELSE NULL END, @key, @units, @operation)
            """, ("id", id), ("organization", user.OrganizationId), ("actor", user.ActorId),
            ("lifecycle", lifecycle), ("key", metadataOnly ? DBNull.Value : (object)$"inventory/{id:N}"),
            ("units", (object?)units ?? DBNull.Value), ("operation", (object?)operation ?? DBNull.Value));
    }
}

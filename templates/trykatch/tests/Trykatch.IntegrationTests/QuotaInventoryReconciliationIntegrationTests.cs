using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Trykatch.Application.Authorization;
using Trykatch.Infrastructure.Quotas;
using Trykatch.Modules;

namespace Trykatch.IntegrationTests;

public sealed partial class ModuleWorkExecutionIntegrationTests
{
    [TestMethod]
    public async Task LegacyResourceWithoutInventoryReferenceKeepsItsExplicitCompleteCheckpoint()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        await fixture.SeedQuotaProjectionAsync(user.OrganizationId, limit: 100);
        await using MutationScope scope = await fixture.BeginMutationScopeAsync(user);
        OrganizationQuotaService quota = ActivatorUtilities.CreateInstance<OrganizationQuotaService>(scope.Services,
            new ModuleCatalog([new LegacyInventoryQuotaModule()]));
        QuotaReservationResult result = await quota.ReserveAsync(DocumentsQuotaResource, 1, Guid.NewGuid());
        result.Disposition.ShouldBe(QuotaReservationDisposition.Reserved);
    }

    private sealed class LegacyInventoryQuotaModule : IModule
    {
        public ModuleDescriptor Descriptor { get; } = new("documents", "Legacy documents", "1.0.0", "Legacy quota declaration.",
            [], [], ModuleCapabilities.Api, [])
        {
            QuotaResources = [new(DocumentsQuotaResource, "Storage", "Uploaded bytes.", ModuleQuotaUnit.Bytes)]
        };
        public void Register(IServiceCollection services, IConfiguration configuration) { }
    }

    [TestMethod]
    public async Task InventoryReconciliationRefusesOverflowAndRollsBackTheNewPage()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        await fixture.SeedQuotaProjectionAsync(user.OrganizationId, limit: 100, inventoryComplete: false);
        Guid retainedEffect = Guid.NewGuid();
        await fixture.SeedHistoricalInventoryEffectAsync(user.OrganizationId, retainedEffect, Guid.NewGuid(), long.MaxValue);
        Guid source = Guid.NewGuid();
        await fixture.SeedInventoryDocumentAsync(user, source, null, 1);
        QuotaInventoryReconciliationResult result = await fixture.ReconcileQuotaInventoryPageAsync(user.OrganizationId, DocumentsQuotaResource);
        result.State.ShouldBe(QuotaInventoryReconciliationState.Unavailable);
        (await fixture.ReadInventoryUsageAsync(user.OrganizationId)).Single().EffectId.ShouldBe(retainedEffect);
        (await fixture.ReadInventoryCheckpointAsync(user.OrganizationId)).State.ShouldBe(0);
    }

    [TestMethod]
    public async Task DocumentInventoryReconciliationCreatesLifecycleEffectsAndCompletesIdempotently()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        await fixture.SeedQuotaProjectionAsync(user.OrganizationId, limit: 10_000, inventoryComplete: false);
        Guid activeId = Guid.NewGuid();
        Guid archivedId = Guid.NewGuid();
        Guid deletedId = Guid.NewGuid();
        Guid operationId = Guid.NewGuid();
        await fixture.SeedInventoryDocumentAsync(user, activeId, null, 11);
        await fixture.SeedInventoryDocumentAsync(user, archivedId, operationId, 13, lifecycle: 1);
        await fixture.SeedInventoryDocumentAsync(user, deletedId, null, 17, lifecycle: 2);
        await fixture.SeedInventoryDocumentAsync(user, Guid.NewGuid(), null, null, metadataOnly: true);

        QuotaInventoryReconciliationResult first = await fixture.ReconcileQuotaInventoryPageAsync(
            user.OrganizationId, DocumentsQuotaResource);
        first.State.ShouldBe(QuotaInventoryReconciliationState.Complete);
        (await fixture.ReadInventoryUsageAsync(user.OrganizationId)).ShouldBe(new[]
        {
            (activeId, activeId, 11L),
            (operationId, archivedId, 13L),
            (deletedId, deletedId, 17L)
        }.OrderBy(row => row.Item1).ToArray());
        InventoryCheckpoint checkpoint = await fixture.ReadInventoryCheckpointAsync(user.OrganizationId);
        checkpoint.SourceKey.ShouldBe("documents.inventory");
        checkpoint.SourceVersion.ShouldBe(1);
        checkpoint.Cursor.ShouldBeNull();
        checkpoint.State.ShouldBe(1);

        QuotaInventoryReconciliationResult repeated = await fixture.ReconcileQuotaInventoryPageAsync(
            user.OrganizationId, DocumentsQuotaResource);
        repeated.State.ShouldBe(QuotaInventoryReconciliationState.Complete);
        (await fixture.ReadInventoryUsageAsync(user.OrganizationId)).Length.ShouldBe(3);
    }

    [TestMethod]
    public async Task DocumentInventoryReconciliationAdvancesInBoundedPages()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        await fixture.SeedQuotaProjectionAsync(user.OrganizationId, limit: 100_000, inventoryComplete: false);
        await fixture.SeedBulkInventoryDocumentsAsync(user, 501);

        QuotaInventoryReconciliationResult first = await fixture.ReconcileQuotaInventoryPageAsync(
            user.OrganizationId, DocumentsQuotaResource);
        first.State.ShouldBe(QuotaInventoryReconciliationState.InProgress);
        (await fixture.CountQuotaRowsAsync("organization_usage", user.OrganizationId)).ShouldBe(500);
        InventoryCheckpoint inProgress = await fixture.ReadInventoryCheckpointAsync(user.OrganizationId);
        inProgress.State.ShouldBe(0);
        inProgress.Cursor.ShouldNotBeNullOrEmpty();

        QuotaInventoryReconciliationResult second = await fixture.ReconcileQuotaInventoryPageAsync(
            user.OrganizationId, DocumentsQuotaResource);
        second.State.ShouldBe(QuotaInventoryReconciliationState.Complete);
        (await fixture.CountQuotaRowsAsync("organization_usage", user.OrganizationId)).ShouldBe(501);
        InventoryCheckpoint complete = await fixture.ReadInventoryCheckpointAsync(user.OrganizationId);
        complete.State.ShouldBe(1);
        complete.Cursor.ShouldBeNull();
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow(0L)]
    [DataRow(-1L)]
    public async Task InvalidUploadedSizeMakesReconciliationUnavailableWithoutPartialProgress(long? size)
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        await fixture.SeedQuotaProjectionAsync(user.OrganizationId, limit: 100, inventoryComplete: false);
        await fixture.SeedInventoryDocumentAsync(user, Guid.NewGuid(), null, 1);
        await fixture.SeedInventoryDocumentAsync(user, Guid.NewGuid(), null, size);

        QuotaInventoryReconciliationResult result = await fixture.ReconcileQuotaInventoryPageAsync(
            user.OrganizationId, DocumentsQuotaResource);

        result.State.ShouldBe(QuotaInventoryReconciliationState.Unavailable);
        (await fixture.ReadInventoryUsageAsync(user.OrganizationId)).ShouldBeEmpty();
        InventoryCheckpoint checkpoint = await fixture.ReadInventoryCheckpointAsync(user.OrganizationId);
        checkpoint.Cursor.ShouldBeNull();
    }

    [TestMethod]
    public async Task ExistingMatchingInventoryEffectIsDeduplicated()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        await fixture.SeedQuotaProjectionAsync(user.OrganizationId, limit: 100, inventoryComplete: false);
        Guid sourceId = Guid.NewGuid();
        Guid operationId = Guid.NewGuid();
        await fixture.SeedInventoryDocumentAsync(user, sourceId, operationId, 7);
        await fixture.SeedHistoricalInventoryEffectAsync(user.OrganizationId, operationId, sourceId, 7);

        QuotaInventoryReconciliationResult result = await fixture.ReconcileQuotaInventoryPageAsync(
            user.OrganizationId, DocumentsQuotaResource);

        result.State.ShouldBe(QuotaInventoryReconciliationState.Complete);
        (await fixture.ReadInventoryUsageAsync(user.OrganizationId)).ShouldBe([(operationId, sourceId, 7)]);
    }

    [TestMethod]
    [DataRow("source")]
    [DataRow("units")]
    public async Task ConflictingHistoricalInventoryEffectRollsBackTheWholePage(string mismatch)
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        await fixture.SeedQuotaProjectionAsync(user.OrganizationId, limit: 100, inventoryComplete: false);
        Guid[] sources = new[] { Guid.NewGuid(), Guid.NewGuid() }.Order().ToArray();
        Guid firstSourceId = sources[0];
        Guid firstOperationId = Guid.Parse("00000000-0000-0000-0000-000000000011");
        Guid conflictSourceId = sources[1];
        Guid conflictOperationId = Guid.Parse("00000000-0000-0000-0000-000000000012");
        Guid differentSourceId = Guid.NewGuid();
        await fixture.SeedInventoryDocumentAsync(user, firstSourceId, firstOperationId, 2);
        await fixture.SeedInventoryDocumentAsync(user, conflictSourceId, conflictOperationId, 5);
        await fixture.SeedHistoricalInventoryEffectAsync(user.OrganizationId, conflictOperationId,
            mismatch == "source" ? differentSourceId : conflictSourceId,
            mismatch == "units" ? 6 : 5);

        QuotaInventoryReconciliationResult result = await fixture.ReconcileQuotaInventoryPageAsync(
            user.OrganizationId, DocumentsQuotaResource);

        result.State.ShouldBe(QuotaInventoryReconciliationState.Unavailable);
        (await fixture.ReadInventoryUsageAsync(user.OrganizationId)).ShouldBe(
            [(conflictOperationId, mismatch == "source" ? differentSourceId : conflictSourceId,
                mismatch == "units" ? 6 : 5)]);
        (await fixture.CountHistoricalEffectAsync(user.OrganizationId, firstOperationId)).ShouldBe(0);
        InventoryCheckpoint checkpoint = await fixture.ReadInventoryCheckpointAsync(user.OrganizationId);
        checkpoint.Cursor.ShouldBeNull();
    }

    [TestMethod]
    public async Task ChangedInventorySourceRestartsAndDeduplicatesExistingEffects()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        await fixture.SeedQuotaProjectionAsync(user.OrganizationId, limit: 100, inventoryComplete: false);
        Guid sourceId = Guid.NewGuid();
        Guid operationId = Guid.NewGuid();
        await fixture.SeedInventoryDocumentAsync(user, sourceId, operationId, 9);
        await fixture.SeedHistoricalInventoryEffectAsync(user.OrganizationId, operationId, sourceId, 9);
        await fixture.SetInventoryCheckpointAsync(user.OrganizationId, "documents.inventory", 2,
            Guid.NewGuid().ToString("N"), (int)QuotaInventoryState.Complete);

        QuotaInventoryReconciliationResult result = await fixture.ReconcileQuotaInventoryPageAsync(
            user.OrganizationId, DocumentsQuotaResource);

        result.State.ShouldBe(QuotaInventoryReconciliationState.Complete);
        (await fixture.ReadInventoryUsageAsync(user.OrganizationId)).ShouldBe([(operationId, sourceId, 9)]);
        InventoryCheckpoint checkpoint = await fixture.ReadInventoryCheckpointAsync(user.OrganizationId);
        checkpoint.SourceKey.ShouldBe("documents.inventory");
        checkpoint.SourceVersion.ShouldBe(1);
        checkpoint.Cursor.ShouldBeNull();
        checkpoint.State.ShouldBe(1);
    }

    [TestMethod]
    public async Task ResourceLockContentionReturnsUnavailableWithoutChangingInventory()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        await fixture.SeedQuotaProjectionAsync(user.OrganizationId, limit: 100, inventoryComplete: false);
        await fixture.SeedInventoryDocumentAsync(user, Guid.NewGuid(), null, 3);
        await using MutationScope scope = await fixture.BeginMutationScopeAsync(user);
        (await CreateQuota(scope).GetAsync(DocumentsQuotaResource)).ShouldNotBeNull();

        Stopwatch stopwatch = Stopwatch.StartNew();
        QuotaInventoryReconciliationResult result = await fixture.ReconcileQuotaInventoryPageAsync(
            user.OrganizationId, DocumentsQuotaResource);
        stopwatch.Stop();

        result.State.ShouldBe(QuotaInventoryReconciliationState.Unavailable);
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
        (await fixture.ReadInventoryUsageAsync(user.OrganizationId)).ShouldBeEmpty();
        InventoryCheckpoint checkpoint = await fixture.ReadInventoryCheckpointAsync(user.OrganizationId);
        checkpoint.Cursor.ShouldBeNull();
        checkpoint.State.ShouldBe(0);
        await scope.CommitAsync();
    }

    [TestMethod]
    public async Task MissingOrganizationPausesInventoryReconciliation()
    {
        QuotaInventoryReconciliationResult result = await fixture.ReconcileQuotaInventoryPageAsync(
            Guid.NewGuid(), DocumentsQuotaResource);

        result.State.ShouldBe(QuotaInventoryReconciliationState.Paused);
    }

    [TestMethod]
    public async Task InactiveOrganizationPausesAndCanResumeReconciliation()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        await fixture.SeedQuotaProjectionAsync(user.OrganizationId, limit: 100, inventoryComplete: false);
        await fixture.SeedInventoryDocumentAsync(user, Guid.NewGuid(), null, 4);
        await fixture.SetOrganizationActiveAsync(user.OrganizationId, false);

        QuotaInventoryReconciliationResult paused = await fixture.ReconcileQuotaInventoryPageAsync(
            user.OrganizationId, DocumentsQuotaResource);
        paused.State.ShouldBe(QuotaInventoryReconciliationState.Paused);
        (await fixture.ReadInventoryUsageAsync(user.OrganizationId)).ShouldBeEmpty();

        await fixture.SetOrganizationActiveAsync(user.OrganizationId, true);
        QuotaInventoryReconciliationResult resumed = await fixture.ReconcileQuotaInventoryPageAsync(
            user.OrganizationId, DocumentsQuotaResource);
        resumed.State.ShouldBe(QuotaInventoryReconciliationState.Complete);
        (await fixture.ReadInventoryUsageAsync(user.OrganizationId)).Length.ShouldBe(1);
    }

    [TestMethod]
    public async Task InventoryEffectsAndCheckpointRemainOrganizationScoped()
    {
        ProjectsSeededUser first = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        ProjectsSeededUser second = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        await fixture.SeedQuotaProjectionAsync(first.OrganizationId, limit: 100, inventoryComplete: false);
        await fixture.SeedQuotaProjectionAsync(second.OrganizationId, limit: 100, inventoryComplete: false);
        await fixture.SeedInventoryDocumentAsync(first, Guid.NewGuid(), null, 6);
        await fixture.SeedInventoryDocumentAsync(second, Guid.NewGuid(), null, 8);

        QuotaInventoryReconciliationResult result = await fixture.ReconcileQuotaInventoryPageAsync(
            first.OrganizationId, DocumentsQuotaResource);

        result.State.ShouldBe(QuotaInventoryReconciliationState.Complete);
        (await fixture.ReadInventoryUsageAsync(first.OrganizationId)).Length.ShouldBe(1);
        (await fixture.ReadInventoryUsageAsync(second.OrganizationId)).ShouldBeEmpty();
        (await fixture.ReadInventoryCheckpointAsync(second.OrganizationId)).State.ShouldBe(0);
    }

    [TestMethod]
    public async Task CompleteCheckpointWithStaleSourceVersionDoesNotPermitReservation()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        await fixture.SeedQuotaProjectionAsync(user.OrganizationId, limit: 100);
        await fixture.SetInventoryCheckpointAsync(user.OrganizationId, "documents.inventory", 2,
            cursor: null, state: 1);
        await using MutationScope scope = await fixture.BeginMutationScopeAsync(user);

        QuotaReservationResult result = await CreateQuota(scope).ReserveAsync(
            DocumentsQuotaResource, 1, Guid.NewGuid());

        result.Disposition.ShouldBe(QuotaReservationDisposition.Unavailable);
        result.FailureCode.ShouldBe("quota_inventory_initializing");
        result.Reservation.ShouldBeNull();
        await scope.CommitAsync();
        (await fixture.CountQuotaRowsAsync("organization_quota_reservations", user.OrganizationId)).ShouldBe(0);
    }

    private sealed record InventoryCheckpoint(string? SourceKey, int? SourceVersion, string? Cursor, int State);

    private sealed partial class ExecutionFixture
    {
        public async Task<QuotaInventoryReconciliationResult> ReconcileQuotaInventoryPageAsync(
            Guid organizationId,
            string resourceKey,
            CancellationToken cancellationToken = default)
        {
            await using AsyncServiceScope scope = services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<QuotaInventoryReconciler>()
                .ReconcilePageAsync(organizationId, resourceKey, cancellationToken);
        }

        public Task SeedBulkInventoryDocumentsAsync(ProjectsSeededUser user, int count) => ExecuteOwnerAsync("""
            INSERT INTO app.documents
                ("Id", "OrganizationId", "Title", "Content", "CreatedBy", "CreatedAt", "ArchivedAt", "DeletedAt",
                 "ObjectKey", "SizeBytes", "UploadOperationId")
            SELECT gen_random_uuid(), @organization, 'Bulk inventory', '', @actor, CURRENT_TIMESTAMP,
                   NULL, NULL, 'bulk/' || sequence::text, 1, NULL
            FROM generate_series(1, @count) AS series(sequence)
            """, ("organization", user.OrganizationId), ("actor", user.ActorId), ("count", count));

        public Task SeedHistoricalInventoryEffectAsync(Guid organizationId, Guid effectId, Guid sourceId, long units) => ExecuteOwnerAsync("""
            INSERT INTO app.organization_usage
                ("OrganizationId", "ResourceKey", "EffectId", "SourceId", "SourceEffectId", "ReservationId", "ActorId", "Units", "OccurredAtUtc")
            VALUES (@organization, 'documents.storage-bytes', @effect, @source, NULL, NULL, @actor, @units, CURRENT_TIMESTAMP)
            """, ("organization", organizationId), ("effect", effectId), ("source", sourceId),
            ("actor", Guid.NewGuid()), ("units", units));

        public async Task<(Guid EffectId, Guid SourceId, long Units)[]> ReadInventoryUsageAsync(Guid organizationId)
        {
            await using NpgsqlConnection connection = new(owner);
            await connection.OpenAsync();
            await using NpgsqlCommand command = new("""
                SELECT "EffectId", "SourceId", "Units" FROM app.organization_usage
                WHERE "OrganizationId"=@organization AND "ResourceKey"='documents.storage-bytes'
                ORDER BY "EffectId"
                """, connection);
            command.Parameters.AddWithValue("organization", organizationId);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
            List<(Guid EffectId, Guid SourceId, long Units)> rows = [];
            while (await reader.ReadAsync())
                rows.Add((reader.GetGuid(0), reader.GetGuid(1), reader.GetInt64(2)));
            return rows.ToArray();
        }

        public Task<long> CountHistoricalEffectAsync(Guid organizationId, Guid effectId) => CountOwnerAsync("""
            SELECT count(*) FROM app.organization_usage
            WHERE "OrganizationId"=@organization AND "ResourceKey"='documents.storage-bytes' AND "EffectId"=@effect
            """, ("organization", organizationId), ("effect", effectId));

        public async Task<InventoryCheckpoint> ReadInventoryCheckpointAsync(Guid organizationId)
        {
            await using NpgsqlConnection connection = new(owner);
            await connection.OpenAsync();
            await using NpgsqlCommand command = new("""
                SELECT "SourceKey", "SourceVersion", "Cursor", "State"
                FROM app.organization_quota_inventory
                WHERE "OrganizationId"=@organization AND "ResourceKey"='documents.storage-bytes'
                """, connection);
            command.Parameters.AddWithValue("organization", organizationId);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).ShouldBeTrue();
            return new(
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetInt32(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetInt32(3));
        }

        public Task SetInventoryCheckpointAsync(Guid organizationId, string? sourceKey, int? sourceVersion,
            string? cursor, int state) => ExecuteOwnerAsync("""
            UPDATE app.organization_quota_inventory
            SET "SourceKey"=@sourceKey, "SourceVersion"=@sourceVersion, "Cursor"=@cursor,
                "State"=@state, "UpdatedAtUtc"=CURRENT_TIMESTAMP
            WHERE "OrganizationId"=@organization AND "ResourceKey"='documents.storage-bytes'
            """, ("organization", organizationId), ("sourceKey", (object?)sourceKey ?? DBNull.Value),
            ("sourceVersion", (object?)sourceVersion ?? DBNull.Value), ("cursor", (object?)cursor ?? DBNull.Value),
            ("state", state));
    }
}

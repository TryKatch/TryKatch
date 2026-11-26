using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Trykatch.Application.Authorization;
using Trykatch.Application.Organizations;
using Trykatch.Application.Quotas;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Infrastructure.Quotas;
using Trykatch.Modules;

namespace Trykatch.IntegrationTests;

public sealed partial class ModuleWorkExecutionIntegrationTests
{
    [TestMethod]
    public async Task QuotaPublicationDefaultPublishesCompleteDisabledBatchOutsideTheCallerTransaction()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        await using (MutationScope scope = await fixture.BeginMutationScopeAsync(user))
        {
            PublicationHarness publication = CreatePublication(scope);
            (await publication.EnsureAsync()).Revision.ShouldBe(0);
            (await publication.EnsureAsync()).Revision.ShouldBe(0);
            // The caller intentionally rolls back. Configuration publication is already durable.
        }
        PublicationRow[] rows = await fixture.ReadPublicationRowsAsync(user.OrganizationId);
        rows.Select(row => row.Resource).ShouldBe([DocumentsQuotaResource, ProbeQuotaResource]);
        rows.All(row => row.Mode == OrganizationEntitlementMode.Disabled && row.Limit == null && row.Revision == 0).ShouldBeTrue();
        (await fixture.CountPublicationHeadersAsync(user.OrganizationId)).ShouldBe(1);
    }

    [TestMethod]
    public async Task QuotaPublicationEnforcedBatchSeparatesFiniteUnlimitedAndMissingRules()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        await using MutationScope scope = await fixture.BeginMutationScopeAsync(user);
        await CreatePublication(scope, revision: 4, limit: 0, probeUnlimited: true).EnsureAsync();
        PublicationRow[] rows = await fixture.ReadPublicationRowsAsync(user.OrganizationId);
        rows.Single(row => row.Resource == DocumentsQuotaResource).Mode.ShouldBe(OrganizationEntitlementMode.Finite);
        rows.Single(row => row.Resource == DocumentsQuotaResource).Limit.ShouldBe(0);
        rows.Single(row => row.Resource == ProbeQuotaResource).Mode.ShouldBe(OrganizationEntitlementMode.Unlimited);
        rows.Single(row => row.Resource == ProbeQuotaResource).Limit.ShouldBeNull();

        ProjectsSeededUser missing = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        await using MutationScope missingScope = await fixture.BeginMutationScopeAsync(missing);
        await CreatePublication(missingScope, revision: 1).EnsureAsync();
        (await fixture.ReadPublicationRowsAsync(missing.OrganizationId))
            .All(row => row.Mode == OrganizationEntitlementMode.Unconfigured && row.Limit == null).ShouldBeTrue();
    }

    [TestMethod]
    public async Task QuotaPublicationOlderNodeUsesNewerDurableRevisionWithoutOverwritingOrBackfilling()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        await using MutationScope newer = await fixture.BeginMutationScopeAsync(user);
        await CreatePublication(newer, revision: 8, limit: 9).EnsureAsync();
        PublicationRow[] before = await fixture.ReadPublicationRowsAsync(user.OrganizationId);
        await using MutationScope older = await fixture.BeginMutationScopeAsync(user);
        QuotaPublicationReceipt receipt = await CreatePublication(older, revision: 2, limit: 1000).EnsureAsync();
        receipt.Revision.ShouldBe(8);
        (await fixture.ReadPublicationRowsAsync(user.OrganizationId)).ShouldBe(before);
        (await fixture.CountPublicationHeadersAsync(user.OrganizationId)).ShouldBe(1);
    }

    [TestMethod]
    public async Task QuotaPublicationSameRevisionDifferentContentFailsClosedWithoutPartialWrites()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        await using MutationScope first = await fixture.BeginMutationScopeAsync(user);
        await CreatePublication(first, revision: 3, limit: 10).EnsureAsync();
        PublicationRow[] before = await fixture.ReadPublicationRowsAsync(user.OrganizationId);
        await using MutationScope conflict = await fixture.BeginMutationScopeAsync(user);
        await Should.ThrowAsync<OrganizationQuotaUnavailableException>(() => CreatePublication(conflict, revision: 3, limit: 11).EnsureAsync());
        (await fixture.ReadPublicationRowsAsync(user.OrganizationId)).ShouldBe(before);
        (await fixture.CountPublicationHeadersAsync(user.OrganizationId)).ShouldBe(1);
    }

    [TestMethod]
    public async Task QuotaPublicationHigherRevisionAdvancesEveryRetainedResourceEvenWithIdenticalLimits()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        await using MutationScope first = await fixture.BeginMutationScopeAsync(user);
        await CreatePublication(first, revision: 1, limit: 10).EnsureAsync();
        PublicationRow[] before = await fixture.ReadPublicationRowsAsync(user.OrganizationId);
        await using MutationScope next = await fixture.BeginMutationScopeAsync(user);
        ModuleCatalog smaller = new(next.Services.GetRequiredService<ModuleCatalog>().Modules
            .Where(module => module.Descriptor.Id != "probe"));
        await CreatePublication(next, revision: 2, limit: 10, catalogOverride: smaller).EnsureAsync();
        PublicationRow[] after = await fixture.ReadPublicationRowsAsync(user.OrganizationId);
        after.All(row => row.Revision == 2).ShouldBeTrue();
        after.Select(row => row.Hash).ShouldBe(before.Select(row => row.Hash));
        after.Select(row => row.Version).Intersect(before.Select(row => row.Version)).ShouldBeEmpty();
        (await fixture.CountPublicationHeadersAsync(user.OrganizationId)).ShouldBe(2);
    }

    [TestMethod]
    public async Task QuotaPublicationBusyLastResourceRollsBackTheWholeSortedBatch()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        await using MutationScope first = await fixture.BeginMutationScopeAsync(user);
        await CreatePublication(first, revision: 1, limit: 10).EnsureAsync();
        PublicationRow[] before = await fixture.ReadPublicationRowsAsync(user.OrganizationId);
        await using MutationScope locked = await fixture.BeginMutationScopeAsync(user);
        (await QuotaResourceLock.TryAcquireAsync(locked.Services.GetRequiredService<ApplicationDbContext>(),
            user.OrganizationId, ProbeQuotaResource, CancellationToken.None)).ShouldBeTrue();
        await using MutationScope publisher = await fixture.BeginMutationScopeAsync(user);
        await Should.ThrowAsync<OrganizationQuotaUnavailableException>(() => CreatePublication(publisher, revision: 2, limit: 1).EnsureAsync());
        (await fixture.ReadPublicationRowsAsync(user.OrganizationId)).ShouldBe(before);
        (await fixture.CountPublicationHeadersAsync(user.OrganizationId)).ShouldBe(1);
    }

    [TestMethod]
    public async Task QuotaPublicationNoopDoesNotTakeAResourceLockHeldByAnotherTransaction()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        await using MutationScope first = await fixture.BeginMutationScopeAsync(user);
        await CreatePublication(first, revision: 1, limit: 10).EnsureAsync();
        await using MutationScope locked = await fixture.BeginMutationScopeAsync(user);
        (await QuotaResourceLock.TryAcquireAsync(locked.Services.GetRequiredService<ApplicationDbContext>(),
            user.OrganizationId, DocumentsQuotaResource, CancellationToken.None)).ShouldBeTrue();
        await using MutationScope noop = await fixture.BeginMutationScopeAsync(user);
        (await CreatePublication(noop, revision: 1, limit: 10).EnsureAsync()).Revision.ShouldBe(1);
    }

    [TestMethod]
    public async Task QuotaPublicationSameRevisionInitializesNewResourcesWithoutRewritingRetainedRows()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        await using MutationScope first = await fixture.BeginMutationScopeAsync(user);
        ModuleCatalog smaller = new(first.Services.GetRequiredService<ModuleCatalog>().Modules
            .Where(module => module.Descriptor.Id != "probe"));
        await CreatePublication(first, revision: 1, limit: 10, catalogOverride: smaller).EnsureAsync();
        PublicationRow original = (await fixture.ReadPublicationRowsAsync(user.OrganizationId)).Single();
        await using MutationScope expanded = await fixture.BeginMutationScopeAsync(user);
        await CreatePublication(expanded, revision: 1, limit: 10).EnsureAsync();
        PublicationRow[] rows = await fixture.ReadPublicationRowsAsync(user.OrganizationId);
        rows.Single(row => row.Resource == DocumentsQuotaResource).ShouldBe(original);
        rows.Single(row => row.Resource == ProbeQuotaResource).Mode.ShouldBe(OrganizationEntitlementMode.Unconfigured);
        await using MutationScope reduced = await fixture.BeginMutationScopeAsync(user);
        await CreatePublication(reduced, revision: 1, limit: 10, catalogOverride: smaller).EnsureAsync();
        (await fixture.ReadPublicationRowsAsync(user.OrganizationId)).ShouldBe(rows);
        (await fixture.CountPublicationHeadersAsync(user.OrganizationId)).ShouldBe(1);
    }

    [TestMethod]
    public async Task QuotaPublicationEmptyCatalogRetainsItsRevisionBeforeModulesAreInstalled()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        await using MutationScope first = await fixture.BeginMutationScopeAsync(user);
        await CreatePublication(first, revision: 5, catalogOverride: new ModuleCatalog([])).EnsureAsync();
        (await fixture.ReadPublicationRowsAsync(user.OrganizationId)).ShouldBeEmpty();
        await using MutationScope older = await fixture.BeginMutationScopeAsync(user);
        (await CreatePublication(older, revision: 1).EnsureAsync()).Revision.ShouldBe(5);
        (await fixture.ReadPublicationRowsAsync(user.OrganizationId)).ShouldBeEmpty();
        await using MutationScope current = await fixture.BeginMutationScopeAsync(user);
        await CreatePublication(current, revision: 5).EnsureAsync();
        (await fixture.ReadPublicationRowsAsync(user.OrganizationId)).Length.ShouldBe(2);
        (await fixture.CountPublicationHeadersAsync(user.OrganizationId)).ShouldBe(1);
    }

    [TestMethod]
    public async Task QuotaPublicationRejectsForgedAuthorityAndACompletedParentTransaction()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        await using MutationScope scope = await fixture.BeginMutationScopeAsync(user);
        PublicationHarness publication = CreatePublication(scope);
        await Should.ThrowAsync<InvalidOperationException>(() => publication.Publisher.PublishAsync(new ForgedPublication()));
        await publication.EnsureAsync();
        await scope.CommitAsync();
        await Should.ThrowAsync<InvalidOperationException>(() => publication.EnsureAsync());
    }

    [TestMethod]
    public async Task QuotaPublicationCannotUseOwnerCredentialsAsItsPlatformConnection()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        await using MutationScope scope = await fixture.BeginMutationScopeAsync(user);
        await Should.ThrowAsync<OrganizationQuotaUnavailableException>(() => CreatePublication(scope,
            platformOverride: fixture.OwnerConnectionForPublication).EnsureAsync());
        (await fixture.CountQuotaRowsAsync("organization_entitlements", user.OrganizationId)).ShouldBe(0);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task QuotaPublicationWrongTargetOrDatabaseOutageNeverUsesALocalFallback(bool unavailable)
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        await using MutationScope scope = await fixture.BeginMutationScopeAsync(user);
        NpgsqlConnectionStringBuilder connection = new(scope.Services.GetRequiredService<IConfiguration>()
            .GetConnectionString(RuntimeDatabaseConnectionContract.Platform));
        if (unavailable) connection.Port = 1;
        else connection.Database = "template1";
        connection.Timeout = 1;
        await Should.ThrowAsync<OrganizationQuotaUnavailableException>(() => CreatePublication(scope,
            platformOverride: connection.ConnectionString).EnsureAsync());
        (await fixture.CountQuotaRowsAsync("organization_entitlements", user.OrganizationId)).ShouldBe(0);
    }

    [TestMethod]
    public async Task QuotaPublicationParallelNodesPublishOneCompleteRevisionAndRetrySafely()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        await using MutationScope first = await fixture.BeginMutationScopeAsync(user);
        await using MutationScope second = await fixture.BeginMutationScopeAsync(user);
        PublicationHarness left = CreatePublication(first, revision: 1, limit: 10);
        PublicationHarness right = CreatePublication(second, revision: 1, limit: 10);
        static async Task<bool> AttemptAsync(PublicationHarness publisher)
        {
            try { await publisher.EnsureAsync(); return true; }
            catch (OrganizationQuotaUnavailableException) { return false; }
        }
        bool[] outcomes = await Task.WhenAll(AttemptAsync(left), AttemptAsync(right));
        outcomes.Any(outcome => outcome).ShouldBeTrue();
        await left.EnsureAsync();
        await right.EnsureAsync();
        (await fixture.CountPublicationHeadersAsync(user.OrganizationId)).ShouldBe(1);
        (await fixture.ReadPublicationRowsAsync(user.OrganizationId)).Length.ShouldBe(2);
    }

    [TestMethod]
    public async Task QuotaPublicationEnginePublishesOnceBeforeMultipleResourceLocksInTheSameTransaction()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        await using MutationScope scope = await fixture.BeginMutationScopeAsync(user);
        scope.Services.GetRequiredService<IOrganizationQuota>().ShouldBeSameAs(
            scope.Services.GetRequiredService<IOrganizationEntitlementReader>());
        OrganizationQuotaService quota = CreatePublishedQuota(scope, CreatePublication(scope));
        (await quota.ReserveAsync(DocumentsQuotaResource, 2, Guid.NewGuid())).Disposition.ShouldBe(QuotaReservationDisposition.Reserved);
        (await quota.ReserveAsync(ProbeQuotaResource, 3, Guid.NewGuid())).Disposition.ShouldBe(QuotaReservationDisposition.Reserved);
        await scope.CommitAsync();
        (await fixture.CountPublicationHeadersAsync(user.OrganizationId)).ShouldBe(1);
    }

    [TestMethod]
    public async Task QuotaPublicationEngineRereadsNewerDowngradeAfterItsReceiptAndPreservesAdmittedRetry()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        Guid operation = Guid.NewGuid();
        Guid reservation;
        await using (MutationScope admission = await fixture.BeginMutationScopeAsync(user))
        {
            PublicationHarness source = CreatePublication(admission, revision: 1, limit: 10);
            await source.EnsureAsync();
            await fixture.CompletePublishedInventoryAsync(user.OrganizationId);
            OrganizationQuotaService quota = CreatePublishedQuota(admission, source);
            QuotaReservationResult hold = await quota.ReserveAsync(DocumentsQuotaResource, 8, operation);
            hold.Disposition.ShouldBe(QuotaReservationDisposition.Reserved);
            reservation = hold.Reservation!.Id;
            await admission.CommitAsync();
        }
        await using MutationScope staleParent = await fixture.BeginMutationScopeAsync(user);
        PublicationHarness old = CreatePublication(staleParent, revision: 1, limit: 10);
        await old.EnsureAsync();
        await using (MutationScope downgrade = await fixture.BeginMutationScopeAsync(user))
            await CreatePublication(downgrade, revision: 2, limit: 0).EnsureAsync();
        OrganizationQuotaService staleQuota = CreatePublishedQuota(staleParent, old);
        (await staleQuota.ReserveAsync(DocumentsQuotaResource, 1, Guid.NewGuid())).FailureCode.ShouldBe("quota_exhausted");
        QuotaReservationResult retry = await staleQuota.ReserveAsync(DocumentsQuotaResource, 8, operation);
        retry.Disposition.ShouldBe(QuotaReservationDisposition.AlreadyReserved);
        retry.Reservation!.Id.ShouldBe(reservation);
        await staleQuota.CommitAsync(reservation);
        await staleParent.CommitAsync();
        (await fixture.SumQuotaUsageAsync(user.OrganizationId)).ShouldBe(8);
    }

    [TestMethod]
    public async Task QuotaPublicationHistoryIsScopedAppendOnlyAndRejectsForgedActorOrTarget()
    {
        ProjectsSeededUser first = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        ProjectsSeededUser other = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        await using MutationScope scope = await fixture.BeginMutationScopeAsync(first);
        await CreatePublication(scope).EnsureAsync();
        await using (MutationScope second = await fixture.BeginMutationScopeAsync(other))
            await CreatePublication(second).EnsureAsync();
        string platform = scope.Services.GetRequiredService<IConfiguration>()
            .GetConnectionString(RuntimeDatabaseConnectionContract.Platform)!;
        foreach ((Guid? target, Guid? actor, long expected) in new (Guid?, Guid?, long)[]
        {
            (first.OrganizationId, first.ActorId, 1), (other.OrganizationId, other.ActorId, 1),
            (first.OrganizationId, null, 0), (null, first.ActorId, 0), (first.OrganizationId, Guid.Empty, 0)
        })
        {
            await using NpgsqlConnection connection = new(platform);
            await connection.OpenAsync();
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
            await SetPublicationScopeAsync(connection, transaction, target, actor);
            await using NpgsqlCommand read = new("SELECT count(*) FROM platform.entitlement_publication_history", connection, transaction);
            (await read.ExecuteScalarAsync()).ShouldBe(expected);
        }
        foreach (string sql in new[]
        {
            "UPDATE platform.entitlement_publication_history SET \"SourceRevision\" = 99",
            "DELETE FROM platform.entitlement_publication_history",
            """
            INSERT INTO platform.entitlement_publication_history
                ("OrganizationId", "SourceRevision", "SourceHash", "Source", "SourceVersion", "ActorId", "InitialResourceCount", "PublishedAtUtc")
            VALUES (@foreign, 1, @hash, 'host-configuration', 1, @actor, 0, CURRENT_TIMESTAMP)
            """,
            """
            INSERT INTO platform.entitlement_publication_history
                ("OrganizationId", "SourceRevision", "SourceHash", "Source", "SourceVersion", "ActorId", "InitialResourceCount", "PublishedAtUtc")
            VALUES (@target, 1, @hash, 'host-configuration', 1, @wrong_actor, 0, CURRENT_TIMESTAMP)
            """
        })
        {
            await using NpgsqlConnection connection = new(platform);
            await connection.OpenAsync();
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
            await SetPublicationScopeAsync(connection, transaction, first.OrganizationId, first.ActorId);
            await using NpgsqlCommand denied = new(sql, connection, transaction);
            denied.Parameters.AddWithValue("foreign", other.OrganizationId);
            denied.Parameters.AddWithValue("target", first.OrganizationId);
            denied.Parameters.AddWithValue("actor", first.ActorId);
            denied.Parameters.AddWithValue("wrong_actor", Guid.NewGuid());
            denied.Parameters.AddWithValue("hash", new string('a', 64));
            (await Should.ThrowAsync<PostgresException>(() => denied.ExecuteNonQueryAsync())).SqlState
                .ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        }
        string organization = scope.Services.GetRequiredService<IConfiguration>()
            .GetConnectionString(RuntimeDatabaseConnectionContract.Organization)!;
        await using NpgsqlConnection org = new(organization);
        await org.OpenAsync();
        await using NpgsqlCommand hidden = new("SELECT * FROM platform.entitlement_publication_history", org);
        (await Should.ThrowAsync<PostgresException>(() => hidden.ExecuteNonQueryAsync())).SqlState
            .ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    [TestMethod]
    public async Task QuotaPublicationHeaderlessProjectionCannotBeSilentlyDowngradedToDisabled()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        await fixture.SeedQuotaProjectionAsync(user.OrganizationId, limit: 0);
        await fixture.RemovePublicationHistoryAsync(user.OrganizationId);
        await using MutationScope scope = await fixture.BeginMutationScopeAsync(user);
        QuotaReservationResult result = await CreateQuota(scope).ReserveAsync(DocumentsQuotaResource, 1, Guid.NewGuid());
        result.Disposition.ShouldBe(QuotaReservationDisposition.Unavailable);
        result.Reservation.ShouldBeNull();
        (await fixture.ReadPublicationRowsAsync(user.OrganizationId)).Single().Limit.ShouldBe(0);
        (await fixture.CountPublicationHeadersAsync(user.OrganizationId)).ShouldBe(0);
    }

    [TestMethod]
    public async Task QuotaPublicationReceiptIsRenewedWhenTheActualTransactionChangesWithinAScope()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        await using MutationScope scope = await fixture.BeginMutationScopeAsync(user);
        PublicationHarness source = CreatePublication(scope, revision: 1);
        (await source.EnsureAsync()).Revision.ShouldBe(1);
        await scope.CommitAsync();
        ApplicationDbContext application = scope.Services.GetRequiredService<ApplicationDbContext>();
        OrganizationControlPlaneDbContext control = scope.Services.GetRequiredService<OrganizationControlPlaneDbContext>();
        await application.Database.CurrentTransaction!.DisposeAsync();
        if (control.Database.CurrentTransaction is { } previous) await previous.DisposeAsync();
        await using (MutationScope newer = await fixture.BeginMutationScopeAsync(user))
            await CreatePublication(newer, revision: 2).EnsureAsync();
        await using IOrganizationDataScope next = await scope.Services.GetRequiredService<IOrganizationDataScopeFactory>()
            .BeginAsync(user.OrganizationId, user.ActorId, CancellationToken.None);
        await using var enlistment = await OrganizationTransactionEnlistment.EnlistAsync(
            control, application, user.OrganizationId, user.ActorId, CancellationToken.None);
        (await source.EnsureAsync()).Revision.ShouldBe(2);
        await next.CommitAsync(CancellationToken.None);
    }

    private static async Task SetPublicationScopeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid? organization, Guid? actor)
    {
        await using NpgsqlCommand scope = new("""
            SELECT set_config('app.organization_id', @organization, true), set_config('app.actor_id', @actor, true)
            """, connection, transaction);
        scope.Parameters.AddWithValue("organization", organization?.ToString() ?? string.Empty);
        scope.Parameters.AddWithValue("actor", actor?.ToString() ?? string.Empty);
        await scope.ExecuteNonQueryAsync();
    }

    private sealed class ForgedPublication : EntitlementPublication;
    private sealed record PublicationRow(string Resource, OrganizationEntitlementMode Mode, long? Limit,
        long Revision, string Hash, Guid Version);
    private sealed record PublicationHarness(QuotaPublicationAuthority Authority, HostEntitlementPublisher Publisher)
    {
        internal Task<QuotaPublicationReceipt> EnsureAsync() => Authority.EnsurePublishedAsync(Publisher, CancellationToken.None);
    }

    private static OrganizationQuotaService CreatePublishedQuota(MutationScope scope, PublicationHarness source) =>
        ActivatorUtilities.CreateInstance<OrganizationQuotaService>(scope.Services, source.Authority, source.Publisher);

    private static PublicationHarness CreatePublication(MutationScope scope, long revision = 0, long? limit = null,
        bool probeUnlimited = false, string? platformOverride = null, ModuleCatalog? catalogOverride = null)
    {
        Dictionary<string, string?> entries = new()
        {
            ["Quotas:EnforcementEnabled"] = (revision > 0).ToString(CultureInfo.InvariantCulture),
            ["Quotas:Revision"] = revision.ToString(CultureInfo.InvariantCulture)
        };
        if (limit.HasValue) entries[$"Quotas:Resources:{DocumentsQuotaResource}:Limit"] = limit.Value.ToString(CultureInfo.InvariantCulture);
        if (probeUnlimited) entries[$"Quotas:Resources:{ProbeQuotaResource}:Unlimited"] = "true";
        if (platformOverride is not null) entries[$"ConnectionStrings:{RuntimeDatabaseConnectionContract.Platform}"] = platformOverride;
        IConfiguration configuration = new ConfigurationBuilder()
            .AddConfiguration(scope.Services.GetRequiredService<IConfiguration>()).AddInMemoryCollection(entries).Build();
        ModuleCatalog catalog = catalogOverride ?? scope.Services.GetRequiredService<ModuleCatalog>();
        QuotaSettings settings = QuotaOptions.CreateSettings(configuration, catalog);
        QuotaPublicationAuthority authority = new(scope.Services.GetRequiredService<ApplicationDbContext>(),
            scope.Services.GetRequiredService<OrganizationControlPlaneDbContext>(), scope.OrganizationContext, settings);
        return new(authority, new(authority, configuration, catalog, TimeProvider.System));
    }

    private sealed partial class ExecutionFixture
    {
        public string OwnerConnectionForPublication => owner;
        public Task RemovePublicationHistoryAsync(Guid organizationId) => ExecuteOwnerAsync("""
            DELETE FROM platform.entitlement_publication_history WHERE "OrganizationId" = @organization
            """, ("organization", organizationId));
        public Task CompletePublishedInventoryAsync(Guid organizationId) => ExecuteOwnerAsync("""
            INSERT INTO app.organization_quota_inventory
                ("OrganizationId", "ResourceKey", "SourceKey", "SourceVersion", "Cursor", "State", "UpdatedAtUtc")
            VALUES (@organization, 'documents.storage-bytes', 'documents.inventory', 1, NULL, 1, CURRENT_TIMESTAMP)
            """, ("organization", organizationId));
        public Task<long> CountPublicationHeadersAsync(Guid organizationId) => CountOwnerAsync("""
            SELECT count(*) FROM platform.entitlement_publication_history WHERE "OrganizationId" = @organization
            """, ("organization", organizationId));

        public async Task<PublicationRow[]> ReadPublicationRowsAsync(Guid organizationId)
        {
            await using NpgsqlConnection connection = new(owner);
            await connection.OpenAsync();
            await using NpgsqlCommand command = new("""
                SELECT "ResourceKey", "Mode", "Limit", "SourceRevision", "SourceHash", "Version"
                FROM app.organization_entitlements WHERE "OrganizationId" = @organization ORDER BY "ResourceKey"
                """, connection);
            command.Parameters.AddWithValue("organization", organizationId);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
            List<PublicationRow> rows = [];
            while (await reader.ReadAsync())
                rows.Add(new(reader.GetString(0), (OrganizationEntitlementMode)reader.GetInt32(1),
                    reader.IsDBNull(2) ? null : reader.GetInt64(2), reader.GetInt64(3), reader.GetString(4), reader.GetGuid(5)));
            return rows.ToArray();
        }
    }
}

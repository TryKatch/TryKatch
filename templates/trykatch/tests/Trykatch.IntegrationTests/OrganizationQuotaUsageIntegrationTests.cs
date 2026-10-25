using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Trykatch.Application.Authorization;
using Trykatch.Application.Quotas;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Infrastructure.Quotas;
using Trykatch.Modules;

namespace Trykatch.IntegrationTests;

public sealed partial class ModuleWorkExecutionIntegrationTests
{
    [TestMethod]
    public async Task QuotaUsageInspectionAllowsEitherReadOrManageAndPreservesInt64AndOrganizationIsolation()
    {
        foreach (string permission in new[] { Permissions.OrganizationsRead, Permissions.OrganizationsManage })
        {
            ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([permission]);
            ProjectsSeededUser other = await fixture.SeedProjectsUserAsync([Permissions.OrganizationsRead]);
            await fixture.SeedQuotaProjectionAsync(user.OrganizationId, long.MaxValue);
            await fixture.SeedQuotaUsageAsync(user.OrganizationId, long.MaxValue);
            await fixture.SeedQuotaProjectionAsync(other.OrganizationId, 7);
            await fixture.SeedQuotaUsageAsync(other.OrganizationId, 6);
            await using MutationScope scope = await fixture.BeginMutationScopeAsync(user);

            var result = await CreateUsageReader(scope).GetAsync();

            result.IsSuccess.ShouldBeTrue();
            OrganizationQuotaUsageResource resource = result.Value!.Resources.Single(row => row.ResourceKey == DocumentsQuotaResource);
            resource.State.ShouldBe("Ready");
            resource.Unit.ShouldBe("Bytes");
            resource.Limit.ShouldBe("9223372036854775807");
            resource.Committed.ShouldBe("9223372036854775807");
            resource.Reserved.ShouldBe("0");
            resource.ReconciliationHeld.ShouldBe("0");
            resource.Name.ShouldBe("Document storage");
            resource.Source.ShouldBe("host-configuration");
            resource.Version.ShouldNotBeNull();
            resource.Installed.ShouldBeTrue();
        }
    }

    [TestMethod]
    public async Task QuotaUsageInspectionDeniesMissingPermissionBeforePublishingOrQueuing()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        await using MutationScope scope = await fixture.BeginMutationScopeAsync(user);
        var result = await CreateUsageReader(scope).GetAsync();
        result.IsSuccess.ShouldBeFalse();
        result.ErrorCode.ShouldBe("forbidden");
        result.Value.ShouldBeNull();
        (await fixture.CountPublicationHeadersAsync(user.OrganizationId)).ShouldBe(0);
        (await fixture.CountUsageInventoryWorkAsync(user.OrganizationId)).ShouldBe(0);
    }

    [TestMethod]
    public async Task QuotaUsageInspectionRechecksMembershipAndActualOrganizationFrame()
    {
        ProjectsSeededUser wrong = await fixture.SeedProjectsUserAsync([Permissions.OrganizationsRead]);
        await using (MutationScope stale = await fixture.BeginMutationScopeAsync(wrong, Guid.NewGuid()))
            (await CreateUsageReader(stale).GetAsync()).ErrorCode.ShouldBe("forbidden");
        ProjectsSeededUser revoked = await fixture.SeedProjectsUserAsync([Permissions.OrganizationsRead]);
        await using (MutationScope scope = await fixture.BeginMutationScopeAsync(revoked))
        {
            await fixture.SuspendMembershipAsync(revoked.MembershipId);
            (await CreateUsageReader(scope).GetAsync()).ErrorCode.ShouldBe("forbidden");
        }
        ProjectsSeededUser framed = await fixture.SeedProjectsUserAsync([Permissions.OrganizationsRead]);
        await using MutationScope changed = await fixture.BeginMutationScopeAsync(framed);
        await changed.Services.GetRequiredService<ApplicationDbContext>().Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.organization_id', {Guid.NewGuid().ToString()}, true)");
        await Should.ThrowAsync<InvalidOperationException>(() => CreateUsageReader(changed).GetAsync());
        (await fixture.CountPublicationHeadersAsync(framed.OrganizationId)).ShouldBe(0);
    }

    [TestMethod]
    public async Task QuotaUsageInspectionInitializesDisabledInventoryWithoutPretendingPartialUsageIsComplete()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.OrganizationsRead]);
        await using (MutationScope publication = await fixture.BeginMutationScopeAsync(user))
            await CreatePublication(publication).EnsureAsync();
        await fixture.SeedQuotaUsageAsync(user.OrganizationId, 17);
        await using (MutationScope scope = await fixture.BeginMutationScopeAsync(user))
        {
            OrganizationQuotaUsageReader reader = CreateUsageReader(scope);
            var first = await reader.GetAsync();
            var resource = first.Value!.Resources.Single(row => row.ResourceKey == DocumentsQuotaResource);
            resource.Mode.ShouldBe("Disabled");
            resource.State.ShouldBe("Initializing");
            resource.Committed.ShouldBeNull();
            resource.Reserved.ShouldBe("0");
            (await reader.GetAsync()).Value!.Resources.Single(row => row.ResourceKey == DocumentsQuotaResource)
                .State.ShouldBe("Initializing");
            await scope.CommitAsync();
        }
        (await fixture.CountUsageInventoryWorkAsync(user.OrganizationId)).ShouldBe(1);
        await using (MutationScope retry = await fixture.BeginMutationScopeAsync(user))
        {
            (await CreateUsageReader(retry).GetAsync()).IsSuccess.ShouldBeTrue();
            await retry.CommitAsync();
        }
        (await fixture.CountUsageInventoryWorkAsync(user.OrganizationId)).ShouldBe(1);
    }

    [TestMethod]
    public async Task QuotaUsageInspectionSeparatesLiveAndExpiredHoldsWithoutRefundingEither()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.OrganizationsRead]);
        await fixture.SeedQuotaProjectionAsync(user.OrganizationId, 4);
        await fixture.SeedQuotaUsageAsync(user.OrganizationId, 3);
        await fixture.SeedExpiredActiveReservationAsync(user, 5);
        await fixture.SeedUsageLiveHoldAsync(user, 7);
        await using MutationScope scope = await fixture.BeginMutationScopeAsync(user);
        var resource = (await CreateUsageReader(scope).GetAsync()).Value!.Resources.Single(row => row.ResourceKey == DocumentsQuotaResource);
        resource.State.ShouldBe("Ready");
        resource.Limit.ShouldBe("4");
        resource.Committed.ShouldBe("3");
        resource.Reserved.ShouldBe("7");
        resource.ReconciliationHeld.ShouldBe("5");
        await scope.CommitAsync();
        (await fixture.CountQuotaRowsAsync("organization_quota_reservations", user.OrganizationId)).ShouldBe(2);
        (await fixture.SumQuotaUsageAsync(user.OrganizationId)).ShouldBe(3);
    }

    [TestMethod]
    public async Task QuotaUsageInspectionClassifiesFencingAsHeldEvenBeforeTheReservationExpires()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.OrganizationsRead, "documents.manage"]);
        await fixture.SeedQuotaProjectionAsync(user.OrganizationId, 64);
        await using UploadAdmissionEnvironment environment = fixture.CreateUploadAdmissionEnvironment();
        ModuleUploadRequest request = new(TestUploadPolicy, Guid.NewGuid(), 11, new string('A', 64), new string('B', 64));
        await using (MutationScope parent = await ExecutionFixture.BeginUploadMutationScopeAsync(environment.Services, user))
        {
            var admitted = await parent.Services.GetRequiredService<IModuleUploadAdmission>().ReserveAsync(request);
            admitted.Disposition.ShouldBe(ModuleUploadReserveDisposition.Reserved);
        }
        await fixture.MarkUsageUploadFencingAsync(user.OrganizationId, request.OperationId);
        await using MutationScope scope = await fixture.BeginMutationScopeAsync(user);
        var resource = (await CreateUsageReader(scope).GetAsync()).Value!.Resources.Single(row => row.ResourceKey == DocumentsQuotaResource);
        resource.Reserved.ShouldBe("0");
        resource.ReconciliationHeld.ShouldBe("11");
        resource.Committed.ShouldBe("0");
        await scope.CommitAsync();
        (await fixture.CountQuotaRowsAsync("organization_quota_reservations", user.OrganizationId)).ShouldBe(1);
    }

    [TestMethod]
    public async Task QuotaUsageInspectionNeverCommitsTheCallersInventoryIntentAndHonorsCancellation()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.OrganizationsRead]);
        await using (MutationScope rolledBack = await fixture.BeginMutationScopeAsync(user))
            (await CreateUsageReader(rolledBack).GetAsync()).IsSuccess.ShouldBeTrue();
        (await fixture.CountUsageInventoryWorkAsync(user.OrganizationId)).ShouldBe(0);
        await using MutationScope cancelled = await fixture.BeginMutationScopeAsync(user);
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => CreateUsageReader(cancelled).GetAsync(cancellation.Token));
        (await fixture.CountUsageInventoryWorkAsync(user.OrganizationId)).ShouldBe(0);
    }

    [TestMethod]
    public async Task QuotaUsageInspectionRefreshesLegacyDisplayMetadataWithoutChangingAllowanceVersion()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.OrganizationsRead]);
        await using (MutationScope source = await fixture.BeginMutationScopeAsync(user))
            await CreatePublication(source).EnsureAsync();
        PublicationRow[] before = await fixture.ReadPublicationRowsAsync(user.OrganizationId);
        await fixture.ClearUsageDisplayNamesAsync(user.OrganizationId);
        await using (MutationScope refreshed = await fixture.BeginMutationScopeAsync(user))
            (await CreateUsageReader(refreshed).GetAsync()).IsSuccess.ShouldBeTrue();
        (await fixture.ReadPublicationRowsAsync(user.OrganizationId)).ShouldBe(before);
        (await fixture.CountPublicationHeadersAsync(user.OrganizationId)).ShouldBe(1);
        await using MutationScope absentScope = await fixture.BeginMutationScopeAsync(user);
        ModuleCatalog absent = new(absentScope.Services.GetRequiredService<ModuleCatalog>().Modules.Where(module => module.Descriptor.Id != "documents"));
        (await CreateUsageReader(absentScope, absent).GetAsync()).Value!.Resources.Single(row => row.ResourceKey == DocumentsQuotaResource)
            .Name.ShouldBe("Document storage");
    }

    [TestMethod]
    public async Task QuotaUsageInspectionPreservesZeroUnlimitedDisabledAndUnconfiguredModes()
    {
        foreach (var (mode, limit, expected) in new (OrganizationEntitlementMode, long?, string?)[]
        {
            (OrganizationEntitlementMode.Finite, 0, "0"), (OrganizationEntitlementMode.Unlimited, null, null),
            (OrganizationEntitlementMode.Disabled, null, null), (OrganizationEntitlementMode.Unconfigured, null, null)
        })
        {
            ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.OrganizationsRead]);
            await fixture.SeedQuotaProjectionAsync(user.OrganizationId, limit, mode);
            await using MutationScope scope = await fixture.BeginMutationScopeAsync(user);
            var resource = (await CreateUsageReader(scope).GetAsync()).Value!.Resources.Single(row => row.ResourceKey == DocumentsQuotaResource);
            resource.Mode.ShouldBe(mode.ToString());
            resource.Limit.ShouldBe(expected);
            resource.Committed.ShouldBe("0");
            resource.State.ShouldBe("Ready");
        }
    }

    [TestMethod]
    public async Task QuotaUsageInspectionRetainsAbsentResourceHistoryWithoutRunningItsInventory()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.OrganizationsRead]);
        await fixture.SeedQuotaProjectionAsync(user.OrganizationId, 10);
        await fixture.SeedQuotaUsageAsync(user.OrganizationId, 9);
        await fixture.SeedExpiredActiveReservationAsync(user, 2);
        await using MutationScope scope = await fixture.BeginMutationScopeAsync(user);
        ModuleCatalog absent = new(scope.Services.GetRequiredService<ModuleCatalog>().Modules.Where(module => module.Descriptor.Id != "documents"));
        var resource = (await CreateUsageReader(scope, absent).GetAsync()).Value!.Resources.Single(row => row.ResourceKey == DocumentsQuotaResource);
        resource.Installed.ShouldBeFalse();
        resource.State.ShouldBe("Paused");
        resource.Name.ShouldBe(DocumentsQuotaResource);
        resource.Unit.ShouldBe("Bytes");
        resource.Committed.ShouldBe("9");
        resource.ReconciliationHeld.ShouldBe("2");
        await scope.CommitAsync();
        (await fixture.CountUsageInventoryWorkAsync(user.OrganizationId)).ShouldBe(0);
        (await fixture.SumQuotaUsageAsync(user.OrganizationId)).ShouldBe(9);
    }

    [TestMethod]
    public async Task QuotaUsageInspectionRetainsPublishedDisplayNameAfterItsOwnerIsRemoved()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.OrganizationsRead]);
        await using (MutationScope scope = await fixture.BeginMutationScopeAsync(user))
            await CreatePublication(scope).EnsureAsync();
        await using MutationScope inspection = await fixture.BeginMutationScopeAsync(user);
        ModuleCatalog absent = new(inspection.Services.GetRequiredService<ModuleCatalog>().Modules.Where(module => module.Descriptor.Id != "documents"));
        var resource = (await CreateUsageReader(inspection, absent).GetAsync()).Value!.Resources.Single(row => row.ResourceKey == DocumentsQuotaResource);
        resource.Name.ShouldBe("Document storage");
        resource.State.ShouldBe("Paused");
        resource.Installed.ShouldBeFalse();
    }

    [TestMethod]
    public async Task QuotaUsageInspectionDoesNotUseAnOldInventorySourceVersionAsComplete()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.OrganizationsRead]);
        await fixture.SeedQuotaProjectionAsync(user.OrganizationId, 10);
        await fixture.SetUsageInventorySourceVersionAsync(user.OrganizationId, 2);
        await using MutationScope scope = await fixture.BeginMutationScopeAsync(user);
        var resource = (await CreateUsageReader(scope).GetAsync()).Value!.Resources.Single(row => row.ResourceKey == DocumentsQuotaResource);
        resource.State.ShouldBe("Initializing");
        resource.Committed.ShouldBeNull();
        await scope.CommitAsync();
        (await fixture.CountUsageInventoryWorkAsync(user.OrganizationId)).ShouldBe(1);
    }

    [TestMethod]
    public async Task QuotaUsageInspectionDoesNotInventMissingProjectionOnAnOlderPolicyNode()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.OrganizationsRead]);
        await using (MutationScope newer = await fixture.BeginMutationScopeAsync(user))
        {
            ModuleCatalog smaller = new(newer.Services.GetRequiredService<ModuleCatalog>().Modules.Where(module => module.Descriptor.Id != "probe"));
            await CreatePublication(newer, revision: 8, limit: 4, catalogOverride: smaller).EnsureAsync();
        }
        await using MutationScope older = await fixture.BeginMutationScopeAsync(user);
        var result = await CreateUsageReader(older).GetAsync();
        result.IsSuccess.ShouldBeTrue();
        var missing = result.Value!.Resources.Single(row => row.ResourceKey == ProbeQuotaResource);
        missing.State.ShouldBe("Unavailable");
        missing.Mode.ShouldBe("Unconfigured");
        missing.Limit.ShouldBeNull();
        missing.Version.ShouldBeNull();
        missing.Source.ShouldBeNull();
        missing.Committed.ShouldBeNull();
        missing.Installed.ShouldBeTrue();
        result.Value.Resources.Single(row => row.ResourceKey == DocumentsQuotaResource).Limit.ShouldBe("4");
    }

    [TestMethod]
    public async Task QuotaUsageInspectionReportsOverflowAsUnavailableWithoutTruncatingOrMutatingUsage()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.OrganizationsRead]);
        await fixture.SeedQuotaProjectionAsync(user.OrganizationId, long.MaxValue);
        await fixture.SeedQuotaUsageAsync(user.OrganizationId, long.MaxValue);
        await fixture.SeedQuotaUsageAsync(user.OrganizationId, 1);
        await using MutationScope scope = await fixture.BeginMutationScopeAsync(user);
        var resource = (await CreateUsageReader(scope).GetAsync()).Value!.Resources.Single(row => row.ResourceKey == DocumentsQuotaResource);
        resource.State.ShouldBe("Unavailable");
        resource.Committed.ShouldBeNull();
        await scope.CommitAsync();
        (await fixture.CountQuotaRowsAsync("organization_usage", user.OrganizationId)).ShouldBe(2);
    }

    [TestMethod]
    public async Task QuotaUsageInspectionSanitizesPublicationOutageAndRequiresCallerTransaction()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.OrganizationsRead]);
        await using MutationScope scope = await fixture.BeginMutationScopeAsync(user);
        PublicationHarness unavailable = CreatePublication(scope, platformOverride:
            "Host=127.0.0.1;Port=1;Database=unavailable;Username=trykatch_platform_runtime;Password=not-a-secret;Timeout=1");
        var result = await ActivatorUtilities.CreateInstance<OrganizationQuotaUsageReader>(scope.Services, unavailable.Authority, unavailable.Publisher).GetAsync();
        result.IsSuccess.ShouldBeFalse();
        result.ErrorCode.ShouldBe("quota_usage_unavailable");
        result.Value.ShouldBeNull();
        await using var noTransaction = fixture.CreateScope();
        await Should.ThrowAsync<InvalidOperationException>(() => ActivatorUtilities.CreateInstance<OrganizationQuotaUsageReader>(noTransaction.ServiceProvider).GetAsync());
    }

    private static OrganizationQuotaUsageReader CreateUsageReader(MutationScope scope, ModuleCatalog? catalog = null)
    {
        if (catalog is null) return ActivatorUtilities.CreateInstance<OrganizationQuotaUsageReader>(scope.Services);
        PublicationHarness publication = CreatePublication(scope, catalogOverride: catalog);
        return ActivatorUtilities.CreateInstance<OrganizationQuotaUsageReader>(scope.Services, catalog, publication.Authority, publication.Publisher);
    }

    [TestMethod]
    public async Task QuotaUsageInspectionRejectsTechnicalMaintenanceAuthorityWithoutInventingMembership()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.OrganizationsRead]);
        await using QuotaMaintenanceScope? scope = await fixture.OpenQuotaMaintenanceScopeAsync(user.OrganizationId);
        scope.ShouldNotBeNull();
        var result = await scope.Services.GetRequiredService<IOrganizationQuotaUsageReader>().GetAsync();
        result.IsSuccess.ShouldBeFalse();
        result.ErrorCode.ShouldBe("forbidden");
        result.Value.ShouldBeNull();
    }

    private sealed partial class ExecutionFixture
    {
        public Task<long> CountUsageInventoryWorkAsync(Guid organizationId) => CountOwnerAsync("""
            SELECT count(*) FROM infrastructure.quota_maintenance_work WHERE "OrganizationId" = @organization AND "Kind" = 3
            """, ("organization", organizationId));

        public Task MarkUsageUploadFencingAsync(Guid organizationId, Guid operationId) => ExecuteOwnerAsync("""
            UPDATE app.module_upload_operations SET "State" = 2
            WHERE "OrganizationId" = @organization AND "OperationId" = @operation AND "ExpiresAtUtc" > statement_timestamp()
            """, ("organization", organizationId), ("operation", operationId));

        public Task ClearUsageDisplayNamesAsync(Guid organizationId) => ExecuteOwnerAsync("""
            UPDATE app.organization_entitlements SET "DisplayName" = NULL WHERE "OrganizationId" = @organization
            """, ("organization", organizationId));

        public Task SetUsageInventorySourceVersionAsync(Guid organizationId, int version) => ExecuteOwnerAsync("""
            UPDATE app.organization_quota_inventory SET "SourceVersion" = @version
            WHERE "OrganizationId" = @organization AND "ResourceKey" = 'documents.storage-bytes'
            """, ("organization", organizationId), ("version", version));

        public Task SeedUsageLiveHoldAsync(ProjectsSeededUser user, long units) => ExecuteOwnerAsync("""
            INSERT INTO app.organization_quota_reservations
                ("OrganizationId", "Id", "ResourceKey", "ActorId", "Units", "OperationId", "EntitlementVersion", "EffectKind", "State", "CreatedAtUtc", "ExpiresAtUtc", "ClosedAtUtc")
            SELECT @organization, @id, 'documents.storage-bytes', @actor, @units, @operation, "Version", 1, 0,
                CURRENT_TIMESTAMP, CURRENT_TIMESTAMP + INTERVAL '1 hour', NULL
            FROM app.organization_entitlements WHERE "OrganizationId" = @organization AND "ResourceKey" = 'documents.storage-bytes'
            """, ("organization", user.OrganizationId), ("id", Guid.NewGuid()), ("actor", user.ActorId), ("units", units), ("operation", Guid.NewGuid()));
    }
}

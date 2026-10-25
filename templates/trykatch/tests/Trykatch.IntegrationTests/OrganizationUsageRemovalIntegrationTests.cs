using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Trykatch.Application;
using Trykatch.Application.Organizations;
using Trykatch.Application.Quotas;
using Trykatch.Infrastructure;
using Trykatch.Infrastructure.Organizations;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Infrastructure.Quotas;
using Trykatch.Modules;
using Trykatch.Modules.Documents.Infrastructure;
using Trykatch.Modules.Projects.Infrastructure;

namespace Trykatch.IntegrationTests;

public sealed partial class ModuleWorkExecutionIntegrationTests
{
    private const string ProbeUsageResource = "probe.units";
    private static readonly ModuleUsageRemovalPolicyRef ProbeRemovalPolicy = new("probe", "probe.permanent-removal", 1);

    [TestMethod]
    public async Task ConfirmedRemovalAppendsNegativeEffectInsideCallerTransactionAndRollsBackWithIt()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync(["probe.write"]);
        await fixture.SeedRemovalQuotaProjectionAsync(user.OrganizationId);
        await using UsageRemovalEnvironment environment = fixture.CreateUsageRemovalEnvironment();
        SeededUsageEffect source = await fixture.SeedUsageRemovalEffectAsync(user, units: 8);
        Guid operationId = Guid.CreateVersion7();

        await using (MutationScope scope = await ExecutionFixture.BeginUploadMutationScopeAsync(environment.Services, user))
        {
            OrganizationUsageRemovalResult result = await scope.Services.GetRequiredService<IOrganizationUsageLedger>()
                .RecordRemovalAsync(ProbeUsageResource, source.EffectId, 8, operationId);

            result.Disposition.ShouldBe(OrganizationUsageRemovalDisposition.Recorded);
            long transactionTotal = await scope.Services.GetRequiredService<ApplicationDbContext>().Database.SqlQuery<long>($"""
                SELECT COALESCE(sum("Units"), 0)::bigint AS "Value" FROM app.organization_usage
                WHERE "OrganizationId" = {user.OrganizationId} AND "ResourceKey" = {ProbeUsageResource}
                """).SingleAsync();
            transactionTotal.ShouldBe(0);
            (await fixture.SumUsageForResourceAsync(user.OrganizationId)).ShouldBe(8);
        }

        (await fixture.ReadUsageEffectsAsync(user.OrganizationId, source.EffectId)).ShouldBe(1);
        (await fixture.SumUsageForResourceAsync(user.OrganizationId)).ShouldBe(8);
    }

    [TestMethod]
    public async Task ConfirmedRemovalCanBeCommittedAndExactRetryDoesNotOverRefund()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync(["probe.write"]);
        await fixture.SeedRemovalQuotaProjectionAsync(user.OrganizationId);
        await using UsageRemovalEnvironment environment = fixture.CreateUsageRemovalEnvironment();
        SeededUsageEffect source = await fixture.SeedUsageRemovalEffectAsync(user, units: 8);
        Guid operationId = Guid.CreateVersion7();

        await using (MutationScope scope = await ExecutionFixture.BeginUploadMutationScopeAsync(environment.Services, user))
        {
            IOrganizationUsageLedger ledger = scope.Services.GetRequiredService<IOrganizationUsageLedger>();
            (await ledger.RecordRemovalAsync(ProbeUsageResource, source.EffectId, 8, operationId)).Disposition
                .ShouldBe(OrganizationUsageRemovalDisposition.Recorded);
            await scope.CommitAsync();
        }
        await using MutationScope retryScope = await ExecutionFixture.BeginUploadMutationScopeAsync(environment.Services, user);
        OrganizationUsageRemovalResult retry = await retryScope.Services.GetRequiredService<IOrganizationUsageLedger>()
            .RecordRemovalAsync(ProbeUsageResource, source.EffectId, 8, operationId);

        retry.Disposition.ShouldBe(OrganizationUsageRemovalDisposition.AlreadyRecorded);
        (await fixture.ReadUsageEffectsAsync(user.OrganizationId, source.EffectId)).ShouldBe(2);
        (await fixture.SumUsageForResourceAsync(user.OrganizationId)).ShouldBe(0);
    }

    [TestMethod]
    public async Task RemovalOperationReuseWithChangedSourceOrUnitsConflicts()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync(["probe.write"]);
        await fixture.SeedRemovalQuotaProjectionAsync(user.OrganizationId);
        await using UsageRemovalEnvironment environment = fixture.CreateUsageRemovalEnvironment();
        SeededUsageEffect first = await fixture.SeedUsageRemovalEffectAsync(user, units: 8);
        SeededUsageEffect second = await fixture.SeedUsageRemovalEffectAsync(user, units: 6);
        Guid operationId = Guid.CreateVersion7();

        await using MutationScope scope = await ExecutionFixture.BeginUploadMutationScopeAsync(environment.Services, user);
        IOrganizationUsageLedger ledger = scope.Services.GetRequiredService<IOrganizationUsageLedger>();
        (await ledger.RecordRemovalAsync(ProbeUsageResource, first.EffectId, 4, operationId)).Disposition
            .ShouldBe(OrganizationUsageRemovalDisposition.Recorded);
        (await ledger.RecordRemovalAsync(ProbeUsageResource, second.EffectId, 4, operationId)).Disposition
            .ShouldBe(OrganizationUsageRemovalDisposition.Conflict);
        (await ledger.RecordRemovalAsync(ProbeUsageResource, first.EffectId, 5, operationId)).Disposition
            .ShouldBe(OrganizationUsageRemovalDisposition.Conflict);
    }

    [TestMethod]
    public async Task RetryFromAnotherAuthorizedActorConflicts()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync(["probe.write"]);
        ProjectsSeededUser otherActor = await fixture.SeedAdditionalProjectsActorAsync(user.OrganizationId);
        await fixture.GrantMembershipPermissionAsync(otherActor.MembershipId, "probe.write");
        await fixture.SeedRemovalQuotaProjectionAsync(user.OrganizationId);
        await using UsageRemovalEnvironment environment = fixture.CreateUsageRemovalEnvironment();
        SeededUsageEffect source = await fixture.SeedUsageRemovalEffectAsync(user, units: 8);
        Guid operationId = Guid.CreateVersion7();

        await using (MutationScope scope = await ExecutionFixture.BeginUploadMutationScopeAsync(environment.Services, user))
        {
            (await scope.Services.GetRequiredService<IOrganizationUsageLedger>()
                .RecordRemovalAsync(ProbeUsageResource, source.EffectId, 8, operationId)).Disposition
                .ShouldBe(OrganizationUsageRemovalDisposition.Recorded);
            await scope.CommitAsync();
        }
        await using MutationScope retryScope = await ExecutionFixture.BeginUploadMutationScopeAsync(environment.Services, otherActor);

        OrganizationUsageRemovalResult retry = await retryScope.Services.GetRequiredService<IOrganizationUsageLedger>()
            .RecordRemovalAsync(ProbeUsageResource, source.EffectId, 8, operationId);

        retry.Disposition.ShouldBe(OrganizationUsageRemovalDisposition.Conflict);
        (await fixture.ReadUsageEffectsAsync(user.OrganizationId, source.EffectId)).ShouldBe(2);
    }

    [TestMethod]
    public async Task RemovalRequiresPermanentSourceEvidenceAndRejectsMissingPolicyResources()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync(["probe.write"]);
        await fixture.SeedRemovalQuotaProjectionAsync(user.OrganizationId);
        await using UsageRemovalEnvironment environment = fixture.CreateUsageRemovalEnvironment();
        SeededUsageEffect active = await fixture.SeedUsageRemovalEffectAsync(user, units: 8, canDisclose: true);
        SeededUsageEffect archived = await fixture.SeedUsageRemovalEffectAsync(user, units: 8, marker: "archived");
        SeededUsageEffect matching = await fixture.SeedUsageRemovalEffectAsync(user, units: 8);
        Guid operationA = Guid.CreateVersion7();
        Guid operationB = Guid.CreateVersion7();
        await using MutationScope scope = await ExecutionFixture.BeginUploadMutationScopeAsync(environment.Services, user);
        IOrganizationUsageLedger ledger = scope.Services.GetRequiredService<IOrganizationUsageLedger>();

        (await ledger.RecordRemovalAsync(ProbeUsageResource, active.EffectId, 8, operationA)).Disposition
            .ShouldBe(OrganizationUsageRemovalDisposition.Denied);
        (await ledger.RecordRemovalAsync(ProbeUsageResource, archived.EffectId, 8, operationB)).Disposition
            .ShouldBe(OrganizationUsageRemovalDisposition.Denied);
        (await ledger.RecordRemovalAsync("documents.storage-bytes", matching.EffectId, 8, Guid.CreateVersion7())).Disposition
            .ShouldBe(OrganizationUsageRemovalDisposition.Denied);
        (await fixture.ReadUsageEffectsAsync(user.OrganizationId, matching.EffectId)).ShouldBe(1);
    }

    [TestMethod]
    public async Task RemovalRequiresPositiveSourceAndValidRequestIdentities()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync(["probe.write"]);
        await fixture.SeedRemovalQuotaProjectionAsync(user.OrganizationId);
        await using UsageRemovalEnvironment environment = fixture.CreateUsageRemovalEnvironment();
        SeededUsageEffect source = await fixture.SeedUsageRemovalEffectAsync(user, units: 8);
        await using MutationScope scope = await ExecutionFixture.BeginUploadMutationScopeAsync(environment.Services, user);
        IOrganizationUsageLedger ledger = scope.Services.GetRequiredService<IOrganizationUsageLedger>();

        (await ledger.RecordRemovalAsync(ProbeUsageResource, source.EffectId, 0, Guid.CreateVersion7())).Disposition
            .ShouldBe(OrganizationUsageRemovalDisposition.Denied);
        (await ledger.RecordRemovalAsync(ProbeUsageResource, source.EffectId, -1, Guid.CreateVersion7())).Disposition
            .ShouldBe(OrganizationUsageRemovalDisposition.Denied);
        (await ledger.RecordRemovalAsync(ProbeUsageResource, Guid.Empty, 1, Guid.CreateVersion7())).Disposition
            .ShouldBe(OrganizationUsageRemovalDisposition.Denied);
        (await ledger.RecordRemovalAsync(ProbeUsageResource, source.EffectId, 1, Guid.Empty)).Disposition
            .ShouldBe(OrganizationUsageRemovalDisposition.Denied);
        (await ledger.RecordRemovalAsync(ProbeUsageResource, source.EffectId, 1, source.EffectId)).Disposition
            .ShouldBe(OrganizationUsageRemovalDisposition.Denied);
        (await ledger.RecordRemovalAsync(ProbeUsageResource, Guid.CreateVersion7(), 1, Guid.CreateVersion7())).Disposition
            .ShouldBe(OrganizationUsageRemovalDisposition.Denied);
        Guid debitId = Guid.CreateVersion7();
        (await ledger.RecordRemovalAsync(ProbeUsageResource, source.EffectId, 8, debitId)).Disposition
            .ShouldBe(OrganizationUsageRemovalDisposition.Recorded);
        (await ledger.RecordRemovalAsync(ProbeUsageResource, debitId, 1, Guid.CreateVersion7())).Disposition
            .ShouldBe(OrganizationUsageRemovalDisposition.Denied);
        (await fixture.ReadUsageEffectsAsync(user.OrganizationId, source.EffectId)).ShouldBe(1);
    }

    [TestMethod]
    public async Task PartialRemovalCanBeSplitButCumulativeRemovalCannotExceedOriginal()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync(["probe.write"]);
        await fixture.SeedRemovalQuotaProjectionAsync(user.OrganizationId);
        await using UsageRemovalEnvironment environment = fixture.CreateUsageRemovalEnvironment();
        SeededUsageEffect source = await fixture.SeedUsageRemovalEffectAsync(user, units: 8);

        await using (MutationScope scope = await ExecutionFixture.BeginUploadMutationScopeAsync(environment.Services, user))
        {
            IOrganizationUsageLedger ledger = scope.Services.GetRequiredService<IOrganizationUsageLedger>();
            (await ledger.RecordRemovalAsync(ProbeUsageResource, source.EffectId, 4, Guid.CreateVersion7())).Disposition
                .ShouldBe(OrganizationUsageRemovalDisposition.Recorded);
            (await ledger.RecordRemovalAsync(ProbeUsageResource, source.EffectId, 4, Guid.CreateVersion7())).Disposition
                .ShouldBe(OrganizationUsageRemovalDisposition.Recorded);
            (await ledger.RecordRemovalAsync(ProbeUsageResource, source.EffectId, 1, Guid.CreateVersion7())).Disposition
                .ShouldBe(OrganizationUsageRemovalDisposition.Denied);
            await scope.CommitAsync();
        }
        await using MutationScope remainingScope = await ExecutionFixture.BeginUploadMutationScopeAsync(environment.Services, user);
        IOrganizationUsageLedger remainingLedger = remainingScope.Services.GetRequiredService<IOrganizationUsageLedger>();
        (await remainingLedger.RecordRemovalAsync(ProbeUsageResource, source.EffectId, 8, Guid.CreateVersion7())).Disposition
            .ShouldBe(OrganizationUsageRemovalDisposition.Denied);
    }

    [TestMethod]
    public async Task RemovalRequiresFreshActiveMembershipAndCurrentOrganization()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync(["probe.write"]);
        await fixture.SeedRemovalQuotaProjectionAsync(user.OrganizationId);
        await using UsageRemovalEnvironment environment = fixture.CreateUsageRemovalEnvironment();
        SeededUsageEffect source = await fixture.SeedUsageRemovalEffectAsync(user, units: 8);
        await using MutationScope scope = await ExecutionFixture.BeginUploadMutationScopeAsync(environment.Services, user);
        await fixture.SuspendMembershipAsync(user.MembershipId);

        OrganizationUsageRemovalResult denied = await scope.Services.GetRequiredService<IOrganizationUsageLedger>()
            .RecordRemovalAsync(ProbeUsageResource, source.EffectId, 8, Guid.CreateVersion7());

        denied.Disposition.ShouldBe(OrganizationUsageRemovalDisposition.Denied);
        (await fixture.ReadUsageEffectsAsync(user.OrganizationId, source.EffectId)).ShouldBe(1);
    }

    [TestMethod]
    public async Task RemovalCannotUseAnotherOrganizationsSourceOrAnInactiveOrganization()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync(["probe.write"]);
        ProjectsSeededUser otherOrganization = await fixture.SeedProjectsUserAsync(["probe.write"]);
        await fixture.SeedRemovalQuotaProjectionAsync(user.OrganizationId);
        await fixture.SeedRemovalQuotaProjectionAsync(otherOrganization.OrganizationId);
        await using UsageRemovalEnvironment environment = fixture.CreateUsageRemovalEnvironment();
        SeededUsageEffect foreignSource = await fixture.SeedUsageRemovalEffectAsync(otherOrganization, units: 8);
        await using (MutationScope scope = await ExecutionFixture.BeginUploadMutationScopeAsync(environment.Services, user))
        {
            OrganizationUsageRemovalResult denied = await scope.Services.GetRequiredService<IOrganizationUsageLedger>()
                .RecordRemovalAsync(ProbeUsageResource, foreignSource.EffectId, 8, Guid.CreateVersion7());
            denied.Disposition.ShouldBe(OrganizationUsageRemovalDisposition.Denied);
        }
        SeededUsageEffect localSource = await fixture.SeedUsageRemovalEffectAsync(otherOrganization, units: 8);
        await using MutationScope inactiveScope = await ExecutionFixture.BeginUploadMutationScopeAsync(environment.Services, otherOrganization);
        await fixture.SetOrganizationActiveAsync(otherOrganization.OrganizationId, active: false);

        OrganizationUsageRemovalResult inactive = await inactiveScope.Services.GetRequiredService<IOrganizationUsageLedger>()
            .RecordRemovalAsync(ProbeUsageResource, localSource.EffectId, 8, Guid.CreateVersion7());

        inactive.Disposition.ShouldBe(OrganizationUsageRemovalDisposition.Denied);
        (await fixture.ReadUsageEffectsAsync(otherOrganization.OrganizationId, localSource.EffectId)).ShouldBe(1);
    }

    [TestMethod]
    public async Task ConcurrentRemovalScopesCannotRefundTheSameSourceTwice()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync(["probe.write"]);
        await fixture.SeedRemovalQuotaProjectionAsync(user.OrganizationId);
        await using UsageRemovalEnvironment environment = fixture.CreateUsageRemovalEnvironment();
        SeededUsageEffect source = await fixture.SeedUsageRemovalEffectAsync(user, units: 8);
        await using MutationScope first = await ExecutionFixture.BeginUploadMutationScopeAsync(environment.Services, user);
        await using MutationScope second = await ExecutionFixture.BeginUploadMutationScopeAsync(environment.Services, user);
        foreach (MutationScope participant in new[] { first, second })
            await participant.Services.GetRequiredService<QuotaPublicationAuthority>().EnsurePublishedAsync(
                participant.Services.GetRequiredService<IOrganizationEntitlementProjectionPublisher>(), CancellationToken.None);

        Task<OrganizationUsageRemovalResult> firstAttempt = first.Services.GetRequiredService<IOrganizationUsageLedger>()
            .RecordRemovalAsync(ProbeUsageResource, source.EffectId, 8, Guid.CreateVersion7());
        Task<OrganizationUsageRemovalResult> secondAttempt = second.Services.GetRequiredService<IOrganizationUsageLedger>()
            .RecordRemovalAsync(ProbeUsageResource, source.EffectId, 8, Guid.CreateVersion7());
        OrganizationUsageRemovalResult[] results = await Task.WhenAll(firstAttempt, secondAttempt);

        results.Count(result => result.Disposition == OrganizationUsageRemovalDisposition.Recorded).ShouldBe(1);
        OrganizationUsageRemovalResult loser = results.Single(result => result.Disposition != OrganizationUsageRemovalDisposition.Recorded);
        loser.Disposition.ShouldBe(OrganizationUsageRemovalDisposition.Unavailable);
        loser.FailureCode.ShouldBe("quota_busy");
        MutationScope recordedScope = results[0].Disposition == OrganizationUsageRemovalDisposition.Recorded ? first : second;
        await recordedScope.CommitAsync();
        await (ReferenceEquals(recordedScope, first) ? second : first).CommitAsync();
        (await fixture.ReadUsageEffectsAsync(user.OrganizationId, source.EffectId)).ShouldBe(2);
    }

    [TestMethod]
    public async Task RemovalConfirmationFailureReturnsUnavailableWithoutWritingDebit()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync(["probe.write"]);
        await fixture.SeedRemovalQuotaProjectionAsync(user.OrganizationId);
        await using UsageRemovalEnvironment environment = fixture.CreateUsageRemovalEnvironment(configurePolicy: behavior => behavior.Fail = true);
        SeededUsageEffect source = await fixture.SeedUsageRemovalEffectAsync(user, units: 8);
        await using MutationScope scope = await ExecutionFixture.BeginUploadMutationScopeAsync(environment.Services, user);

        OrganizationUsageRemovalResult result = await scope.Services.GetRequiredService<IOrganizationUsageLedger>()
            .RecordRemovalAsync(ProbeUsageResource, source.EffectId, 8, Guid.CreateVersion7());

        result.Disposition.ShouldBe(OrganizationUsageRemovalDisposition.Unavailable);
        (await fixture.ReadUsageEffectsAsync(user.OrganizationId, source.EffectId)).ShouldBe(1);
    }

    [TestMethod]
    public async Task CallerCancellationIsPropagatedBeforeAnyDebit()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync(["probe.write"]);
        await fixture.SeedRemovalQuotaProjectionAsync(user.OrganizationId);
        await using UsageRemovalEnvironment environment = fixture.CreateUsageRemovalEnvironment();
        SeededUsageEffect source = await fixture.SeedUsageRemovalEffectAsync(user, units: 8);
        await using MutationScope scope = await ExecutionFixture.BeginUploadMutationScopeAsync(environment.Services, user);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => scope.Services.GetRequiredService<IOrganizationUsageLedger>()
            .RecordRemovalAsync(ProbeUsageResource, source.EffectId, 8, Guid.CreateVersion7(), cancellation.Token));
        (await fixture.ReadUsageEffectsAsync(user.OrganizationId, source.EffectId)).ShouldBe(1);
    }

    [TestMethod]
    public async Task RemovalRequiresOriginalParentTransactionAndMatchingActorFrame()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync(["probe.write"]);
        await fixture.SeedRemovalQuotaProjectionAsync(user.OrganizationId);
        await using UsageRemovalEnvironment environment = fixture.CreateUsageRemovalEnvironment();
        SeededUsageEffect source = await fixture.SeedUsageRemovalEffectAsync(user, units: 8);
        await using (AsyncServiceScope missing = environment.Services.CreateAsyncScope())
        {
            IOrganizationUsageLedger ledger = missing.ServiceProvider.GetRequiredService<IOrganizationUsageLedger>();
            await Should.ThrowAsync<InvalidOperationException>(() =>
                ledger.RecordRemovalAsync(ProbeUsageResource, source.EffectId, 8, Guid.CreateVersion7()));
        }
        await using MutationScope scope = await ExecutionFixture.BeginUploadMutationScopeAsync(environment.Services, user);
        await scope.Services.GetRequiredService<ApplicationDbContext>().Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.actor_id', {Guid.CreateVersion7().ToString()}, true)");
        await Should.ThrowAsync<InvalidOperationException>(() => scope.Services.GetRequiredService<IOrganizationUsageLedger>()
            .RecordRemovalAsync(ProbeUsageResource, source.EffectId, 8, Guid.CreateVersion7()));
        (await fixture.ReadUsageEffectsAsync(user.OrganizationId, source.EffectId)).ShouldBe(1);
    }

    [TestMethod]
    public async Task RemovalRechecksMembershipAfterPolicyConfirmation()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync(["probe.write"]);
        await fixture.SeedRemovalQuotaProjectionAsync(user.OrganizationId);
        await using UsageRemovalEnvironment environment = fixture.CreateUsageRemovalEnvironment(behavior =>
            behavior.BeforeReturn = _ => fixture.SuspendMembershipAsync(user.MembershipId));
        SeededUsageEffect source = await fixture.SeedUsageRemovalEffectAsync(user, units: 8);
        await using MutationScope scope = await ExecutionFixture.BeginUploadMutationScopeAsync(environment.Services, user);

        OrganizationUsageRemovalResult result = await scope.Services.GetRequiredService<IOrganizationUsageLedger>()
            .RecordRemovalAsync(ProbeUsageResource, source.EffectId, 8, Guid.CreateVersion7());

        result.Disposition.ShouldBe(OrganizationUsageRemovalDisposition.Denied);
        await scope.CommitAsync();
        (await fixture.ReadUsageEffectsAsync(user.OrganizationId, source.EffectId)).ShouldBe(1);
    }

    [TestMethod]
    public async Task RemovalCanRefundOverLimitInt64UsageWithoutOverflow()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync(["probe.write"]);
        await fixture.SeedRemovalQuotaProjectionAsync(user.OrganizationId);
        await using UsageRemovalEnvironment environment = fixture.CreateUsageRemovalEnvironment();
        SeededUsageEffect source = await fixture.SeedUsageRemovalEffectAsync(user, units: long.MaxValue);
        await using MutationScope scope = await ExecutionFixture.BeginUploadMutationScopeAsync(environment.Services, user);
        IOrganizationUsageLedger ledger = scope.Services.GetRequiredService<IOrganizationUsageLedger>();

        (await ledger.RecordRemovalAsync(ProbeUsageResource, source.EffectId, long.MaxValue - 1, Guid.CreateVersion7())).Disposition
            .ShouldBe(OrganizationUsageRemovalDisposition.Recorded);
        (await ledger.RecordRemovalAsync(ProbeUsageResource, source.EffectId, 2, Guid.CreateVersion7())).Disposition
            .ShouldBe(OrganizationUsageRemovalDisposition.Denied);
        (await ledger.RecordRemovalAsync(ProbeUsageResource, source.EffectId, 1, Guid.CreateVersion7())).Disposition
            .ShouldBe(OrganizationUsageRemovalDisposition.Recorded);
        await scope.CommitAsync();
        (await fixture.SumUsageForResourceAsync(user.OrganizationId)).ShouldBe(0);
        (await fixture.ReadUsageEffectsAsync(user.OrganizationId, source.EffectId)).ShouldBe(3);
    }

    [TestMethod]
    public async Task RemovalCompletedRetryRequiresCurrentOwnerAndPolicyPermission()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync(["probe.write"]);
        await fixture.SeedRemovalQuotaProjectionAsync(user.OrganizationId);
        await using UsageRemovalEnvironment environment = fixture.CreateUsageRemovalEnvironment();
        SeededUsageEffect source = await fixture.SeedUsageRemovalEffectAsync(user, units: 8);
        Guid operationId = Guid.CreateVersion7();
        await using (MutationScope scope = await ExecutionFixture.BeginUploadMutationScopeAsync(environment.Services, user))
        {
            (await scope.Services.GetRequiredService<IOrganizationUsageLedger>()
                .RecordRemovalAsync(ProbeUsageResource, source.EffectId, 8, operationId)).Disposition
                .ShouldBe(OrganizationUsageRemovalDisposition.Recorded);
            await scope.CommitAsync();
        }
        await using (UploadAdmissionEnvironment absentOwner = fixture.CreateUploadAdmissionEnvironment())
        await using (MutationScope absent = await ExecutionFixture.BeginUploadMutationScopeAsync(absentOwner.Services, user))
            (await absent.Services.GetRequiredService<IOrganizationUsageLedger>()
                .RecordRemovalAsync(ProbeUsageResource, source.EffectId, 8, operationId)).Disposition
                .ShouldBe(OrganizationUsageRemovalDisposition.Denied);
        await using MutationScope retry = await ExecutionFixture.BeginUploadMutationScopeAsync(environment.Services, user);
        await fixture.RevokeRemovalPermissionAsync(user.MembershipId);
        (await retry.Services.GetRequiredService<IOrganizationUsageLedger>()
            .RecordRemovalAsync(ProbeUsageResource, source.EffectId, 8, operationId)).Disposition
            .ShouldBe(OrganizationUsageRemovalDisposition.Denied);
        (await fixture.ReadUsageEffectsAsync(user.OrganizationId, source.EffectId)).ShouldBe(2);
    }

    [TestMethod]
    public async Task RemovalConfirmationCannotMoveTheDebitToANewTransactionLifetime()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync(["probe.write"]);
        await fixture.SeedRemovalQuotaProjectionAsync(user.OrganizationId);
        await using UsageRemovalEnvironment environment = fixture.CreateUsageRemovalEnvironment();
        SeededUsageEffect source = await fixture.SeedUsageRemovalEffectAsync(user, units: 8);
        await using MutationScope scope = await ExecutionFixture.BeginUploadMutationScopeAsync(environment.Services, user);
        OrganizationControlPlaneDbContext control = scope.Services.GetRequiredService<OrganizationControlPlaneDbContext>();
        ApplicationDbContext application = scope.Services.GetRequiredService<ApplicationDbContext>();
        IOrganizationDataScope? replacement = null;
        environment.Services.GetRequiredService<RemovalPolicyBehavior>().BeforeReturn = async token =>
        {
            IDbContextTransaction old = control.Database.CurrentTransaction!;
            Guid oldLifetime = old.TransactionId;
            await old.RollbackAsync(token);
            await application.Database.CurrentTransaction!.DisposeAsync();
            await old.DisposeAsync();
            replacement = await scope.Services.GetRequiredService<IOrganizationDataScopeFactory>()
                .BeginAsync(user.OrganizationId, user.ActorId, token);
            _ = await OrganizationTransactionEnlistment.EnlistAsync(control, application,
                user.OrganizationId, user.ActorId, token);
            control.Database.CurrentTransaction!.TransactionId.ShouldNotBe(oldLifetime);
        };
        try
        {
            InvalidOperationException failure = await Should.ThrowAsync<InvalidOperationException>(() =>
                scope.Services.GetRequiredService<IOrganizationUsageLedger>()
                    .RecordRemovalAsync(ProbeUsageResource, source.EffectId, 8, Guid.CreateVersion7()));
            failure.Message.ShouldContain("original parent transaction");
            (await fixture.ReadUsageEffectsAsync(user.OrganizationId, source.EffectId)).ShouldBe(1);
        }
        finally
        {
            if (replacement is not null) await replacement.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task RemovalRequiresOneAcknowledgedLedgerInsert()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync(["probe.write"]);
        await fixture.SeedRemovalQuotaProjectionAsync(user.OrganizationId);
        await using UsageRemovalEnvironment environment = fixture.CreateUsageRemovalEnvironment();
        SeededUsageEffect source = await fixture.SeedUsageRemovalEffectAsync(user, units: 8);
        Guid operationId = Guid.CreateVersion7();
        await fixture.SuppressRemovalInsertAsync(operationId);
        try
        {
            await using MutationScope scope = await ExecutionFixture.BeginUploadMutationScopeAsync(environment.Services, user);
            OrganizationUsageRemovalResult result = await scope.Services.GetRequiredService<IOrganizationUsageLedger>()
                .RecordRemovalAsync(ProbeUsageResource, source.EffectId, 8, operationId);
            result.Disposition.ShouldBe(OrganizationUsageRemovalDisposition.Unavailable);
            (await fixture.ReadUsageEffectsAsync(user.OrganizationId, source.EffectId)).ShouldBe(1);
        }
        finally { await fixture.RemoveSuppressedRemovalInsertAsync(); }
    }

    private sealed class UsageRemovalEnvironment(ServiceProvider services) : IAsyncDisposable
    {
        public ServiceProvider Services { get; } = services;
        public ValueTask DisposeAsync() => Services.DisposeAsync();
    }

    private sealed class RemovalPolicyBehavior
    {
        public bool Fail { get; set; }
        public Func<CancellationToken, Task>? BeforeReturn { get; set; }
    }

    private sealed class RemovalProbeModule : IModule
    {
        private readonly ProbeModule inner = new();
        public ModuleDescriptor Descriptor { get; } = WithRemovalPolicy(new ProbeModule().Descriptor);

        private static ModuleDescriptor WithRemovalPolicy(ModuleDescriptor source) => source with
        {
            QuotaResources = source.QuotaResources.Select(resource => resource.Key == ProbeUsageResource
                ? resource with { RemovalPolicy = ProbeRemovalPolicy }
                : resource).ToArray()
        };

        public void Register(IServiceCollection services, IConfiguration configuration)
        {
            inner.Register(services, configuration);
            services.AddSingleton(new RemovalPolicyBehavior());
            services.AddScoped<IModuleUsageRemovalPolicy, ProbeRemovalPolicyAdapter>();
            services.AddScoped(provider => new ModuleUsageRemovalPolicyBinding(
                ProbeRemovalPolicy, provider.GetRequiredService<IModuleUsageRemovalPolicy>()));
        }
    }

    private sealed class ProbeRemovalPolicyAdapter(ApplicationDbContext application, OrganizationContext organization,
        IOrganizationAccessResolver accessResolver, RemovalPolicyBehavior behavior) : IModuleUsageRemovalPolicy
    {
        public async Task<bool> ConfirmAsync(ModuleUsageRemovalContext context, CancellationToken cancellationToken = default)
        {
            if (behavior.Fail) throw new InvalidOperationException("Injected removal confirmation failure.");
            string expectedMarker = $"permanent-removal/{context.SourceEffectId:N}";
            bool sourceIsOriginalPositiveEffect = await application.Database.SqlQuery<bool>($"""
                SELECT EXISTS (
                    SELECT 1 FROM app.organization_usage AS usage
                    JOIN app.probe_records AS record
                        ON record."OrganizationId" = usage."OrganizationId" AND record."Id" = usage."SourceId"
                        AND record."RecipientActorId" = usage."ActorId"
                    WHERE usage."OrganizationId" = {organization.OrganizationId} AND usage."ResourceKey" = {ProbeUsageResource}
                        AND usage."EffectId" = {context.SourceEffectId} AND usage."SourceId" = {context.SourceId}
                        AND usage."SourceEffectId" IS NULL AND usage."Units" = {context.OriginalUnits} AND usage."Units" > 0
                        AND NOT record."CanDisclose" AND record."Marker" = {expectedMarker})
                    AS "Value"
                """).SingleAsync(cancellationToken);
            if (!sourceIsOriginalPositiveEffect || context.RequestedUnits <= 0 || context.RequestedUnits > context.OriginalUnits)
                return false;
            bool confirmed;
            try
            {
                Guid membershipId = organization.MembershipId;
                OrganizationAccess? currentAccess = await accessResolver.ResolveAsync(
                    organization.ActorId, organization.OrganizationId, cancellationToken);
                confirmed = currentAccess is not null && currentAccess.MembershipId == membershipId
                    && currentAccess.Permissions.Contains("probe.write");
            }
            catch (OrganizationMembershipRequiredException)
            {
                // The host must independently validate the compiled owning system quota grant.
                confirmed = organization.ActorId == ProbeModule.AuditActorId;
            }
            if (confirmed && behavior.BeforeReturn is { } beforeReturn) await beforeReturn(cancellationToken);
            return confirmed;
        }
    }

    private sealed record SeededUsageEffect(Guid RecordId, Guid EffectId);

    private sealed partial class ExecutionFixture
    {
        public Task SuppressRemovalInsertAsync(Guid operationId) => ExecuteOwnerAsync($"""
            CREATE FUNCTION app.suppress_test_removal_insert() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
              IF NEW."EffectId" = '{operationId:D}'::uuid THEN RETURN NULL; END IF;
              RETURN NEW;
            END $$;
            CREATE TRIGGER suppress_test_removal_insert BEFORE INSERT ON app.organization_usage
              FOR EACH ROW EXECUTE FUNCTION app.suppress_test_removal_insert();
            """);

        public Task RemoveSuppressedRemovalInsertAsync() => ExecuteOwnerAsync("""
            DROP TRIGGER suppress_test_removal_insert ON app.organization_usage;
            DROP FUNCTION app.suppress_test_removal_insert();
            """);

        public Task RevokeRemovalPermissionAsync(Guid membershipId) => ExecuteOwnerAsync("""
            DELETE FROM platform.role_permissions AS permission
            USING platform.membership_roles AS membership
            WHERE permission."RoleId" = membership."RoleId" AND membership."MembershipId" = @membership
                AND permission."Permission" = 'probe.write'
            """, ("membership", membershipId));

        public UsageRemovalEnvironment CreateUsageRemovalEnvironment(Action<RemovalPolicyBehavior>? configurePolicy = null)
        {
            IConfiguration configuration = services.GetRequiredService<IConfiguration>();
            ServiceCollection registrations = new();
            registrations.AddSingleton(configuration);
            registrations.AddLogging();
            registrations.AddApplication();
            registrations.AddModules(configuration, [new ProjectsModule(), new DocumentsModule(), new RemovalProbeModule()]);
            registrations.AddInfrastructure(configuration);
            registrations.AddSingleton(new ProbeExecutionBehavior());
            ServiceProvider provider = registrations.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
            if (configurePolicy is not null)
            {
                using IServiceScope scope = provider.CreateScope();
                configurePolicy(scope.ServiceProvider.GetRequiredService<RemovalPolicyBehavior>());
            }
            return new(provider);
        }

        public async Task SeedRemovalQuotaProjectionAsync(Guid organizationId)
        {
            string hash = new('c', 64);
            await ExecuteOwnerAsync("""
                INSERT INTO app.organization_entitlements
                    ("OrganizationId", "ResourceKey", "OwnerModuleId", "Unit", "Mode", "Limit", "Version",
                     "Source", "SourceRevision", "SourceHash", "EffectiveAtUtc")
                VALUES (@organization, 'probe.units', 'probe', 2, 1, 100, @version,
                    'host-configuration', 1, @hash, CURRENT_TIMESTAMP);
                INSERT INTO platform.entitlement_publication_history
                    ("OrganizationId", "SourceRevision", "SourceHash", "Source", "SourceVersion", "ActorId", "InitialResourceCount", "PublishedAtUtc")
                VALUES (@organization, 1, @hash, 'host-configuration', 1, @publisher, 1, CURRENT_TIMESTAMP)
                """, ("organization", organizationId), ("version", Guid.CreateVersion7()), ("hash", hash),
                ("publisher", HostEntitlementPublisher.AuditActorId));
        }

        public async Task<SeededUsageEffect> SeedUsageRemovalEffectAsync(ProjectsSeededUser user, long units,
            string? marker = null, bool canDisclose = false, Guid? organizationId = null, Guid? recipientActorId = null)
        {
            Guid recordId = Guid.CreateVersion7();
            Guid effectId = Guid.CreateVersion7();
            Guid owningOrganizationId = organizationId ?? user.OrganizationId;
            Guid recipient = recipientActorId ?? user.ActorId;
            await ExecuteOwnerAsync("""
                INSERT INTO app.probe_records ("Id", "OrganizationId", "RecipientActorId", "CanDisclose", "Marker", "EffectCount")
                VALUES (@record, @organization, @recipient, @disclose, @marker, 0);
                INSERT INTO app.organization_usage
                    ("OrganizationId", "ResourceKey", "EffectId", "SourceId", "SourceEffectId", "ReservationId", "ActorId", "Units", "OccurredAtUtc")
                VALUES (@organization, 'probe.units', @effect, @record, NULL, NULL, @actor, @units, CURRENT_TIMESTAMP)
                """, ("record", recordId), ("organization", owningOrganizationId), ("recipient", recipient),
                ("disclose", canDisclose), ("marker", marker ?? $"permanent-removal/{effectId:N}"),
                ("effect", effectId), ("actor", user.ActorId), ("units", units));
            return new(recordId, effectId);
        }

        public Task<long> SumUsageForResourceAsync(Guid organizationId) => CountOwnerAsync("""
            SELECT COALESCE(sum("Units"), 0) FROM app.organization_usage
            WHERE "OrganizationId" = @organization AND "ResourceKey" = 'probe.units'
            """, ("organization", organizationId));

        public Task<long> ReadUsageEffectsAsync(Guid organizationId, Guid sourceEffectId) => CountOwnerAsync("""
            SELECT count(*) FROM app.organization_usage
            WHERE "OrganizationId" = @organization AND "ResourceKey" = 'probe.units'
                AND ("EffectId" = @effect OR "SourceEffectId" = @effect)
            """, ("organization", organizationId), ("effect", sourceEffectId));

    }
}

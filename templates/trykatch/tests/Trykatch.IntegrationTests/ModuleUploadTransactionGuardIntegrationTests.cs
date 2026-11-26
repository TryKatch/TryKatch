using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Infrastructure.Quotas;

namespace Trykatch.IntegrationTests;

public sealed partial class ModuleWorkExecutionIntegrationTests
{
    [TestMethod]
    public async Task IncompleteUploadFinalizationRejectsCommitAndRetryAndRollsBackProjectEffects()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Trykatch.Application.Authorization.Permissions.ProjectsManage]);
        await using (MutationScope scope = await fixture.BeginMutationScopeAsync(user))
        {
            UploadFinalizationCommitGuard guard = scope.Services.GetRequiredService<UploadFinalizationCommitGuard>();
            _ = guard.Begin(GetTransaction(scope));
            try
            {
                await CreateProjectAndSaveAuditAsync(scope, "Incomplete upload project");
                throw new IOException("Simulated failure after the Projects callback saved its effects.");
            }
            catch (IOException)
            {
                // The upload caller handles the callback failure, then its transaction owner attempts commit.
            }

            await Should.ThrowAsync<UploadFinalizationIncompleteException>(() => scope.CommitAsync());
            await Should.ThrowAsync<UploadFinalizationIncompleteException>(() => scope.CommitAsync());
        }

        ProjectProducerSnapshot snapshot = await fixture.ReadProjectProducerSnapshotAsync(user.OrganizationId);
        snapshot.ProjectCount.ShouldBe(0);
        snapshot.AuditCount.ShouldBe(0);
        snapshot.OutboxRows.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task SequentiallyCompletedUploadFinalizationsPermitAtomicProjectCommit()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Trykatch.Application.Authorization.Permissions.ProjectsManage]);
        await using (MutationScope scope = await fixture.BeginMutationScopeAsync(user))
        {
            UploadFinalizationCommitGuard guard = scope.Services.GetRequiredService<UploadFinalizationCommitGuard>();
            var transaction = GetTransaction(scope);
            UploadFinalizationCommitGuard.Attempt first = guard.Begin(transaction);
            await CreateProjectAndSaveAuditAsync(scope, "First completed upload project");
            guard.Complete(first);
            UploadFinalizationCommitGuard.Attempt second = guard.Begin(transaction);
            await CreateProjectAndSaveAuditAsync(scope, "Second completed upload project");
            guard.Complete(second);
            await scope.CommitAsync();
        }

        ProjectProducerSnapshot snapshot = await fixture.ReadProjectProducerSnapshotAsync(user.OrganizationId);
        snapshot.ProjectCount.ShouldBe(2);
        snapshot.AuditCount.ShouldBe(2);
        snapshot.OutboxRows.Count.ShouldBe(4);
    }

    [TestMethod]
    public async Task IncompleteLaterUploadPoisonsTransactionAfterEarlierSuccess()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Trykatch.Application.Authorization.Permissions.ProjectsManage]);
        await using (MutationScope scope = await fixture.BeginMutationScopeAsync(user))
        {
            UploadFinalizationCommitGuard guard = scope.Services.GetRequiredService<UploadFinalizationCommitGuard>();
            var transaction = GetTransaction(scope);
            UploadFinalizationCommitGuard.Attempt first = guard.Begin(transaction);
            await CreateProjectAndSaveAuditAsync(scope, "First upload succeeded");
            guard.Complete(first);
            _ = guard.Begin(transaction);
            await CreateProjectAndSaveAuditAsync(scope, "Second upload failed");

            await Should.ThrowAsync<UploadFinalizationIncompleteException>(() => scope.CommitAsync());
        }

        ProjectProducerSnapshot snapshot = await fixture.ReadProjectProducerSnapshotAsync(user.OrganizationId);
        snapshot.ProjectCount.ShouldBe(0);
        snapshot.AuditCount.ShouldBe(0);
        snapshot.OutboxRows.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task NestedUploadFinalizationCannotBeRecoveredByCompletingOuterAttempt()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Trykatch.Application.Authorization.Permissions.ProjectsManage]);
        await using MutationScope scope = await fixture.BeginMutationScopeAsync(user);
        UploadFinalizationCommitGuard guard = scope.Services.GetRequiredService<UploadFinalizationCommitGuard>();
        var transaction = GetTransaction(scope);
        UploadFinalizationCommitGuard.Attempt outer = guard.Begin(transaction);

        Should.Throw<InvalidOperationException>(() => guard.Begin(transaction));
        Should.Throw<UploadFinalizationIncompleteException>(() => guard.Complete(outer));

        await Should.ThrowAsync<UploadFinalizationIncompleteException>(() => scope.CommitAsync());
    }

    [TestMethod]
    public async Task SynchronousControlPlaneCommitRejectsIncompleteUploadFinalization()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Trykatch.Application.Authorization.Permissions.ProjectsManage]);
        await using MutationScope scope = await fixture.BeginMutationScopeAsync(user);
        UploadFinalizationCommitGuard guard = scope.Services.GetRequiredService<UploadFinalizationCommitGuard>();
        _ = guard.Begin(GetTransaction(scope));
        OrganizationControlPlaneDbContext control = scope.Services.GetRequiredService<OrganizationControlPlaneDbContext>();

        Should.Throw<UploadFinalizationIncompleteException>(() => control.Database.CurrentTransaction!.Commit());
        await Should.ThrowAsync<UploadFinalizationIncompleteException>(() => scope.CommitAsync());
    }

    [TestMethod]
    public async Task AsynchronousApplicationCommitRejectsIncompleteUploadFinalization()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Trykatch.Application.Authorization.Permissions.ProjectsManage]);
        await using MutationScope scope = await fixture.BeginMutationScopeAsync(user);
        UploadFinalizationCommitGuard guard = scope.Services.GetRequiredService<UploadFinalizationCommitGuard>();
        _ = guard.Begin(GetTransaction(scope));
        ApplicationDbContext application = scope.Services.GetRequiredService<ApplicationDbContext>();

        await Should.ThrowAsync<UploadFinalizationIncompleteException>(
            () => application.Database.CurrentTransaction!.CommitAsync(CancellationToken.None));
        await Should.ThrowAsync<UploadFinalizationIncompleteException>(() => scope.CommitAsync());
    }

    [TestMethod]
    public async Task StartingANewTransactionLifetimeClearsThePriorGuardState()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Trykatch.Application.Authorization.Permissions.ProjectsManage]);
        await using MutationScope scope = await fixture.BeginMutationScopeAsync(user);
        OrganizationControlPlaneDbContext control = scope.Services.GetRequiredService<OrganizationControlPlaneDbContext>();
        UploadFinalizationCommitGuard guard = scope.Services.GetRequiredService<UploadFinalizationCommitGuard>();
        _ = guard.Begin(GetTransaction(scope));

        IDbContextTransaction previous = control.Database.CurrentTransaction!;
        await previous.RollbackAsync(CancellationToken.None);
        await scope.Services.GetRequiredService<ApplicationDbContext>().Database.CurrentTransaction!.DisposeAsync();
        await previous.DisposeAsync();
        await using IDbContextTransaction nextTransaction = await control.Database.BeginTransactionAsync();

        await nextTransaction.CommitAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task OrdinaryProjectMutationWithoutUploadGuardStillCommits()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Trykatch.Application.Authorization.Permissions.ProjectsManage]);
        await using (MutationScope scope = await fixture.BeginMutationScopeAsync(user))
        {
            await CreateProjectAndSaveAuditAsync(scope, "Ordinary project");
            await scope.CommitAsync();
        }

        ProjectProducerSnapshot snapshot = await fixture.ReadProjectProducerSnapshotAsync(user.OrganizationId);
        snapshot.ProjectCount.ShouldBe(1);
        snapshot.AuditCount.ShouldBe(1);
        snapshot.OutboxRows.Count.ShouldBe(2);
    }

    private static System.Data.Common.DbTransaction GetTransaction(MutationScope scope)
    {
        OrganizationControlPlaneDbContext control = scope.Services.GetRequiredService<OrganizationControlPlaneDbContext>();
        return control.Database.CurrentTransaction!.GetDbTransaction();
    }
}

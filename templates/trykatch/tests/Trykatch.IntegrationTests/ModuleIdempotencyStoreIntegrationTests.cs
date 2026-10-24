using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Trykatch.Application.Auditing;
using Trykatch.Application.Authorization;
using Trykatch.Application.Common;
using Trykatch.Application.Organizations;
using Trykatch.Domain.Organizations;
using Trykatch.Infrastructure.Organizations;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Modules;
using Trykatch.Modules.Projects.Application;

namespace Trykatch.IntegrationTests;

public sealed partial class ModuleWorkExecutionIntegrationTests
{
    private static readonly ModuleMutationPolicy MutationPolicy = ModuleMutationPolicy.Default;

    [TestMethod]
    public async Task IdempotencyStoreCommitsProjectAndReturnsTheExactCompletedReceipt()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        MutationRequest request = CreateMutationRequest();
        byte[] body = Encoding.UTF8.GetBytes("{\"id\":\"project-result\"}");
        MutationReceipt receipt = new(StatusCodes.Status201Created, "application/json", "/api/v1/projects/project-result",
            body, new MutationResourceReference("projects", Guid.CreateVersion7()));

        await using (MutationScope scope = await fixture.BeginMutationScopeAsync(user))
        {
            MutationReservationResult reservation = await scope.Store.ReserveAsync(request, AllowRequestAsync);
            reservation.State.ShouldBe(MutationReservationState.Reserved);
            reservation.Authority.ShouldNotBeNull();
            reservation.Authority!.OrganizationId.ShouldBe(user.OrganizationId);
            reservation.Authority.ActorId.ShouldBe(user.ActorId);
            reservation.Authority.MembershipId.ShouldBe(user.MembershipId);

            Guid projectId = await CreateProjectAndSaveAuditAsync(scope, "Receipt project");
            receipt = receipt with
            {
                Body = Encoding.UTF8.GetBytes($"{{\"id\":\"{projectId:D}\"}}"),
                Location = $"/api/v1/projects/{projectId:D}",
                ResourceReference = new MutationResourceReference("projects", projectId)
            };
            await scope.Store.CompleteAsync(reservation.ReservationId!.Value, receipt);
            await scope.CommitAsync();
        }

        ProjectProducerSnapshot snapshot = await fixture.ReadProjectProducerSnapshotAsync(user.OrganizationId);
        snapshot.ProjectCount.ShouldBe(1);
        snapshot.AuditCount.ShouldBe(1);
        snapshot.OutboxRows.Count.ShouldBe(2);

        await using MutationScope replayScope = await fixture.BeginMutationScopeAsync(user);
        MutationReservationResult replay = await replayScope.Store.ReserveAsync(request, AllowRequestAsync);
        replay.State.ShouldBe(MutationReservationState.Completed);
        replay.Receipt.ShouldNotBeNull();
        replay.Receipt!.StatusCode.ShouldBe(StatusCodes.Status201Created);
        replay.Receipt.ContentType.ShouldBe("application/json");
        replay.Receipt.Location.ShouldBe(receipt.Location);
        replay.Receipt.Body.SequenceEqual(receipt.Body).ShouldBeTrue();
        replay.Receipt.ResourceReference.ShouldBe(receipt.ResourceReference);
    }

    [TestMethod]
    public async Task IdempotencyStoreReportsInProgressThenCompletedForConcurrentSameIdentity()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        MutationRequest request = CreateMutationRequest();
        await using MutationScope first = await fixture.BeginMutationScopeAsync(user);
        MutationReservationResult reservation = await first.Store.ReserveAsync(request, AllowRequestAsync);
        reservation.State.ShouldBe(MutationReservationState.Reserved);

        await using (MutationScope competing = await fixture.BeginMutationScopeAsync(user))
        {
            MutationReservationResult busy = await competing.Store.ReserveAsync(request, AllowRequestAsync)
                .WaitAsync(TimeSpan.FromSeconds(5));
            busy.State.ShouldBe(MutationReservationState.InProgress);
        }

        Guid projectId = await CreateProjectAndSaveAuditAsync(first, "Concurrent project");
        MutationReceipt receipt = ReceiptFor(projectId);
        await first.Store.CompleteAsync(reservation.ReservationId!.Value, receipt);
        await first.CommitAsync();

        await using MutationScope replayScope = await fixture.BeginMutationScopeAsync(user);
        MutationReservationResult replay = await replayScope.Store.ReserveAsync(request, AllowRequestAsync);
        replay.State.ShouldBe(MutationReservationState.Completed);
        replay.Receipt.ShouldNotBeNull();
        replay.Receipt!.StatusCode.ShouldBe(receipt.StatusCode);
        replay.Receipt.ContentType.ShouldBe(receipt.ContentType);
        replay.Receipt.Location.ShouldBe(receipt.Location);
        replay.Receipt.Body.SequenceEqual(receipt.Body).ShouldBeTrue();
        replay.Receipt.ResourceReference.ShouldBe(receipt.ResourceReference);

        ProjectProducerSnapshot snapshot = await fixture.ReadProjectProducerSnapshotAsync(user.OrganizationId);
        snapshot.ProjectCount.ShouldBe(1);
        snapshot.AuditCount.ShouldBe(1);
        snapshot.OutboxRows.Count.ShouldBe(2);
    }

    [TestMethod]
    public async Task IdempotencyStoreConflictsWhenTheSameKeyHasDifferentRequestIntent()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        MutationRequest request = CreateMutationRequest();
        await using (MutationScope scope = await fixture.BeginMutationScopeAsync(user))
        {
            MutationReservationResult first = await scope.Store.ReserveAsync(request, AllowRequestAsync);
            first.State.ShouldBe(MutationReservationState.Reserved);
            Guid projectId = await CreateProjectAndSaveAuditAsync(scope, "Conflict source");
            await scope.Store.CompleteAsync(first.ReservationId!.Value, ReceiptFor(projectId));
            await scope.CommitAsync();
        }

        await using MutationScope conflictScope = await fixture.BeginMutationScopeAsync(user);
        MutationReservationResult conflict = await conflictScope.Store.ReserveAsync(
            CreateMutationRequest(requestHash: new string('C', 64)), AllowRequestAsync);
        conflict.State.ShouldBe(MutationReservationState.Conflict);
        conflict.Receipt.ShouldBeNull();
        ProjectProducerSnapshot snapshot = await fixture.ReadProjectProducerSnapshotAsync(user.OrganizationId);
        snapshot.ProjectCount.ShouldBe(1);
        snapshot.AuditCount.ShouldBe(1);
        snapshot.OutboxRows.Count.ShouldBe(2);
    }

    [TestMethod]
    public async Task IdempotencyStoreRollbackReleasesReservationAndReceiptForRetry()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        MutationRequest request = CreateMutationRequest();

        await using (MutationScope rolledBack = await fixture.BeginMutationScopeAsync(user))
        {
            MutationReservationResult reservation = await rolledBack.Store.ReserveAsync(request, AllowRequestAsync);
            reservation.State.ShouldBe(MutationReservationState.Reserved);
            Guid projectId = await CreateProjectAndSaveAuditAsync(rolledBack, "Rolled-back project");
            await rolledBack.Store.CompleteAsync(reservation.ReservationId!.Value, ReceiptFor(projectId));
        }

        (await fixture.ReadProjectProducerSnapshotAsync(user.OrganizationId)).ShouldBeEmpty();

        await using MutationScope retry = await fixture.BeginMutationScopeAsync(user);
        MutationReservationResult retried = await retry.Store.ReserveAsync(request, AllowRequestAsync);
        retried.State.ShouldBe(MutationReservationState.Reserved);
        Guid committedProject = await CreateProjectAndSaveAuditAsync(retry, "Retried project");
        await retry.Store.CompleteAsync(retried.ReservationId!.Value, ReceiptFor(committedProject));
        await retry.CommitAsync();

        ProjectProducerSnapshot snapshot = await fixture.ReadProjectProducerSnapshotAsync(user.OrganizationId);
        snapshot.ProjectCount.ShouldBe(1);
        snapshot.AuditCount.ShouldBe(1);
        snapshot.OutboxRows.Count.ShouldBe(2);
    }

    [TestMethod]
    public async Task IdempotencyStoreRunsTheRequestAuthorizationCallbackBeforeReturningAReceipt()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        MutationRequest request = CreateMutationRequest();
        await using (MutationScope source = await fixture.BeginMutationScopeAsync(user))
        {
            MutationReservationResult reservation = await source.Store.ReserveAsync(request, AllowRequestAsync);
            reservation.State.ShouldBe(MutationReservationState.Reserved);
            Guid projectId = await CreateProjectAndSaveAuditAsync(source, "Authorization source");
            await source.Store.CompleteAsync(reservation.ReservationId!.Value, ReceiptFor(projectId));
            await source.CommitAsync();
        }

        await using MutationScope retry = await fixture.BeginMutationScopeAsync(user);
        int calls = 0;
        MutationAuthority? observed = null;
        MutationReservationResult denied = await retry.Store.ReserveAsync(request, (authority, _) =>
        {
            calls++;
            observed = authority;
            return Task.FromResult(false);
        });

        calls.ShouldBe(1);
        observed.ShouldNotBeNull();
        observed!.OrganizationId.ShouldBe(user.OrganizationId);
        observed.ActorId.ShouldBe(user.ActorId);
        observed.MembershipId.ShouldBe(user.MembershipId);
        observed.Permissions.ShouldContain(Permissions.ProjectsManage);
        denied.State.ShouldBe(MutationReservationState.Denied);
        denied.Receipt.ShouldBeNull();
    }

    [TestMethod]
    public async Task IdempotencyStoreRefreshesPermissionBeforeDisclosingAnExistingReceipt()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        MutationRequest request = CreateMutationRequest();
        await using (MutationScope source = await fixture.BeginMutationScopeAsync(user))
        {
            MutationReservationResult reservation = await source.Store.ReserveAsync(request, AllowRequestAsync);
            reservation.State.ShouldBe(MutationReservationState.Reserved);
            Guid projectId = await CreateProjectAndSaveAuditAsync(source, "Permission source");
            await source.Store.CompleteAsync(reservation.ReservationId!.Value, ReceiptFor(projectId));
            await source.CommitAsync();
        }

        await using MutationScope staleContext = await fixture.BeginMutationScopeAsync(user);
        staleContext.OrganizationContext.Permissions.ShouldContain(Permissions.ProjectsManage);
        await fixture.RemoveProjectsManagePermissionAsync(user.OrganizationId);

        MutationReservationResult denied = await staleContext.Store.ReserveAsync(request, AllowRequestAsync);
        denied.State.ShouldBe(MutationReservationState.Denied);
        denied.Receipt.ShouldBeNull();
    }

    [TestMethod]
    public async Task IdempotencyStoreRefreshesMembershipBeforeDisclosingAnExistingReceipt()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        MutationRequest request = CreateMutationRequest();
        await using (MutationScope source = await fixture.BeginMutationScopeAsync(user))
        {
            MutationReservationResult reservation = await source.Store.ReserveAsync(request, AllowRequestAsync);
            reservation.State.ShouldBe(MutationReservationState.Reserved);
            Guid projectId = await CreateProjectAndSaveAuditAsync(source, "Membership source");
            await source.Store.CompleteAsync(reservation.ReservationId!.Value, ReceiptFor(projectId));
            await source.CommitAsync();
        }

        await using MutationScope staleContext = await fixture.BeginMutationScopeAsync(user);
        staleContext.OrganizationContext.MembershipId.ShouldBe(user.MembershipId);
        (await fixture.RevokeMembershipAsync(user.OrganizationId, user.ActorId)).ShouldBe(1);

        MutationReservationResult denied = await staleContext.Store.ReserveAsync(request, AllowRequestAsync);
        denied.State.ShouldBe(MutationReservationState.Denied);
        denied.Receipt.ShouldBeNull();
    }

    [TestMethod]
    public async Task IdempotencyStoreRejectsUnenlistedAndMissingTransactions()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        MutationRequest request = CreateMutationRequest();
        await using (AsyncServiceScope scope = fixture.CreateScope())
        {
            IModuleMutationIdempotencyStore store = scope.ServiceProvider.GetRequiredService<IModuleMutationIdempotencyStore>();
            await Should.ThrowAsync<InvalidOperationException>(() => store.ReserveAsync(request, AllowRequestAsync));
        }

        await using MutationScope unenlisted = await fixture.BeginUnenlistedMutationScopeAsync(user);
        await Should.ThrowAsync<InvalidOperationException>(() => unenlisted.Store.ReserveAsync(request, AllowRequestAsync));
    }

    [TestMethod]
    public async Task IdempotencyStoreRejectsWrongScopeTokenAndDoubleCompletion()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        MutationRequest request = CreateMutationRequest();
        await using MutationScope original = await fixture.BeginMutationScopeAsync(user);
        MutationReservationResult reservation = await original.Store.ReserveAsync(request, AllowRequestAsync);
        reservation.State.ShouldBe(MutationReservationState.Reserved);
        MutationReceipt receipt = ReceiptFor(Guid.CreateVersion7());

        await Should.ThrowAsync<InvalidOperationException>(() => original.Store.CompleteAsync(Guid.NewGuid(), receipt));

        await using (MutationScope foreign = await fixture.BeginMutationScopeAsync(user))
            await Should.ThrowAsync<InvalidOperationException>(() => foreign.Store.CompleteAsync(reservation.ReservationId!.Value, receipt));

        await original.Store.CompleteAsync(reservation.ReservationId!.Value, receipt);
        await Should.ThrowAsync<InvalidOperationException>(() => original.Store.CompleteAsync(reservation.ReservationId!.Value, receipt));
        await original.CommitAsync();
    }

    [TestMethod]
    public async Task IdempotencyStoreDoesNotDiscloseBusyOrConflictBeforeRequestAuthorization()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        MutationRequest request = CreateMutationRequest();
        await using MutationScope pending = await fixture.BeginMutationScopeAsync(user);
        MutationReservationResult sourceReservation = await pending.Store.ReserveAsync(request, AllowRequestAsync);
        sourceReservation.State.ShouldBe(MutationReservationState.Reserved);

        await using (MutationScope competing = await fixture.BeginMutationScopeAsync(user))
        {
            int callbackCalls = 0;
            MutationReservationResult deniedBusy = await competing.Store.ReserveAsync(request, (_, _) =>
            {
                callbackCalls++;
                return Task.FromResult(false);
            }).WaitAsync(TimeSpan.FromSeconds(5));
            callbackCalls.ShouldBe(1);
            deniedBusy.State.ShouldBe(MutationReservationState.Denied);
            deniedBusy.Receipt.ShouldBeNull();
        }

        Guid projectId = await CreateProjectAndSaveAuditAsync(pending, "Authorization conflict source");
        await pending.Store.CompleteAsync(sourceReservation.ReservationId!.Value, ReceiptFor(projectId));
        await pending.CommitAsync();

        await using MutationScope conflict = await fixture.BeginMutationScopeAsync(user);
        int conflictCallbackCalls = 0;
        MutationReservationResult deniedConflict = await conflict.Store.ReserveAsync(
            CreateMutationRequest(requestHash: new string('C', 64)), (_, _) =>
            {
                conflictCallbackCalls++;
                return Task.FromResult(false);
            });
        conflictCallbackCalls.ShouldBe(1);
        deniedConflict.State.ShouldBe(MutationReservationState.Denied);
        deniedConflict.Receipt.ShouldBeNull();
    }

    [TestMethod]
    public async Task IdempotencyStoreRejectsAnInitializedMembershipThatDiffersFromFreshResolution()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        await using MutationScope scope = await fixture.BeginMutationScopeAsync(user, Guid.CreateVersion7());

        MutationReservationResult result = await scope.Store.ReserveAsync(CreateMutationRequest(), AllowRequestAsync);

        result.State.ShouldBe(MutationReservationState.Denied);
        result.Receipt.ShouldBeNull();
    }

    [TestMethod]
    public async Task IdempotencyStoreValidatesReceiptStatusAndCurrentResponseBounds()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        MutationRequest request = CreateMutationRequest(policy: new ModuleMutationPolicy(TimeSpan.FromMinutes(1), MaximumResponseBytes: 8));
        await using MutationScope scope = await fixture.BeginMutationScopeAsync(user);
        MutationReservationResult reservation = await scope.Store.ReserveAsync(request, AllowRequestAsync);
        reservation.State.ShouldBe(MutationReservationState.Reserved);

        MutationReceipt valid = new(StatusCodes.Status201Created, "application/json", "/api/v1/projects/receipt",
            Encoding.UTF8.GetBytes("{}"));
        await Should.ThrowAsync<ArgumentException>(() => scope.Store.CompleteAsync(reservation.ReservationId!.Value,
            valid with { StatusCode = StatusCodes.Status400BadRequest }));
        await Should.ThrowAsync<ArgumentException>(() => scope.Store.CompleteAsync(reservation.ReservationId!.Value,
            valid with { Body = new byte[9] }));

        await scope.Store.CompleteAsync(reservation.ReservationId!.Value, valid);
        await scope.CommitAsync();
    }

    [TestMethod]
    public async Task IdempotencyStoreRejectsCompletionAfterItsTransactionChanges()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        await using MutationScope scope = await fixture.BeginMutationScopeAsync(user);
        MutationReservationResult reservation = await scope.Store.ReserveAsync(CreateMutationRequest(), AllowRequestAsync);
        reservation.State.ShouldBe(MutationReservationState.Reserved);
        await scope.CommitAsync();

        await Should.ThrowAsync<InvalidOperationException>(() => scope.Store.CompleteAsync(
            reservation.ReservationId!.Value, ReceiptFor(Guid.CreateVersion7())));
    }

    [TestMethod]
    public async Task IdempotencyStoreAllowsIndependentActorsOrganizationsModulesAndOperations()
    {
        ProjectsSeededUser original = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        ProjectsSeededUser otherActor = await fixture.SeedAdditionalProjectsActorAsync(original.OrganizationId);
        ProjectsSeededUser sameActorOtherOrganization = await fixture.SeedProjectsOrganizationForActorAsync(original.ActorId);
        List<MutationScope> scopes = [];
        try
        {
            scopes.Add(await fixture.BeginMutationScopeAsync(original));
            scopes.Add(await fixture.BeginMutationScopeAsync(original));
            scopes.Add(await fixture.BeginMutationScopeAsync(original));
            scopes.Add(await fixture.BeginMutationScopeAsync(otherActor));
            scopes.Add(await fixture.BeginMutationScopeAsync(sameActorOtherOrganization));

            MutationRequest[] requests =
            [
                CreateMutationRequest(),
                CreateMutationRequest(moduleId: "documents"),
                CreateMutationRequest(operationId: "projects.update"),
                CreateMutationRequest(),
                CreateMutationRequest()
            ];
            ProjectsSeededUser[] users = [original, original, original, otherActor, sameActorOtherOrganization];
            for (int index = 0; index < scopes.Count; index++)
            {
                MutationReservationResult reservation = await scopes[index].Store.ReserveAsync(requests[index], AllowRequestAsync);
                reservation.State.ShouldBe(MutationReservationState.Reserved);
                reservation.Authority!.OrganizationId.ShouldBe(users[index].OrganizationId);
                reservation.Authority.ActorId.ShouldBe(users[index].ActorId);
            }
        }
        finally
        {
            foreach (MutationScope scope in scopes.AsEnumerable().Reverse())
                await scope.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task IdempotencyStoreReusesAnExpiredIdentityAndReplacesItOnlyOnCommit()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        MutationRequest request = CreateMutationRequest(policy: new ModuleMutationPolicy(TimeSpan.FromMinutes(1)));
        await using (MutationScope source = await fixture.BeginMutationScopeAsync(user))
        {
            MutationReservationResult reservation = await source.Store.ReserveAsync(request, AllowRequestAsync);
            reservation.State.ShouldBe(MutationReservationState.Reserved);
            Guid projectId = await CreateProjectAndSaveAuditAsync(source, "Expired receipt source");
            await source.Store.CompleteAsync(reservation.ReservationId!.Value, ReceiptFor(projectId));
            await source.CommitAsync();
        }

        await fixture.ExpireMutationReceiptAsync(user.OrganizationId, user.ActorId, request);
        await using (MutationScope retry = await fixture.BeginMutationScopeAsync(user))
        {
            MutationReservationResult reservation = await retry.Store.ReserveAsync(request, AllowRequestAsync);
            reservation.State.ShouldBe(MutationReservationState.Reserved);
        }

        await using MutationScope freshRetry = await fixture.BeginMutationScopeAsync(user);
        MutationReservationResult retried = await freshRetry.Store.ReserveAsync(request, AllowRequestAsync);
        retried.State.ShouldBe(MutationReservationState.Reserved);
        Guid newProjectId = await CreateProjectAndSaveAuditAsync(freshRetry, "Replacement receipt source");
        await freshRetry.Store.CompleteAsync(retried.ReservationId!.Value, ReceiptFor(newProjectId));
        await freshRetry.CommitAsync();
        ProjectProducerSnapshot snapshot = await fixture.ReadProjectProducerSnapshotAsync(user.OrganizationId);
        snapshot.ProjectCount.ShouldBe(2);
        snapshot.AuditCount.ShouldBe(2);
        snapshot.OutboxRows.Count.ShouldBe(4);
    }

    [TestMethod]
    public async Task IdempotencyStoreHonorsReducedReplayLifetimeWithoutReplacingARolledBackReceipt()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        MutationRequest request = CreateMutationRequest();
        MutationReceipt originalReceipt = ReceiptFor(Guid.CreateVersion7());
        await using (MutationScope original = await fixture.BeginMutationScopeAsync(user))
        {
            MutationReservationResult reserved = await original.Store.ReserveAsync(request, AllowRequestAsync);
            reserved.State.ShouldBe(MutationReservationState.Reserved);
            await original.Store.CompleteAsync(reserved.ReservationId!.Value, originalReceipt);
            await original.CommitAsync();
        }
        await fixture.AgeMutationReceiptAsync(user, TimeSpan.FromMinutes(2));
        MutationRequest shorter = request with { Policy = new(TimeSpan.FromMinutes(1)) };
        await using (MutationScope retry = await fixture.BeginMutationScopeAsync(user))
        {
            MutationReservationResult reserved = await retry.Store.ReserveAsync(shorter, AllowRequestAsync);
            reserved.State.ShouldBe(MutationReservationState.Reserved);
            await retry.Store.CompleteAsync(reserved.ReservationId!.Value,
                new MutationReceipt(202, "application/json", null, Encoding.UTF8.GetBytes("{}")));
            // Disposal rolls back the replacement as well as the reservation lock.
        }
        await using MutationScope replay = await fixture.BeginMutationScopeAsync(user);
        MutationReservationResult result = await replay.Store.ReserveAsync(request, AllowRequestAsync);
        result.State.ShouldBe(MutationReservationState.Completed);
        result.Receipt!.StatusCode.ShouldBe(originalReceipt.StatusCode);
        result.Receipt.Body.ShouldBe(originalReceipt.Body);
        result.Receipt.Location.ShouldBe(originalReceipt.Location);
    }

    [TestMethod]
    public async Task IdempotencyStoreDoesNotDiscloseReceiptAboveCurrentResponseBound()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        MutationRequest request = CreateMutationRequest();
        await using (MutationScope original = await fixture.BeginMutationScopeAsync(user))
        {
            MutationReservationResult reserved = await original.Store.ReserveAsync(request, AllowRequestAsync);
            reserved.State.ShouldBe(MutationReservationState.Reserved);
            await original.Store.CompleteAsync(reserved.ReservationId!.Value, ReceiptFor(Guid.CreateVersion7()));
            await original.CommitAsync();
        }
        await using MutationScope replay = await fixture.BeginMutationScopeAsync(user);
        MutationReservationResult result = await replay.Store.ReserveAsync(
            request with { Policy = request.Policy with { MaximumResponseBytes = 1 } }, AllowRequestAsync);
        result.State.ShouldBe(MutationReservationState.Denied);
        result.Receipt.ShouldBeNull();
    }

    private static MutationRequest CreateMutationRequest(
        string moduleId = "projects",
        string operationId = "projects.create",
        string requestHash = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB",
        ModuleMutationPolicy? policy = null) =>
        new(moduleId, operationId,
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
            requestHash,
            policy ?? MutationPolicy,
            [Permissions.ProjectsManage]);

    private static Task<bool> AllowRequestAsync(MutationAuthority _, CancellationToken __) => Task.FromResult(true);

    private static MutationReceipt ReceiptFor(Guid projectId) => new(
        StatusCodes.Status201Created,
        "application/json",
        $"/api/v1/projects/{projectId:D}",
        Encoding.UTF8.GetBytes($"{{\"id\":\"{projectId:D}\"}}"),
        new MutationResourceReference("projects", projectId));

    private static async Task<Guid> CreateProjectAndSaveAuditAsync(MutationScope scope, string name)
    {
        Result<ProjectDto> created = await scope.Services.GetRequiredService<ProjectUseCases>()
            .CreateAsync(new CreateProjectCommand(name, "Mutation receipt integration"), CancellationToken.None);
        created.IsSuccess.ShouldBeTrue();
        await scope.Services.GetRequiredService<IAuditWriter>().SaveChangesAsync(CancellationToken.None);
        return created.Value!.Id;
    }

    private sealed class MutationScope(
        AsyncServiceScope scope,
        IOrganizationDataScope transaction,
        IDbContextTransaction applicationEnlistment,
        IModuleMutationIdempotencyStore store,
        OrganizationContext organizationContext) : IAsyncDisposable
    {
        public IServiceProvider Services => scope.ServiceProvider;
        public IModuleMutationIdempotencyStore Store { get; } = store;
        public OrganizationContext OrganizationContext { get; } = organizationContext;
        public Task CommitAsync() => transaction.CommitAsync(CancellationToken.None);

        public async ValueTask DisposeAsync()
        {
            await applicationEnlistment.DisposeAsync();
            await transaction.DisposeAsync();
            await scope.DisposeAsync();
        }
    }

    private sealed partial class ExecutionFixture
    {
        public async Task AgeMutationReceiptAsync(ProjectsSeededUser user, TimeSpan age)
        {
            await using NpgsqlConnection connection = new(owner);
            await connection.OpenAsync();
            await using NpgsqlCommand command = new("""
                UPDATE app.mutation_receipts
                SET "CompletedAtUtc" = "CompletedAtUtc" - @age, "ExpiresAtUtc" = "ExpiresAtUtc" - @age
                WHERE "OrganizationId" = @organization AND "ActorId" = @actor
                """, connection);
            command.Parameters.AddWithValue("age", age);
            command.Parameters.AddWithValue("organization", user.OrganizationId);
            command.Parameters.AddWithValue("actor", user.ActorId);
            (await command.ExecuteNonQueryAsync()).ShouldBe(1);
        }

        public AsyncServiceScope CreateScope() => services.CreateAsyncScope();

        public async Task<MutationScope> BeginMutationScopeAsync(ProjectsSeededUser user, Guid? initializedMembershipOverride = null)
        {
            AsyncServiceScope scope = services.CreateAsyncScope();
            IOrganizationDataScope? transaction = null;
            IDbContextTransaction? enlistment = null;
            try
            {
                transaction = await scope.ServiceProvider.GetRequiredService<IOrganizationDataScopeFactory>()
                    .BeginAsync(user.OrganizationId, user.ActorId, CancellationToken.None);
                OrganizationControlPlaneDbContext control = scope.ServiceProvider.GetRequiredService<OrganizationControlPlaneDbContext>();
                ApplicationDbContext application = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                enlistment = await OrganizationTransactionEnlistment.EnlistAsync(
                    control, application, user.OrganizationId, user.ActorId, CancellationToken.None);
                OrganizationAccess access = (await scope.ServiceProvider.GetRequiredService<IOrganizationAccessResolver>()
                    .ResolveAsync(user.ActorId, user.OrganizationId, CancellationToken.None))!;
                scope.ServiceProvider.GetRequiredService<IOrganizationContextInitializer>()
                    .Initialize(initializedMembershipOverride is { } membershipId ? access with { MembershipId = membershipId } : access);
                return new(scope, transaction, enlistment,
                    scope.ServiceProvider.GetRequiredService<IModuleMutationIdempotencyStore>(),
                    scope.ServiceProvider.GetRequiredService<OrganizationContext>());
            }
            catch
            {
                if (enlistment is not null) await enlistment.DisposeAsync();
                if (transaction is not null) await transaction.DisposeAsync();
                await scope.DisposeAsync();
                throw;
            }
        }

        public async Task<MutationScope> BeginUnenlistedMutationScopeAsync(ProjectsSeededUser user)
        {
            AsyncServiceScope scope = services.CreateAsyncScope();
            IOrganizationDataScope? transaction = null;
            IDbContextTransaction? enlistment = null;
            try
            {
                OrganizationControlPlaneDbContext control = scope.ServiceProvider.GetRequiredService<OrganizationControlPlaneDbContext>();
                enlistment = await control.Database.BeginTransactionAsync();
                await control.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT set_config('app.organization_id', {user.OrganizationId.ToString()}, true), set_config('app.actor_id', {user.ActorId.ToString()}, true)");
                OrganizationAccess access = (await scope.ServiceProvider.GetRequiredService<IOrganizationAccessResolver>()
                    .ResolveAsync(user.ActorId, user.OrganizationId, CancellationToken.None))!;
                scope.ServiceProvider.GetRequiredService<IOrganizationContextInitializer>().Initialize(access);
                return new(scope,
                    new NoopOrganizationDataScope(),
                    enlistment,
                    scope.ServiceProvider.GetRequiredService<IModuleMutationIdempotencyStore>(),
                    scope.ServiceProvider.GetRequiredService<OrganizationContext>());
            }
            catch
            {
                if (enlistment is not null) await enlistment.DisposeAsync();
                if (transaction is not null) await transaction.DisposeAsync();
                await scope.DisposeAsync();
                throw;
            }
        }

        public async Task<ProjectsSeededUser> SeedAdditionalProjectsActorAsync(Guid organizationId)
        {
            Guid actorId = Guid.CreateVersion7();
            Role role = Role.Create(organizationId, $"Mutation actor {actorId:N}", isSystem: true);
            role.SetPermissions([Permissions.ProjectsManage]);
            Membership membership = Membership.Create(organizationId, actorId);
            membership.AssignRole(role.Id);
            await using PlatformDbContext platform = new(new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(owner).Options);
            platform.AddRange(role, membership);
            await platform.SaveChangesAsync();
            return new(organizationId, actorId, membership.Id);
        }

        public async Task<ProjectsSeededUser> SeedProjectsOrganizationForActorAsync(Guid actorId)
        {
            Organization organization = Organization.Create("Mutation Idempotency", $"mutation-{Guid.NewGuid():N}"[..29]);
            Role role = Role.Create(organization.Id, "Mutation actor", isSystem: true);
            role.SetPermissions([Permissions.ProjectsManage]);
            Membership membership = Membership.Create(organization.Id, actorId);
            membership.AssignRole(role.Id);
            OrganizationDataPlacementRecord placement = OrganizationDataPlacementRecord.Begin(
                organization.Id, OrganizationDataPlacementKind.Shared, "postgres", null);
            placement.MarkReady(new NpgsqlConnectionStringBuilder(owner).Database!, "integration-test", "baseline", DateTimeOffset.UtcNow);
            await using PlatformDbContext platform = new(new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(owner).Options);
            platform.AddRange(organization, role, membership, placement);
            await platform.SaveChangesAsync();
            return new(organization.Id, actorId, membership.Id);
        }

        public async Task RemoveProjectsManagePermissionAsync(Guid organizationId)
        {
            await using NpgsqlConnection connection = new(owner);
            await connection.OpenAsync();
            await using NpgsqlCommand command = new("""
                DELETE FROM platform.role_permissions AS permission
                USING platform.roles AS role
                WHERE permission."RoleId" = role."Id"
                  AND role."OrganizationId" = @organization
                  AND permission."Permission" = @permission
                """, connection);
            command.Parameters.AddWithValue("organization", organizationId);
            command.Parameters.AddWithValue("permission", Permissions.ProjectsManage);
            (await command.ExecuteNonQueryAsync()).ShouldBeGreaterThan(0);
        }

        public async Task ExpireMutationReceiptAsync(Guid organizationId, Guid actorId, MutationRequest request)
        {
            await using NpgsqlConnection connection = new(owner);
            await connection.OpenAsync();
            await using NpgsqlCommand command = new("""
                UPDATE app.mutation_receipts
                SET "CompletedAtUtc" = CURRENT_TIMESTAMP - INTERVAL '2 minutes',
                    "ExpiresAtUtc" = CURRENT_TIMESTAMP - INTERVAL '1 minute'
                WHERE "OrganizationId" = @organization
                  AND "ActorId" = @actor
                  AND "ModuleId" = @module
                  AND "OperationId" = @operation
                  AND "KeyHash" = @keyHash
                """, connection);
            command.Parameters.AddWithValue("organization", organizationId);
            command.Parameters.AddWithValue("actor", actorId);
            command.Parameters.AddWithValue("module", request.ModuleId);
            command.Parameters.AddWithValue("operation", request.OperationId);
            command.Parameters.AddWithValue("keyHash", request.KeyHash);
            (await command.ExecuteNonQueryAsync()).ShouldBe(1);
        }
    }

    private sealed class NoopOrganizationDataScope : IOrganizationDataScope
    {
        public Task CommitAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

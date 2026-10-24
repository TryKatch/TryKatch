using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using Trykatch.Application;
using Trykatch.Application.Authorization;
using Trykatch.Application.Organizations;
using Trykatch.Application.Quotas;
using Trykatch.Infrastructure;
using Trykatch.Infrastructure.Organizations;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Modules;
using Trykatch.Modules.Projects.Infrastructure;
using Trykatch.Modules.Documents.Infrastructure;

namespace Trykatch.IntegrationTests;

public sealed partial class ModuleWorkExecutionIntegrationTests
{
    private static readonly ModuleUploadPolicyRef TestUploadPolicy = new("documents", "documents.upload", 1);

    [TestMethod]
    public async Task UploadReservationSurvivesParentRollbackAndReturnsStableOpaqueReceiptOnRetry()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync(["documents.manage"]);
        await fixture.SeedQuotaProjectionAsync(user.OrganizationId, limit: 64);
        await using UploadAdmissionEnvironment environment = fixture.CreateUploadAdmissionEnvironment();
        ModuleUploadRequest request = CreateUploadRequest();
        ModuleUploadReceipt originalReceipt;

        await using (MutationScope parent = await ExecutionFixture.BeginUploadMutationScopeAsync(environment.Services, user))
        {
            ModuleUploadReserveResult reserved = await parent.Services.GetRequiredService<IModuleUploadAdmission>()
                .ReserveAsync(request);
            reserved.Disposition.ShouldBe(ModuleUploadReserveDisposition.Reserved);
            reserved.Receipt.ShouldNotBeNull();
            originalReceipt = reserved.Receipt!;
        }

        (await fixture.CountQuotaReservationsForOperationAsync(user.OrganizationId, request.OperationId)).ShouldBe(1);
        (await fixture.CountQuotaRowsAsync("module_upload_operations", user.OrganizationId)).ShouldBe(1);
        (await fixture.CountUploadRecoveryWorkAsync(user.OrganizationId, request.OperationId)).ShouldBe(1);

        await using MutationScope retryScope = await ExecutionFixture.BeginUploadMutationScopeAsync(environment.Services, user);
        ModuleUploadReserveResult retry = await retryScope.Services.GetRequiredService<IModuleUploadAdmission>()
            .ReserveAsync(request);

        retry.Disposition.ShouldBe(ModuleUploadReserveDisposition.AlreadyReserved);
        retry.Receipt.ShouldNotBeNull();
        retry.Receipt!.Policy.ShouldBe(originalReceipt.Policy);
        retry.Receipt.OperationId.ShouldBe(originalReceipt.OperationId);
        retry.Receipt.SubjectId.ShouldBe(originalReceipt.SubjectId);
        retry.Receipt.ObjectKey.ShouldBe(originalReceipt.ObjectKey);
        retry.Receipt.Identity.ShouldBe(originalReceipt.Identity);
        retry.Receipt.ExpiresAtUtc.ShouldBe(originalReceipt.ExpiresAtUtc);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task UploadReservationRejectsConflictingRetryWithoutCreatingAnotherHold(bool changeHash)
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync(["documents.manage"]);
        await fixture.SeedQuotaProjectionAsync(user.OrganizationId, limit: 64);
        await using UploadAdmissionEnvironment environment = fixture.CreateUploadAdmissionEnvironment();
        ModuleUploadRequest request = CreateUploadRequest();
        await using MutationScope scope = await ExecutionFixture.BeginUploadMutationScopeAsync(environment.Services, user);
        IModuleUploadAdmission admission = scope.Services.GetRequiredService<IModuleUploadAdmission>();

        ModuleUploadReserveResult first = await admission.ReserveAsync(request);
        first.Disposition.ShouldBe(ModuleUploadReserveDisposition.Reserved);
        ModuleUploadReserveResult conflict = await admission.ReserveAsync(request with
        {
            Sha256 = changeHash ? new string('F', 64) : request.Sha256,
            MetadataFingerprint = changeHash ? request.MetadataFingerprint : new string('E', 64)
        });

        conflict.Disposition.ShouldBe(ModuleUploadReserveDisposition.Conflict);
        conflict.Receipt.ShouldBeNull();
        (await fixture.CountQuotaReservationsForOperationAsync(user.OrganizationId, request.OperationId)).ShouldBe(1);
    }

    [TestMethod]
    public async Task UploadReservationRequiresActiveMembershipAndTheDeclaredPermission()
    {
        await using UploadAdmissionEnvironment environment = fixture.CreateUploadAdmissionEnvironment();

        ProjectsSeededUser missingPermission = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        await fixture.SeedQuotaProjectionAsync(missingPermission.OrganizationId, limit: 64);
        await using (MutationScope scope = await ExecutionFixture.BeginUploadMutationScopeAsync(environment.Services, missingPermission))
        {
            ModuleUploadReserveResult denied = await scope.Services.GetRequiredService<IModuleUploadAdmission>()
                .ReserveAsync(CreateUploadRequest());
            denied.Disposition.ShouldBe(ModuleUploadReserveDisposition.Denied);
            denied.Receipt.ShouldBeNull();
        }
        (await fixture.CountQuotaRowsAsync("module_upload_operations", missingPermission.OrganizationId)).ShouldBe(0);

        ProjectsSeededUser revoked = await fixture.SeedProjectsUserAsync(["documents.manage"]);
        await fixture.SeedQuotaProjectionAsync(revoked.OrganizationId, limit: 64);
        await using (MutationScope staleScope = await ExecutionFixture.BeginUploadMutationScopeAsync(environment.Services, revoked))
        {
            await fixture.SuspendMembershipAsync(revoked.MembershipId);
            ModuleUploadReserveResult denied = await staleScope.Services.GetRequiredService<IModuleUploadAdmission>()
                .ReserveAsync(CreateUploadRequest());
            denied.Disposition.ShouldBe(ModuleUploadReserveDisposition.Denied);
            denied.Receipt.ShouldBeNull();
        }
        (await fixture.CountQuotaRowsAsync("module_upload_operations", revoked.OrganizationId)).ShouldBe(0);
    }

    [TestMethod]
    public async Task DifferentAuthorizedMemberCannotRetryAnotherActorsUploadOperation()
    {
        ProjectsSeededUser owner = await fixture.SeedProjectsUserAsync(["documents.manage"]);
        ProjectsSeededUser otherActor = await fixture.SeedAdditionalProjectsActorAsync(owner.OrganizationId);
        await fixture.GrantMembershipPermissionAsync(otherActor.MembershipId, "documents.manage");
        await fixture.SeedQuotaProjectionAsync(owner.OrganizationId, limit: 64);
        await using UploadAdmissionEnvironment environment = fixture.CreateUploadAdmissionEnvironment();
        ModuleUploadRequest request = CreateUploadRequest();

        await using (MutationScope ownerScope = await ExecutionFixture.BeginUploadMutationScopeAsync(environment.Services, owner))
        {
            ModuleUploadReserveResult reserved = await ownerScope.Services.GetRequiredService<IModuleUploadAdmission>()
                .ReserveAsync(request);
            reserved.Disposition.ShouldBe(ModuleUploadReserveDisposition.Reserved);
        }
        await using MutationScope otherScope = await ExecutionFixture.BeginUploadMutationScopeAsync(environment.Services, otherActor);
        ModuleUploadReserveResult foreignRetry = await otherScope.Services.GetRequiredService<IModuleUploadAdmission>()
            .ReserveAsync(request);

        foreignRetry.Disposition.ShouldBe(ModuleUploadReserveDisposition.Conflict);
        foreignRetry.Receipt.ShouldBeNull();
        (await fixture.CountQuotaReservationsForOperationAsync(owner.OrganizationId, request.OperationId)).ShouldBe(1);
        (await fixture.CountQuotaRowsAsync("module_upload_operations", owner.OrganizationId)).ShouldBe(1);
    }

    [TestMethod]
    public async Task FiniteUploadAdmissionQueuesInventoryWorkButCreatesNoUploadIntentOrHold()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync(["documents.manage"]);
        await fixture.SeedQuotaProjectionAsync(user.OrganizationId, limit: 64, inventoryComplete: false);
        await using UploadAdmissionEnvironment environment = fixture.CreateUploadAdmissionEnvironment();
        ModuleUploadRequest request = CreateUploadRequest();
        await using (MutationScope scope = await ExecutionFixture.BeginUploadMutationScopeAsync(environment.Services, user))
        {
            ModuleUploadReserveResult result = await scope.Services.GetRequiredService<IModuleUploadAdmission>()
                .ReserveAsync(request);
            result.Disposition.ShouldBe(ModuleUploadReserveDisposition.Unavailable);
            result.Receipt.ShouldBeNull();
        }
        await using MutationScope retryScope = await ExecutionFixture.BeginUploadMutationScopeAsync(environment.Services, user);
        ModuleUploadReserveResult retry = await retryScope.Services.GetRequiredService<IModuleUploadAdmission>()
            .ReserveAsync(request with { OperationId = Guid.CreateVersion7() });
        retry.Disposition.ShouldBe(ModuleUploadReserveDisposition.Unavailable);
        retry.Receipt.ShouldBeNull();
        (await fixture.CountQuotaRowsAsync("organization_quota_reservations", user.OrganizationId)).ShouldBe(0);
        (await fixture.CountQuotaRowsAsync("module_upload_operations", user.OrganizationId)).ShouldBe(0);
        (await fixture.CountInventoryWorkAsync(user.OrganizationId)).ShouldBe(1);
    }

    [TestMethod]
    public async Task UploadReservationRejectsWhenParentAlreadyOwnsTheResourceLock()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync(["documents.manage"]);
        await fixture.SeedQuotaProjectionAsync(user.OrganizationId, limit: 64);
        await using UploadAdmissionEnvironment environment = fixture.CreateUploadAdmissionEnvironment();
        await using MutationScope parent = await ExecutionFixture.BeginUploadMutationScopeAsync(environment.Services, user);
        ApplicationDbContext application = parent.Services.GetRequiredService<ApplicationDbContext>();
        (await Trykatch.Infrastructure.Quotas.QuotaResourceLock.TryAcquireAsync(
            application, user.OrganizationId, DocumentsQuotaResource, CancellationToken.None)).ShouldBeTrue();

        Task<ModuleUploadReserveResult> attempt = parent.Services.GetRequiredService<IModuleUploadAdmission>()
            .ReserveAsync(CreateUploadRequest());
        ModuleUploadReserveResult result = await attempt.WaitAsync(TimeSpan.FromSeconds(2));

        result.Disposition.ShouldBe(ModuleUploadReserveDisposition.Unavailable);
        result.Receipt.ShouldBeNull();
        (await fixture.CountQuotaRowsAsync("module_upload_operations", user.OrganizationId)).ShouldBe(0);
    }

    [TestMethod]
    public async Task UploadReservationRejectsAConflictingTransactionOnlyOperation()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync(["documents.manage"]);
        await fixture.SeedQuotaProjectionAsync(user.OrganizationId, limit: 64);
        await using UploadAdmissionEnvironment environment = fixture.CreateUploadAdmissionEnvironment();
        ModuleUploadRequest request = CreateUploadRequest();
        await using (MutationScope ordinary = await ExecutionFixture.BeginUploadMutationScopeAsync(environment.Services, user))
        {
            QuotaReservationResult reservation = await ordinary.Services.GetRequiredService<IOrganizationQuota>()
                .ReserveAsync(DocumentsQuotaResource, request.Length, request.OperationId);
            reservation.Disposition.ShouldBe(QuotaReservationDisposition.Reserved);
            await ordinary.CommitAsync();
        }

        await using MutationScope scope = await ExecutionFixture.BeginUploadMutationScopeAsync(environment.Services, user);
        ModuleUploadReserveResult upload = await scope.Services.GetRequiredService<IModuleUploadAdmission>()
            .ReserveAsync(request);

        upload.Disposition.ShouldBe(ModuleUploadReserveDisposition.Conflict);
        upload.Receipt.ShouldBeNull();
        (await fixture.CountQuotaReservationsForOperationAsync(user.OrganizationId, request.OperationId)).ShouldBe(1);
        (await fixture.CountQuotaRowsAsync("module_upload_operations", user.OrganizationId)).ShouldBe(0);
    }

    [TestMethod]
    public async Task UploadReservationRequiresAnActiveCallerTransactionContext()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync(["documents.manage"]);
        await fixture.SeedQuotaProjectionAsync(user.OrganizationId, limit: 64);
        await using UploadAdmissionEnvironment environment = fixture.CreateUploadAdmissionEnvironment();
        await using MutationScope scope = await ExecutionFixture.BeginUploadMutationScopeAsync(environment.Services, user);
        OrganizationControlPlaneDbContext control = scope.Services.GetRequiredService<OrganizationControlPlaneDbContext>();
        ApplicationDbContext application = scope.Services.GetRequiredService<ApplicationDbContext>();
        IDbContextTransaction ownerTransaction = control.Database.CurrentTransaction!;
        IDbContextTransaction applicationTransaction = application.Database.CurrentTransaction!;
        await ownerTransaction.RollbackAsync(CancellationToken.None);
        await applicationTransaction.DisposeAsync();
        await ownerTransaction.DisposeAsync();

        await Should.ThrowAsync<InvalidOperationException>(() => scope.Services.GetRequiredService<IModuleUploadAdmission>()
            .ReserveAsync(CreateUploadRequest()));
        (await fixture.CountQuotaRowsAsync("module_upload_operations", user.OrganizationId)).ShouldBe(0);
    }

    [TestMethod]
    public async Task UploadAdmissionRejectsAfterParentAlreadyAcquiredQuotaResourceLock()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync(["documents.manage"]);
        await fixture.SeedQuotaProjectionAsync(user.OrganizationId, limit: 64);
        await using UploadAdmissionEnvironment environment = fixture.CreateUploadAdmissionEnvironment();
        await using MutationScope parent = await ExecutionFixture.BeginUploadMutationScopeAsync(environment.Services, user);

        _ = await parent.Services.GetRequiredService<IOrganizationEntitlementReader>().GetAsync(DocumentsQuotaResource);
        await Should.ThrowAsync<InvalidOperationException>(() => parent.Services.GetRequiredService<IModuleUploadAdmission>()
            .ReserveAsync(CreateUploadRequest()));
        (await fixture.CountQuotaRowsAsync("module_upload_operations", user.OrganizationId)).ShouldBe(0);
    }

    private static ModuleUploadRequest CreateUploadRequest()
    {
        byte[] bytes = "admitted module upload"u8.ToArray();
        return new(TestUploadPolicy, Guid.CreateVersion7(), bytes.LongLength,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)), new string('A', 64));
    }

    private sealed class UploadAdmissionEnvironment(ServiceProvider services, string storageRoot) : IAsyncDisposable
    {
        public ServiceProvider Services { get; } = services;

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            if (Directory.Exists(storageRoot))
                Directory.Delete(storageRoot, recursive: true);
        }
    }

    private sealed class UploadPolicyDocumentsModule : IModule
    {
        private readonly DocumentsModule inner = new();

        public ModuleDescriptor Descriptor { get; }

        public UploadPolicyDocumentsModule()
        {
            Descriptor = inner.Descriptor with
            {
                QuotaResources = inner.Descriptor.QuotaResources.Select(resource => resource with
                {
                    UploadPolicy = resource.Key == "documents.storage-bytes"
                        ? new ModuleUploadPolicyDescriptor("documents", "documents.upload", 1, "documents.manage")
                        : resource.UploadPolicy
                }).ToArray()
            };
        }

        public void Register(IServiceCollection services, IConfiguration configuration) => inner.Register(services, configuration);
    }

    private sealed partial class ExecutionFixture
    {
        public UploadAdmissionEnvironment CreateUploadAdmissionEnvironment(Action<IServiceCollection>? configure = null)
        {
            IConfiguration configuration = new ConfigurationBuilder()
                .AddConfiguration(services.GetRequiredService<IConfiguration>())
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Storage:LocalPath"] = Path.Combine(Path.GetTempPath(), $"trykatch-upload-tests-{Guid.NewGuid():N}")
                })
                .Build();
            string storageRoot = configuration["Storage:LocalPath"]!;
            ServiceCollection registrations = new();
            registrations.AddSingleton(configuration);
            registrations.AddLogging();
            registrations.AddApplication();
            registrations.AddModules(configuration, [new ProjectsModule(), new UploadPolicyDocumentsModule()]);
            registrations.AddInfrastructure(configuration);
            configure?.Invoke(registrations);
            ServiceProvider provider = registrations.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
            return new(provider, storageRoot);
        }

        public Task<long> CountUploadRecoveryWorkAsync(Guid organizationId, Guid operationId) => CountOwnerAsync("""
            SELECT count(*) FROM infrastructure.quota_maintenance_work
            WHERE "OrganizationId" = @organization AND "UploadOperationId" = @operation AND "Kind" = 2
            """, ("organization", organizationId), ("operation", operationId));

        public Task<long> CountInventoryWorkAsync(Guid organizationId) => CountOwnerAsync("""
            SELECT count(*) FROM infrastructure.quota_maintenance_work
            WHERE "OrganizationId" = @organization AND "Kind" = 3 AND "UploadOperationId" IS NULL
            """, ("organization", organizationId));

        public Task GrantMembershipPermissionAsync(Guid membershipId, string permission) => ExecuteOwnerAsync("""
            INSERT INTO platform.role_permissions ("RoleId", "Permission")
            SELECT "RoleId", @permission FROM platform.membership_roles WHERE "MembershipId" = @membership
            ON CONFLICT DO NOTHING
            """, ("membership", membershipId), ("permission", permission));

        public static async Task<MutationScope> BeginUploadMutationScopeAsync(
            IServiceProvider provider,
            ProjectsSeededUser user)
        {
            AsyncServiceScope scope = provider.CreateAsyncScope();
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
                scope.ServiceProvider.GetRequiredService<IOrganizationContextInitializer>().Initialize(access);
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
    }
}

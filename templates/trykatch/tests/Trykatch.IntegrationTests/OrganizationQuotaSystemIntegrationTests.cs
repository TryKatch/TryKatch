using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Trykatch.Application.Execution;
using Trykatch.Application.Organizations;
using Trykatch.Domain.Organizations;
using Trykatch.Infrastructure.Execution;
using Trykatch.Infrastructure.Organizations;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Infrastructure.Quotas;
using Trykatch.Modules;

namespace Trykatch.IntegrationTests;

public sealed partial class ModuleWorkExecutionIntegrationTests
{
    private const string ProbeQuotaResource = "probe.units";

    [TestMethod]
    public async Task QuotaSystemScheduledGrantCanReserveAndCommitWithoutMembershipAuthority()
    {
        Guid organizationId = await fixture.CreateOrganizationAsync();
        await fixture.SeedProbeQuotaProjectionAsync(organizationId);
        await using MutationScope scope = await fixture.BeginSystemQuotaScopeAsync(organizationId);
        Should.Throw<OrganizationMembershipRequiredException>(() => _ = scope.OrganizationContext.MembershipId);

        OrganizationQuotaService quota = CreateQuota(scope);
        Guid operationId = Guid.NewGuid();
        QuotaReservationResult reservation = await quota.ReserveAsync(ProbeQuotaResource, 2, operationId);
        reservation.Disposition.ShouldBe(QuotaReservationDisposition.Reserved);
        reservation.Reservation.ShouldNotBeNull();
        await quota.CommitAsync(reservation.Reservation!.Id);
        await scope.CommitAsync();

        (await fixture.CountProbeQuotaRowsAsync("organization_quota_reservations", organizationId)).ShouldBe(1);
        (await fixture.CountProbeQuotaRowsAsync("organization_usage", organizationId)).ShouldBe(1);
        (await fixture.ReadProbeQuotaReservationActorAsync(organizationId)).ShouldBe(ProbeModule.AuditActorId);
    }

    [TestMethod]
    public async Task QuotaSystemScheduledGrantIsDeniedAfterOrganizationDeactivation()
    {
        Guid organizationId = await fixture.CreateOrganizationAsync();
        await fixture.SeedProbeQuotaProjectionAsync(organizationId);
        await using MutationScope scope = await fixture.BeginSystemQuotaScopeAsync(organizationId);
        await fixture.SetOrganizationActiveAsync(organizationId, false);

        QuotaReservationResult result = await CreateQuota(scope).ReserveAsync(ProbeQuotaResource, 1, Guid.NewGuid());
        result.Disposition.ShouldBe(QuotaReservationDisposition.Denied);
        result.Reservation.ShouldBeNull();
        await scope.CommitAsync();

        (await fixture.CountProbeQuotaRowsAsync("organization_quota_reservations", organizationId)).ShouldBe(0);
    }

    [TestMethod]
    public async Task QuotaSystemScheduledGrantIsDeniedAfterPlacementChangesToDedicated()
    {
        Guid organizationId = await fixture.CreateOrganizationAsync();
        await fixture.SeedProbeQuotaProjectionAsync(organizationId);
        await using MutationScope scope = await fixture.BeginSystemQuotaScopeAsync(organizationId);
        await fixture.SetOrganizationPlacementAsync(organizationId, OrganizationDataPlacementKind.Dedicated);

        QuotaReservationResult result = await CreateQuota(scope).ReserveAsync(ProbeQuotaResource, 1, Guid.NewGuid());
        result.Disposition.ShouldBe(QuotaReservationDisposition.Denied);
        result.Reservation.ShouldBeNull();
        await scope.CommitAsync();

        (await fixture.CountProbeQuotaRowsAsync("organization_quota_reservations", organizationId)).ShouldBe(0);
    }

    [TestMethod]
    public async Task QuotaSystemContextWithoutWorkProvenanceIsDenied()
    {
        Guid organizationId = await fixture.CreateOrganizationAsync();
        await fixture.SeedProbeQuotaProjectionAsync(organizationId);
        await using MutationScope scope = await fixture.BeginSystemQuotaScopeAsync(organizationId, initializeWork: false);

        QuotaReservationResult result = await CreateQuota(scope).ReserveAsync(ProbeQuotaResource, 1, Guid.NewGuid());
        result.Disposition.ShouldBe(QuotaReservationDisposition.Denied);
        result.Reservation.ShouldBeNull();
        await scope.CommitAsync();

        (await fixture.CountProbeQuotaRowsAsync("organization_quota_reservations", organizationId)).ShouldBe(0);
    }

    private sealed partial class ExecutionFixture
    {
        public async Task<MutationScope> BeginSystemQuotaScopeAsync(Guid organizationId, bool initializeWork = true)
        {
            AsyncServiceScope scope = services.CreateAsyncScope();
            IOrganizationDataScope? transaction = null;
            IDbContextTransaction? enlistment = null;
            try
            {
                Guid actorId = ProbeModule.AuditActorId;
                transaction = await scope.ServiceProvider.GetRequiredService<IOrganizationDataScopeFactory>()
                    .BeginAsync(organizationId, actorId, CancellationToken.None);
                OrganizationControlPlaneDbContext control = scope.ServiceProvider.GetRequiredService<OrganizationControlPlaneDbContext>();
                ApplicationDbContext application = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                enlistment = await OrganizationTransactionEnlistment.EnlistAsync(
                    control, application, organizationId, actorId, CancellationToken.None);

                ModuleOperationRegistry operations = scope.ServiceProvider.GetRequiredService<ModuleOperationRegistry>();
                operations.TryGet("probe", "probe.scheduled", 1, out ModuleOperationRegistration? registration).ShouldBeTrue();
                registration.ShouldNotBeNull();
                ModuleSystemGrantDescriptor grant = registration!.SystemGrant!;
                Guid originActorId = await control.Memberships.AsNoTracking()
                    .Where(row => row.OrganizationId == organizationId)
                    .Select(row => row.UserId).SingleAsync();
                ModuleExecutionIdentity identity = new(
                    organizationId,
                    ProbeModule.AuditActorId,
                    originActorId,
                    null,
                    ModuleExecutionKind.ScheduledSystem,
                    grant.GrantKey,
                    grant.GrantVersion,
                    null,
                    ModuleOperationRegistry.CatalogVersion,
                    null);
                SystemWorkGrantValidator.TryValidate(registration, identity).ShouldBeTrue();

                string organizationSlug = await control.Organizations.AsNoTracking()
                    .Where(row => row.Id == organizationId)
                    .Select(row => row.Slug)
                    .SingleAsync();
                OrganizationContext organization = scope.ServiceProvider.GetRequiredService<OrganizationContext>();
                organization.InitializeSystem(new OrganizationSystemAccess(
                    organizationId, organizationSlug, ProbeModule.AuditActorId, new HashSet<string>(StringComparer.Ordinal)));
                if (initializeWork)
                    scope.ServiceProvider.GetRequiredService<ModuleWorkExecutionContext>().Initialize(identity, registration.Descriptor);

                return new(scope, transaction, enlistment,
                    scope.ServiceProvider.GetRequiredService<IModuleMutationIdempotencyStore>(), organization);
            }
            catch
            {
                if (enlistment is not null) await enlistment.DisposeAsync();
                if (transaction is not null) await transaction.DisposeAsync();
                await scope.DisposeAsync();
                throw;
            }
        }

        public async Task SeedProbeQuotaProjectionAsync(Guid organizationId)
        {
            await ExecuteOwnerAsync("""
            INSERT INTO app.organization_entitlements
                ("OrganizationId", "ResourceKey", "OwnerModuleId", "Unit", "Mode", "Limit", "Version",
                 "Source", "SourceRevision", "SourceHash", "EffectiveAtUtc")
            VALUES (@organization, 'probe.units', 'probe', 2, 1, 10, @version,
                    'host-configuration', 1, @hash, CURRENT_TIMESTAMP);
            INSERT INTO platform.entitlement_publication_history
                ("OrganizationId", "SourceRevision", "SourceHash", "Source", "SourceVersion", "ActorId", "InitialResourceCount", "PublishedAtUtc")
            VALUES (@organization, 1, @hash, 'host-configuration', 1, @publisher, 1, CURRENT_TIMESTAMP)
            """, ("organization", organizationId), ("version", Guid.NewGuid()), ("hash", new string('c', 64)),
                ("publisher", HostEntitlementPublisher.AuditActorId));
            await ExecuteOwnerAsync("""
                INSERT INTO app.organization_quota_inventory
                    ("OrganizationId", "ResourceKey", "SourceKey", "SourceVersion", "Cursor", "State", "UpdatedAtUtc")
                VALUES (@organization, 'probe.units', 'probe.inventory', 1, NULL, 1, CURRENT_TIMESTAMP)
                """, ("organization", organizationId));
        }

        public Task SetOrganizationActiveAsync(Guid organizationId, bool active) => ExecuteOwnerAsync(
            "UPDATE platform.organizations SET \"IsActive\"=@active WHERE \"Id\"=@organization",
            ("active", active), ("organization", organizationId));

        public Task SetOrganizationPlacementAsync(Guid organizationId, OrganizationDataPlacementKind placement) => ExecuteOwnerAsync(
            "UPDATE platform.organization_data_placements SET \"Placement\"=@placement WHERE \"OrganizationId\"=@organization",
            ("placement", (int)placement), ("organization", organizationId));

        public Task<long> CountProbeQuotaRowsAsync(string table, Guid organizationId) => CountOwnerAsync(
            $"SELECT count(*) FROM app.{table} WHERE \"OrganizationId\"=@organization",
            ("organization", organizationId));

        public async Task<Guid> ReadProbeQuotaReservationActorAsync(Guid organizationId)
        {
            await using NpgsqlConnection connection = new(owner);
            await connection.OpenAsync();
            await using NpgsqlCommand command = new("""
                SELECT "ActorId" FROM app.organization_quota_reservations
                WHERE "OrganizationId"=@organization AND "ResourceKey"='probe.units'
                """, connection);
            command.Parameters.AddWithValue("organization", organizationId);
            return (Guid)(await command.ExecuteScalarAsync())!;
        }
    }
}

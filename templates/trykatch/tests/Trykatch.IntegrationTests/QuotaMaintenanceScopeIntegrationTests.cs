using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Trykatch.Application.Authorization;
using Trykatch.Application.Organizations;
using Trykatch.Infrastructure.Organizations;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Infrastructure.Quotas;
using Trykatch.Modules;
using Trykatch.Modules.AspNetCore;
using Trykatch.Modules.Documents.Application;

namespace Trykatch.IntegrationTests;

public sealed partial class ModuleWorkExecutionIntegrationTests
{
    [TestMethod]
    public async Task QuotaMaintenanceScopeHasFixedEmptyPermissionPrincipalAndCannotManageDocuments()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        await using QuotaMaintenanceScope? scope = await fixture.OpenQuotaMaintenanceScopeAsync(user.OrganizationId);
        scope.ShouldNotBeNull();
        IOrganizationContext organization = scope.Services.GetRequiredService<IOrganizationContext>();
        organization.OrganizationId.ShouldBe(user.OrganizationId);
        organization.ActorId.ShouldBe(QuotaMaintenanceAuthority.ActorId);
        organization.ActorId.ShouldNotBe(user.ActorId);
        organization.Permissions.ShouldBeEmpty();
        Should.Throw<OrganizationMembershipRequiredException>(() => organization.MembershipId);
        ApplicationDbContext application = scope.Services.GetRequiredService<ApplicationDbContext>();
        OrganizationControlPlaneDbContext control = scope.Services.GetRequiredService<OrganizationControlPlaneDbContext>();
        application.Database.CurrentTransaction.ShouldNotBeNull();
        control.Database.CurrentTransaction.ShouldNotBeNull();
        await scope.ValidateAsync(CancellationToken.None);
        await Should.ThrowAsync<OrganizationQuotaUnavailableException>(() =>
            scope.Services.GetRequiredService<IOrganizationEntitlementReader>().GetAsync("documents.storage-bytes"));
        using MemoryStream content = new([1]);
        DocumentsUseCases documents = ActivatorUtilities.CreateInstance<DocumentsUseCases>(
            scope.Services, new ModuleTransactionCompensation());
        DocumentOperationResult<DocumentDto> denied = await documents
            .UploadAsync(new("Document", null, "file.txt", "text/plain", 1, new string('a', 64), content), CancellationToken.None);
        denied.IsSuccess.ShouldBeFalse();
        denied.Code.ShouldBe("forbidden");
    }

    [TestMethod]
    public async Task QuotaMaintenanceScopePausesUnsupportedPlacementAndHonorsCancellation()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        await fixture.SetQuotaMaintenanceProviderAsync(user.OrganizationId, "unsupported");
        (await fixture.OpenQuotaMaintenanceScopeAsync(user.OrganizationId)).ShouldBeNull();
        await fixture.SetQuotaMaintenanceProviderAsync(user.OrganizationId, "postgres");
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => fixture.OpenQuotaMaintenanceScopeAsync(user.OrganizationId, cancelled.Token));
        await using QuotaMaintenanceScope? resumed = await fixture.OpenQuotaMaintenanceScopeAsync(user.OrganizationId);
        resumed.ShouldNotBeNull();
    }

    [TestMethod]
    public async Task QuotaMaintenanceAuthorityRejectsOrdinaryUserScope()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        await using MutationScope scope = await fixture.BeginMutationScopeAsync(user);
        await Should.ThrowAsync<InvalidOperationException>(() => scope.Services.GetRequiredService<QuotaMaintenanceAuthority>()
            .ValidateAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task QuotaMaintenanceAuthorityIsBoundToActualTransactionAndActorFrame()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync([Permissions.ProjectsManage]);
        await using (QuotaMaintenanceScope? scope = await fixture.OpenQuotaMaintenanceScopeAsync(user.OrganizationId))
        {
            scope.ShouldNotBeNull();
            await scope.Services.GetRequiredService<ApplicationDbContext>().Database.ExecuteSqlInterpolatedAsync(
                $"SELECT set_config('app.actor_id', {user.ActorId.ToString()}, true)");
            await Should.ThrowAsync<InvalidOperationException>(() => scope.ValidateAsync(CancellationToken.None));
        }
        await using (QuotaMaintenanceScope? scope = await fixture.OpenQuotaMaintenanceScopeAsync(user.OrganizationId))
        {
            scope.ShouldNotBeNull();
            await scope.CommitAsync(CancellationToken.None);
            await Should.ThrowAsync<InvalidOperationException>(() => scope.ValidateAsync(CancellationToken.None));
        }
    }

    private sealed partial class ExecutionFixture
    {
        public Task SetQuotaMaintenanceProviderAsync(Guid organizationId, string provider) => ExecuteOwnerAsync("""
            UPDATE platform.organization_data_placements SET "Provider" = @provider WHERE "OrganizationId" = @organization
            """, ("provider", provider), ("organization", organizationId));

        public async Task<QuotaMaintenanceScope?> OpenQuotaMaintenanceScopeAsync(Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            await using AsyncServiceScope scope = services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<QuotaMaintenanceScopeFactory>()
                .TryCreateAsync(organizationId, cancellationToken);
        }
    }
}

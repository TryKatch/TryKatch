using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Trykatch.Application.Organizations;
using Trykatch.Domain.Organizations;
using Trykatch.Infrastructure.Organizations;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Modules;

namespace Trykatch.Infrastructure.Quotas;

/// <summary>Host-only technical scopes for closed quota maintenance, with no module permissions or membership.</summary>
internal sealed class QuotaMaintenanceScopeFactory(IServiceScopeFactory scopes)
{
    internal async Task<QuotaMaintenanceScope?> TryCreateAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (organizationId == Guid.Empty) return null;
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        AsyncServiceScope scope = scopes.CreateAsyncScope();
        IOrganizationDataScope? transaction = null;
        bool handedOff = false;
        try
        {
            IServiceProvider services = scope.ServiceProvider;
            transaction = await services.GetRequiredService<IOrganizationDataScopeFactory>()
                .BeginAsync(organizationId, QuotaMaintenanceAuthority.ActorId, timeout.Token);
            ApplicationDbContext application = services.GetRequiredService<ApplicationDbContext>();
            OrganizationControlPlaneDbContext control = services.GetRequiredService<OrganizationControlPlaneDbContext>();
            _ = await OrganizationTransactionEnlistment.EnlistAsync(control, application, organizationId,
                QuotaMaintenanceAuthority.ActorId, timeout.Token);
            await application.Database.ExecuteSqlRawAsync(
                "SET LOCAL statement_timeout = '5s'; SET LOCAL lock_timeout = '1s'", timeout.Token);
            Organization? target = await control.Organizations.AsNoTracking()
                .SingleOrDefaultAsync(row => row.Id == organizationId && row.IsActive, timeout.Token);
            if (target is null) return null;
            services.GetRequiredService<OrganizationContext>().InitializeSystem(new(
                organizationId, target.Slug, QuotaMaintenanceAuthority.ActorId, new HashSet<string>(StringComparer.Ordinal)));
            QuotaMaintenanceAuthority authority = services.GetRequiredService<QuotaMaintenanceAuthority>();
            authority.Issue();
            if (!await authority.TargetIsEligibleAsync(timeout.Token)) return null;
            await authority.ValidateAsync(timeout.Token);
            QuotaMaintenanceScope result = new(scope, transaction, authority);
            handedOff = true;
            return result;
        }
        catch (OperationCanceledException failure) when (!cancellationToken.IsCancellationRequested)
        {
            throw new OrganizationQuotaUnavailableException(failure);
        }
        finally
        {
            if (!handedOff)
            {
                try { if (transaction is not null) await transaction.DisposeAsync(); }
                finally { await scope.DisposeAsync(); }
            }
        }
    }
}

internal sealed class QuotaMaintenanceAuthority(ApplicationDbContext application,
    OrganizationControlPlaneDbContext control, IOrganizationContext organization, IOrganizationDataPlacement placement)
{
    internal static readonly Guid ActorId = Guid.Parse("14d732d4-995d-4fbb-bc78-13156f940d0e");
    private DbTransaction? issuedTransaction;

    internal void Issue()
    {
        if (issuedTransaction is not null)
            throw new InvalidOperationException("Quota maintenance authority can only be issued once per scope.");
        if (!organization.IsResolved || organization.OrganizationId == Guid.Empty
            || organization.ActorId != ActorId || organization.Permissions.Count != 0)
            throw new InvalidOperationException("Quota maintenance requires the fixed empty-permission host principal.");
        try
        {
            _ = organization.MembershipId;
            throw new InvalidOperationException("Quota maintenance cannot use an organization membership.");
        }
        catch (OrganizationMembershipRequiredException) { }
        issuedTransaction = RequireTransaction();
    }

    internal async Task ValidateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (issuedTransaction is null || !ReferenceEquals(issuedTransaction, RequireTransaction())
            || organization.ActorId != ActorId || organization.Permissions.Count != 0)
            throw new InvalidOperationException("Quota maintenance requires its original compiled transaction authority.");
        bool matches = await application.Database.SqlQuery<bool>($"""
            SELECT (NULLIF(current_setting('app.organization_id', true), '')::uuid = {organization.OrganizationId}
                AND NULLIF(current_setting('app.actor_id', true), '')::uuid = {ActorId}) IS TRUE AS "Value"
            """).SingleAsync(cancellationToken);
        if (!matches)
            throw new InvalidOperationException("Quota maintenance must match the current organization and actor frame.");
        if (!await TargetIsEligibleAsync(cancellationToken))
            throw new OrganizationQuotaUnavailableException();
    }

    internal async Task<bool> TargetIsEligibleAsync(CancellationToken cancellationToken)
    {
        if (!await control.Organizations.AsNoTracking().AnyAsync(
                row => row.Id == organization.OrganizationId && row.IsActive, cancellationToken)) return false;
        OrganizationDataRoute route;
        try { route = await placement.ResolveAsync(organization.OrganizationId, cancellationToken); }
        catch (Exception failure) when (failure is KeyNotFoundException or InvalidOperationException { InnerException: null })
        {
            return false;
        }
        return route.OrganizationId == organization.OrganizationId && route.Placement == OrganizationDataPlacementKind.Shared
            && route.Provider == "postgres" && route.DatabaseIdentifier == application.Database.GetDbConnection().Database;
    }

    private DbTransaction RequireTransaction()
    {
        DbTransaction? transaction = application.Database.CurrentTransaction?.GetDbTransaction();
        if (transaction is null || transaction.Connection is null || transaction.IsolationLevel != IsolationLevel.ReadCommitted
            || !ReferenceEquals(transaction, control.Database.CurrentTransaction?.GetDbTransaction()))
            throw new InvalidOperationException("Quota maintenance requires the same active ReadCommitted organization transaction.");
        return transaction;
    }
}

internal sealed class QuotaMaintenanceScope(AsyncServiceScope scope, IOrganizationDataScope transaction,
    QuotaMaintenanceAuthority authority) : IAsyncDisposable
{
    internal IServiceProvider Services => scope.ServiceProvider;
    internal Task ValidateAsync(CancellationToken cancellationToken) => authority.ValidateAsync(cancellationToken);
    internal async Task CommitAsync(CancellationToken cancellationToken)
    {
        await authority.ValidateAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
    public async ValueTask DisposeAsync()
    {
        try { await transaction.DisposeAsync(); }
        finally { await scope.DisposeAsync(); }
    }
}

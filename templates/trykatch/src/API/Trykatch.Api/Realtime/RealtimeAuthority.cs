using Trykatch.Application.Identity;
using Trykatch.Application.Organizations;
using Trykatch.Infrastructure.Organizations;
using Trykatch.Domain.Organizations;

namespace Trykatch.Api.Realtime;

/// <summary>One fresh DI scope and read-only database transaction for one recipient attempt.</summary>
internal sealed class RealtimeAuthority(
    IAccountSessions sessions,
    IOrganizationDataScopeFactory dataScopes,
    IOrganizationAccessResolver access,
    IOrganizationDataPlacement placement,
    IOrganizationContextInitializer initializer) : IAsyncDisposable
{
    private IOrganizationDataScope? dataScope;
    private bool attempted;

    internal async Task<bool> OpenAsync(RealtimeConnectionBinding binding, CancellationToken cancellationToken)
    {
        if (attempted) throw new InvalidOperationException("Realtime authority requires a fresh scope for each attempt.");
        attempted = true;
        if (!await IsSessionCurrentAsync(binding, cancellationToken)) return false;
        dataScope = await dataScopes.BeginReadOnlyAsync(binding.OrganizationId, binding.Proof.ActorId, cancellationToken);
        OrganizationAccess? current = await access.ResolveAsync(binding.Proof.ActorId, binding.OrganizationId, cancellationToken);
        if (current is null)
        {
            await DisposeAsync();
            return false;
        }
        OrganizationDataRoute route;
        try { route = await placement.ResolveAsync(binding.OrganizationId, cancellationToken); }
        catch (KeyNotFoundException)
        {
            await DisposeAsync();
            return false;
        }
        if (route.Placement != OrganizationDataPlacementKind.Shared)
        {
            await DisposeAsync();
            return false;
        }
        initializer.Initialize(current);
        return true;
    }

    internal async Task<bool> IsSessionCurrentAsync(RealtimeConnectionBinding binding, CancellationToken cancellationToken)
    {
        if (binding.OrganizationId == Guid.Empty || binding.Generation == Guid.Empty) return false;
        AccountSessionState? state = await sessions.ValidateAsync(binding.Proof, cancellationToken);
        return state is not null && state.OrganizationId == binding.OrganizationId && state.WorkspaceGeneration == binding.Generation;
    }

    public async ValueTask DisposeAsync()
    {
        if (dataScope is null) return;
        await dataScope.DisposeAsync();
        dataScope = null;
    }
}

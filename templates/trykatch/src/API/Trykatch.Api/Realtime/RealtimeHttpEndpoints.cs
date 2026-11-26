using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Trykatch.Api.Security;
using Trykatch.Application.Identity;
using Trykatch.Application.Organizations;
using Trykatch.Application.Realtime;
using Trykatch.Identity;
using Trykatch.Modules;
using Trykatch.Modules.AspNetCore;

namespace Trykatch.Api.Realtime;

public sealed record RealtimeStateResponse(bool Enabled, Guid Generation, string Cursor);
public sealed record RealtimeHistoryResponse(
    IReadOnlyList<ModuleRealtimeNotification> Items, Guid Generation, string Cursor, bool HasMore);

internal static class RealtimeHttpEndpoints
{
    internal static void MapRealtimeHistory(this WebApplication app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/v1/realtime")
            .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = AuthenticationSchemes.ApplicationCookie })
            .WithMetadata(new OrganizationScopedAttribute(), new ModuleReadOnlyTransactionMetadata())
            .WithTags("Realtime");
        group.MapGet("/state", (HttpContext context, RealtimeHttpReader reader) => reader.ReadAsync(context, false, null))
            .WithName("Realtime_GetState").Produces<RealtimeStateResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized).ProducesProblem(StatusCodes.Status409Conflict);
        group.MapGet("/history", (HttpContext context, RealtimeHttpReader reader, string? cursor = null) => reader.ReadAsync(context, true, cursor))
            .WithName("Realtime_GetHistory").Produces<RealtimeHistoryResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized).ProducesProblem(StatusCodes.Status409Conflict);
    }
}

internal sealed class RealtimeHttpReader(
    ICurrentAccountSession currentSession,
    IAccountSessions sessions,
    IOrganizationContext organization,
    IRealtimeHistoryReader history,
    IRealtimeCursorProtector cursors,
    IOptions<RealtimeOptions> options)
{
    internal async Task<IResult> ReadAsync(HttpContext context, bool readHistory, string? cursor)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        deadline.CancelAfter(options.Value.AuthorityTimeout);
        CancellationToken cancellationToken = deadline.Token;
        AccountSessionProof? proof = currentSession.Proof;
        if (proof is null || !organization.IsResolved || proof.ActorId != organization.ActorId)
            return Results.Unauthorized();
        AccountSessionState? session = await sessions.ValidateAsync(proof, cancellationToken);
        if (session is null) return Results.Unauthorized();
        if (session.OrganizationId != organization.OrganizationId) return Reset();
        RealtimeHistoryScope scope = new(organization.OrganizationId, proof.ActorId, proof.SessionHash, session.WorkspaceGeneration);
        long after = 0;
        if (readHistory && cursor is not null && !cursors.TryRead(cursor, scope, out after)) return Reset();
        long head = await history.ReadHeadAsync(cancellationToken);
        if (readHistory && after > head) return Reset();
        RealtimeHistoryBatch? batch = readHistory ? await history.ReadPageAsync(after, cancellationToken) : null;
        // A workspace change during policy work invalidates this response as well as its continuation.
        AccountSessionState? finalSession = await sessions.ValidateAsync(proof, cancellationToken);
        if (finalSession is null) return Results.Unauthorized();
        if (finalSession.OrganizationId != scope.OrganizationId || finalSession.WorkspaceGeneration != scope.WorkspaceGeneration)
            return Reset();
        if (batch is null)
            return Results.Ok(new RealtimeStateResponse(options.Value.Mode != RealtimeMode.Disabled,
                scope.WorkspaceGeneration, cursors.Protect(scope, head)));
        return Results.Ok(new RealtimeHistoryResponse(batch.Items.Select(item => item.Notification).ToArray(),
            scope.WorkspaceGeneration, cursors.Protect(scope, batch.Position), batch.HasMore));
    }

    private static IResult Reset() => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "realtime_reset_required");
}

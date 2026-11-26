namespace Trykatch.Application.Realtime;

/// <summary>Host-injected binding metadata; it never supplies permission or session authority.</summary>
public sealed record RealtimeHistoryScope(Guid OrganizationId, Guid ActorId, string SessionHash, Guid WorkspaceGeneration)
{
    public override string ToString() => "Realtime history scope";
}

public interface IRealtimeCursorProtector
{
    string Protect(RealtimeHistoryScope scope, long position);
    bool TryRead(string? cursor, RealtimeHistoryScope scope, out long position);
}

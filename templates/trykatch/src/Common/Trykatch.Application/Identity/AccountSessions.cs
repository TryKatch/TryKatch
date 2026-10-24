namespace Trykatch.Application.Identity;

/// <summary>Captured from a protected application cookie, never from a client DTO.</summary>
public sealed record AccountSessionProof(Guid ActorId, string SessionHash, string SecurityStamp, DateTimeOffset TicketExpiresAt)
{
    public override string ToString() => nameof(AccountSessionProof);
}

/// <summary>Current durable binding; membership and permissions must still be resolved separately.</summary>
public sealed record AccountSessionState(Guid? OrganizationId, Guid WorkspaceGeneration, DateTimeOffset ExpiresAt)
{
    public override string ToString() => nameof(AccountSessionState);
}

public interface IAccountSessions
{
    Task<AccountSessionState?> ValidateAsync(AccountSessionProof proof, CancellationToken cancellationToken = default);
    Task<AccountSessionState?> BindWorkspaceAsync(AccountSessionProof proof, Guid? organizationId, CancellationToken cancellationToken = default);
}

public interface ICurrentAccountSession
{
    AccountSessionProof? Proof { get; }
}

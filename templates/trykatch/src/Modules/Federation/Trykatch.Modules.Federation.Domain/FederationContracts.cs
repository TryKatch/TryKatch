namespace Trykatch.Modules.Federation.Domain;

public sealed record FederationConnection(
    Guid Id,
    string Name,
    string Issuer,
    string ClientId,
    bool SecretConfigured,
    bool Enabled,
    DateTimeOffset? LastTestedAt,
    string? LastTestResult,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Deep protocol seam owned by the security kernel. Provider adapters return
/// normalized identities; they never issue Trykatch cookies or choose roles.
/// </summary>
public interface IExternalIdentityBroker
{
    Task<ExternalChallenge> BeginAsync(Guid connectionId, string returnPath, CancellationToken cancellationToken);
    Task<ExternalIdentityResult> CompleteAsync(string callbackPath, CancellationToken cancellationToken);
}

public sealed record ExternalChallenge(Uri AuthorizationUri, string TransactionId);
public sealed record ExternalIdentityResult(string Issuer, string Subject, string? VerifiedEmail, IReadOnlyDictionary<string, string> Claims);

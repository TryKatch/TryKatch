using System.Text.Json;

namespace Trykatch.Application.Auditing;

/// <summary>
/// An append-only request to project an organization audit event from a transaction
/// that cannot share the audit-store runtime role.
/// </summary>
public sealed class AuditIntent
{
    private static readonly HashSet<string> ApprovedDetailKeys = new(StringComparer.Ordinal)
    {
        "expiresAt",
        "permissionCount",
        "reasonProvided",
        "roleCount",
        "status"
    };

    private AuditIntent() { }

    public Guid Id { get; private init; }
    public Guid OrganizationId { get; private init; }
    public Guid ActorId { get; private init; }
    public string Operation { get; private init; } = string.Empty;
    public string SubjectType { get; private init; } = string.Empty;
    public string SubjectId { get; private init; } = string.Empty;
    public string SubjectDisplayName { get; private init; } = string.Empty;
    public string Details { get; private init; } = "{}";
    public DateTimeOffset OccurredAt { get; private init; }

    public static AuditIntent Create(
        Guid eventId,
        Guid organizationId,
        Guid actorId,
        string operation,
        AuditTarget target,
        IReadOnlyDictionary<string, string?>? details,
        DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (eventId == Guid.Empty || organizationId == Guid.Empty || actorId == Guid.Empty)
            throw new ArgumentException("Audit event, organization, and actor identifiers are required.");
        string normalizedOperation = Required(operation, 120, nameof(operation));
        string subjectType = Required(target.Type, 120, nameof(target));
        string subjectId = Required(target.Id, 160, nameof(target));
        string displayName = Required(target.DisplayName, 240, nameof(target));
        Dictionary<string, string?> approved = new(StringComparer.Ordinal);
        foreach ((string key, string? value) in details ?? new Dictionary<string, string?>())
        {
            if (!ApprovedDetailKeys.Contains(key))
                throw new ArgumentException($"Audit detail '{key}' is not approved.", nameof(details));
            if (value?.Length > 500)
                throw new ArgumentException($"Audit detail '{key}' exceeds 500 characters.", nameof(details));
            approved.Add(key, value);
        }
        string serialized = JsonSerializer.Serialize(approved);
        if (serialized.Length > 2048)
            throw new ArgumentException("Audit details exceed 2048 characters.", nameof(details));

        return new AuditIntent
        {
            Id = eventId,
            OrganizationId = organizationId,
            ActorId = actorId,
            Operation = normalizedOperation,
            SubjectType = subjectType,
            SubjectId = subjectId,
            SubjectDisplayName = displayName,
            Details = serialized,
            OccurredAt = occurredAt
        };
    }

    private static string Required(string value, int maximumLength, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength)
            throw new ArgumentException($"A value containing 1-{maximumLength} characters is required.", parameterName);
        return value;
    }
}

public static class AuditDetails
{
    public static IReadOnlyDictionary<string, string?> ReasonProvided(string? reason) =>
        new Dictionary<string, string?> { ["reasonProvided"] = (!string.IsNullOrWhiteSpace(reason)).ToString() };
}

public interface IAuditIntentWriter
{
    Guid Record(string operation, AuditTarget target, IReadOnlyDictionary<string, string?>? details = null);
}

public interface IAuditIntentProjectionStore
{
    Task<AuditProjectionBacklog> ReadBacklogAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<AuditIntent>> ReadPendingAsync(int batchSize, CancellationToken cancellationToken);
    Task<bool> ProjectAsync(AuditIntent intent, CancellationToken cancellationToken);
}

public sealed record AuditProjectionBacklog(long Count, DateTimeOffset? OldestOccurredAt);
public sealed record AuditProjectionBatch(int Read, int Projected);

public sealed class AuditIntentProjector(IAuditIntentProjectionStore store)
{
    public async Task<AuditProjectionBatch> ProjectBatchAsync(int batchSize, CancellationToken cancellationToken)
    {
        if (batchSize is < 1 or > 500)
            throw new ArgumentOutOfRangeException(nameof(batchSize), "Audit projection batch size must be between 1 and 500.");
        IReadOnlyList<AuditIntent> intents = await store.ReadPendingAsync(batchSize, cancellationToken);
        int projected = 0;
        foreach (AuditIntent intent in intents)
        {
            if (await store.ProjectAsync(intent, cancellationToken)) projected++;
        }
        return new AuditProjectionBatch(intents.Count, projected);
    }
}

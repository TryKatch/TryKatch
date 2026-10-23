using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Trykatch.Infrastructure.Execution;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Modules;

namespace Trykatch.Infrastructure.Jobs;

internal static class ModuleJobIntent
{
    private static readonly JsonSerializerOptions EnvelopeOptions = new(JsonSerializerDefaults.Web);

    public static DateTimeOffset NormalizeDue(DateTimeOffset dueAtUtc)
    {
        DateTimeOffset utc = dueAtUtc.ToUniversalTime();
        return utc.AddTicks(-(utc.Ticks % 10));
    }

    public static string IntentHash(string payload, string mode, DateTimeOffset? dueAtUtc)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (mode is not ("enqueue" or "schedule"))
            throw new ArgumentOutOfRangeException(nameof(mode));
        if ((mode == "enqueue") != (dueAtUtc is null))
            throw new ArgumentException("The submission mode and due time do not match.", nameof(dueAtUtc));

        string payloadHash = OutboxPayloadHash.Compute(payload);
        DateTimeOffset? normalizedDue = dueAtUtc is { } due ? NormalizeDue(due) : null;
        byte[] canonicalIntent = JsonSerializer.SerializeToUtf8Bytes(new IntentValue(payloadHash, mode, normalizedDue), EnvelopeOptions);
        return Convert.ToHexString(SHA256.HashData(canonicalIntent));
    }

    public static string RequestIdentity(Guid actorId)
    {
        if (actorId == Guid.Empty) throw new ArgumentException("An actor is required.", nameof(actorId));
        return $"user/{actorId:N}";
    }

    public static ModuleWorkItem CreateWork(
        ModuleJobRegistration registration,
        Guid organizationId,
        ModuleExecutionIdentity identity,
        Guid requestId,
        string identityKey,
        string payload,
        string mode,
        DateTimeOffset? dueAtUtc,
        DateTimeOffset now,
        string? scheduleKey = null,
        string? scheduleTargetKey = null,
        DateTimeOffset? occurrenceAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(identityKey);
        ArgumentNullException.ThrowIfNull(payload);
        ModuleJobDescriptor job = registration.Descriptor;
        ModuleOperationDescriptor operation = registration.Operation.Descriptor;
        if (organizationId == Guid.Empty || requestId == Guid.Empty
            || identity.OrganizationId != organizationId || identity.EffectiveActorId == Guid.Empty
            || identity.ExecutionKind != operation.ExecutionKind
            || operation.ExecutionKind is not (ModuleExecutionKind.UserCommand or ModuleExecutionKind.ScheduledSystem)
            || identity.CatalogVersion != ModuleOperationRegistry.CatalogVersion
            || job.ModuleId != operation.ModuleId || job.OperationKey != operation.OperationKey
            || job.OperationVersion != operation.OperationVersion || job.ExecutionKind != operation.ExecutionKind
            || job.PayloadSchemaKey != operation.PayloadSchemaKey
            || job.PayloadSchemaVersion != operation.PayloadSchemaVersion
            || job.MaximumPayloadBytes > operation.MaximumPayloadBytes)
            throw new ArgumentException("The registered job, operation, and execution identity are inconsistent.", nameof(registration));
        if (operation.ExecutionKind == ModuleExecutionKind.UserCommand
            && (identity.MembershipId is null || identity.MembershipId == Guid.Empty
                || identity.OriginActorId != identity.EffectiveActorId
                || identity.GrantKey is not null || identity.GrantVersion is not null || identity.SourceMessageId is not null))
            throw new ArgumentException("A user command requires membership provenance and cannot use a system grant.", nameof(identity));
        if (operation.ExecutionKind == ModuleExecutionKind.ScheduledSystem
            && (identity.MembershipId is not null || identity.GrantKey is null || identity.GrantVersion is null
                || identity.GrantVersion <= 0 || identity.OriginActorId == Guid.Empty
                || identity.SourceMessageId is not null))
            throw new ArgumentException("Scheduled work requires trusted system-grant provenance.", nameof(identity));
        bool hasScheduleScope = scheduleKey is not null || scheduleTargetKey is not null || occurrenceAtUtc is not null;
        if (hasScheduleScope && (operation.ExecutionKind != ModuleExecutionKind.ScheduledSystem
                || string.IsNullOrWhiteSpace(scheduleKey) || scheduleKey.Length > 120
                || string.IsNullOrWhiteSpace(scheduleTargetKey) || scheduleTargetKey.Length > 120
                || occurrenceAtUtc is null))
            throw new ArgumentException("Recurring work requires a scheduled-system identity and complete occurrence scope.", nameof(scheduleKey));
        if (Encoding.UTF8.GetByteCount(payload) > Math.Min(job.MaximumPayloadBytes, operation.MaximumPayloadBytes))
            throw new ArgumentException("The job payload exceeds its declared size limit.", nameof(payload));

        using JsonDocument parsedPayload = JsonDocument.Parse(payload);
        string normalizedPayload = JsonSerializer.Serialize(parsedPayload.RootElement, EnvelopeOptions);
        string payloadHash = OutboxPayloadHash.Compute(normalizedPayload);
        DateTimeOffset? normalizedDue = dueAtUtc is { } due ? NormalizeDue(due) : null;
        DateTimeOffset? normalizedOccurrence = occurrenceAtUtc is { } occurrence ? NormalizeDue(occurrence) : null;
        string envelope = JsonSerializer.Serialize(new ModuleWorkEnvelope(1, job.ModuleId, identity), EnvelopeOptions);
        return new ModuleWorkItem
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            ActorId = identity.EffectiveActorId,
            RequestId = requestId,
            ExecutionIdentityKey = identityKey,
            JobKey = job.JobKey,
            JobVersion = job.JobVersion,
            OperationKey = operation.OperationKey,
            OperationVersion = operation.OperationVersion,
            ContextVersion = 1,
            ExecutionContextJson = envelope,
            Payload = normalizedPayload,
            PayloadHash = payloadHash,
            IntentHash = IntentHash(normalizedPayload, mode, normalizedDue),
            SubmissionMode = mode,
            DueAtUtc = normalizedDue,
            NextAttemptAtUtc = normalizedDue,
            ScheduleKey = scheduleKey,
            ScheduleTargetKey = scheduleTargetKey,
            OccurrenceAtUtc = normalizedOccurrence,
            State = "queued",
            Attempts = 0,
            CreatedAtUtc = NormalizeDue(now)
        };
    }

    private sealed record IntentValue(string PayloadHash, string Mode, DateTimeOffset? DueAtUtc);
}

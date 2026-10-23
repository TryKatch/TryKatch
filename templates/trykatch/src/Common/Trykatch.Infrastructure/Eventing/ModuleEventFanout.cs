using Microsoft.EntityFrameworkCore;
using Trykatch.Application.Outbox;
using Trykatch.Infrastructure.Execution;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Modules;

namespace Trykatch.Infrastructure.Eventing;

/// <summary>A sanitized compatibility failure that the outbox terminalizes durably.</summary>
internal sealed class ModuleEventCompatibilityException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>
/// Persists the installed-consumer snapshot in the source outbox transaction.
/// This adapter is invoked directly by <see cref="OutboxDelivery"/> and is not
/// registered as the configured external outbox transport.
/// </summary>
internal sealed class ModuleEventFanout(
    OutboxDbContext dbContext,
    ModuleEventRegistry registry,
    TimeProvider timeProvider) : IOutboxTransport
{
    public bool CanFanout(OutboxEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        OutboxContext context = envelope.Context
            ?? throw new ModuleEventCompatibilityException("outbox_context_invalid");

        ValidateSource(context, envelope.Payload);
        return registry.ConsumersFor(context.EventKey, context.SchemaVersion).Count > 0;
    }

    public async Task PublishAsync(OutboxEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        try
        {
            await PublishCoreAsync(envelope, cancellationToken);
        }
        catch (ModuleEventCompatibilityException)
        {
            foreach (ModuleEventDelivery pending in dbContext.ModuleEventDeliveries.Local
                         .Where(delivery => delivery.MessageId == envelope.MessageId)
                         .ToArray())
            {
                if (dbContext.Entry(pending).State == EntityState.Added)
                    dbContext.Entry(pending).State = EntityState.Detached;
            }
            throw;
        }
    }

    private async Task PublishCoreAsync(OutboxEnvelope envelope, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (envelope.Context is not { } context)
            throw new ModuleEventCompatibilityException("outbox_context_invalid");

        ValidateSource(context, envelope.Payload);
        IReadOnlyList<ModuleEventConsumerRegistration> consumers = registry.ConsumersFor(context.EventKey, context.SchemaVersion);
        if (consumers.Count == 0)
            throw new ModuleEventCompatibilityException("event_consumers_unavailable");

        if (dbContext.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Module event fan-out requires the source outbox transaction.");

        OutboxMessage? source = await dbContext.Messages
            .FromSqlInterpolated($"SELECT * FROM platform.outbox_messages WHERE \"Id\" = {envelope.MessageId} FOR UPDATE")
            .AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken);
        if (source is null)
            throw new ModuleEventCompatibilityException("outbox_source_missing");

        if (source.Id != envelope.MessageId
            || source.Type != envelope.MessageType
            || source.Payload != envelope.Payload
            || source.OccurredAt != envelope.OccurredAt
            || source.PayloadHash != envelope.PayloadHash
            || !OutboxContextReader.TryRead(source, out OutboxContext? durableContext, out bool legacy)
            || legacy
            || durableContext != context)
            throw new ModuleEventCompatibilityException("outbox_source_mismatch");

        if (source.ProcessedAt is not null)
            return;

        ModuleEventDelivery[] persisted = await dbContext.ModuleEventDeliveries
            .Where(delivery => delivery.MessageId == source.Id)
            .AsNoTracking()
            .ToArrayAsync(cancellationToken);
        Dictionary<string, ModuleEventDelivery> byConsumer = new(StringComparer.Ordinal);
        foreach (ModuleEventDelivery delivery in persisted)
        {
            if (!byConsumer.TryAdd(delivery.ConsumerKey, delivery))
                throw new ModuleEventCompatibilityException("event_delivery_snapshot_mismatch");
        }
        HashSet<string> localConsumers = new(StringComparer.Ordinal);
        foreach (ModuleEventDelivery delivery in dbContext.ModuleEventDeliveries.Local
                     .Where(delivery => delivery.MessageId == source.Id))
        {
            if (!localConsumers.Add(delivery.ConsumerKey))
                throw new ModuleEventCompatibilityException("event_delivery_snapshot_mismatch");
            if (byConsumer.TryGetValue(delivery.ConsumerKey, out ModuleEventDelivery? existing)
                && !ReferenceEquals(existing, delivery)
                && !SnapshotMatches(existing, delivery))
                throw new ModuleEventCompatibilityException("event_delivery_snapshot_mismatch");
            byConsumer[delivery.ConsumerKey] = delivery;
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        List<ModuleEventDelivery> additions = [];
        foreach (ModuleEventConsumerRegistration registration in consumers)
        {
            ModuleEventDelivery expected = CreateSnapshot(source, context, registration, now);
            if (byConsumer.TryGetValue(registration.Descriptor.ConsumerKey, out ModuleEventDelivery? current))
            {
                if (!SnapshotMatches(current, expected))
                    throw new ModuleEventCompatibilityException("event_delivery_snapshot_mismatch");
                continue;
            }

            additions.Add(expected);
        }

        dbContext.ModuleEventDeliveries.AddRange(additions);
    }

    private IntegrationEventDescriptor ValidateSource(OutboxContext context, string payload)
    {
        if (!registry.TryValidateSource(context, payload, out IntegrationEventDescriptor? descriptor, out string? failureCode)
            || descriptor is null)
            throw new ModuleEventCompatibilityException(SanitizeCode(failureCode));
        return descriptor;
    }

    private static ModuleEventDelivery CreateSnapshot(
        OutboxMessage source,
        OutboxContext context,
        ModuleEventConsumerRegistration registration,
        DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        MessageId = source.Id,
        OrganizationId = context.OrganizationId,
        ConsumerModuleId = registration.Descriptor.OwnerModuleId,
        ConsumerKey = registration.Descriptor.ConsumerKey,
        EventKey = context.EventKey,
        OperationKey = registration.Operation.OperationKey,
        OperationVersion = registration.Operation.OperationVersion,
        SchemaVersion = context.SchemaVersion,
        PayloadHash = source.PayloadHash!,
        EffectiveActorId = registration.Grant.AuditActorId,
        OriginActorId = context.OriginActorId,
        GrantKey = registration.Grant.GrantKey,
        GrantVersion = registration.Grant.GrantVersion,
        CreatedAtUtc = now,
        NextAttemptAtUtc = now,
        ReplayGeneration = source.ReplayGeneration
    };

    private static bool SnapshotMatches(ModuleEventDelivery actual, ModuleEventDelivery expected) =>
        actual.MessageId == expected.MessageId
        && actual.OrganizationId == expected.OrganizationId
        && actual.ConsumerModuleId == expected.ConsumerModuleId
        && actual.ConsumerKey == expected.ConsumerKey
        && actual.EventKey == expected.EventKey
        && actual.OperationKey == expected.OperationKey
        && actual.OperationVersion == expected.OperationVersion
        && actual.SchemaVersion == expected.SchemaVersion
        && actual.PayloadHash == expected.PayloadHash
        && actual.EffectiveActorId == expected.EffectiveActorId
        && actual.OriginActorId == expected.OriginActorId
        && actual.GrantKey == expected.GrantKey
        && actual.GrantVersion == expected.GrantVersion
        && actual.ReplayGeneration == expected.ReplayGeneration;

    private static string SanitizeCode(string? code) => code is { Length: > 0 and <= 80 }
        && code.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-')
            ? code
            : "event_source_incompatible";
}

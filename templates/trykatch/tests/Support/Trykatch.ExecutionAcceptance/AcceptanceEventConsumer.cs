using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Modules;

namespace Trykatch.ExecutionAcceptance;

public sealed class AcceptanceEventConsumer(ApplicationDbContext db) : IModuleEventConsumer
{
    public async Task HandleAsync(
        ModuleEventContext context,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        Guid projectId = ReadGuid(payload, "projectId");
        Guid organizationId = ReadGuid(payload, "organizationId");
        Guid actorId = ReadGuid(payload, "actorId");
        if (organizationId != context.OrganizationId || actorId != context.OriginActorId)
            throw new InvalidOperationException("Projects event provenance does not match the delivery context.");

        db.Set<AcceptanceEffect>().Add(new AcceptanceEffect
        {
            Id = context.MessageId,
            OrganizationId = context.OrganizationId,
            Kind = AcceptanceEffect.ProjectEvent,
            ActorId = actorId,
            EventMessageId = context.MessageId,
            ProjectId = projectId,
            EffectiveActorId = context.EffectiveActorId,
            OriginActorId = context.OriginActorId
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    private static Guid ReadGuid(JsonElement payload, string name) =>
        payload.ValueKind == JsonValueKind.Object
        && payload.TryGetProperty(name, out JsonElement value)
        && value.TryGetGuid(out Guid id)
        && id != Guid.Empty
            ? id
            : throw new JsonException($"Projects event field '{name}' must be a non-empty GUID.");
}

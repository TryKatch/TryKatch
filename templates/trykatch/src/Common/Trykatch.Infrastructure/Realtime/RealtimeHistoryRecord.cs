using Microsoft.EntityFrameworkCore;
using Trykatch.Modules;

namespace Trykatch.Infrastructure.Realtime;

/// <summary>A transactionally serialized position within one organization's committed history.</summary>
internal sealed class RealtimeStream : IOrganizationOwned
{
    public Guid OrganizationId { get; init; }
    public long Sequence { get; init; }

    internal static void Configure(ModelBuilder modelBuilder) => modelBuilder.Entity<RealtimeStream>(entity =>
    {
        entity.ToTable("realtime_streams", "app", table =>
            table.HasCheckConstraint("CK_realtime_streams_sequence", "\"Sequence\" > 0"));
        entity.HasKey(value => value.OrganizationId);
        entity.Property(value => value.OrganizationId).ValueGeneratedNever();
    });
}

/// <summary>Immutable invalidation references; runtime roles cannot update or delete history.</summary>
internal sealed class RealtimeHistoryRecord : IOrganizationOwned
{
    public Guid OrganizationId { get; init; }
    public long Sequence { get; init; }
    public string ModuleId { get; init; } = string.Empty;
    public string EventKey { get; init; } = string.Empty;
    public int SchemaVersion { get; init; }
    public Guid EventId { get; init; }
    public string PolicyOwnerModuleId { get; init; } = string.Empty;
    public string PolicyKey { get; init; } = string.Empty;
    public int PolicyVersion { get; init; }
    public ModuleRealtimeAudienceKind AudienceKind { get; init; }
    public Guid? UserId { get; init; }
    public string? ResourceKey { get; init; }
    public Guid? ResourceId { get; init; }
    public Guid SourceMessageId { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }

    internal ModuleRealtimeNotification ToNotification() => new(ModuleId, EventKey, SchemaVersion, EventId,
        new(new(PolicyOwnerModuleId, PolicyKey, PolicyVersion), AudienceKind, UserId,
            ResourceKey is not null && ResourceId.HasValue ? new(ResourceKey, ResourceId.Value) : null));

    internal static void Configure(ModelBuilder modelBuilder) => modelBuilder.Entity<RealtimeHistoryRecord>(entity =>
    {
        entity.ToTable("realtime_history", "app", table =>
        {
            table.HasCheckConstraint("CK_realtime_history_versions", "\"Sequence\" > 0 AND \"SchemaVersion\" > 0 AND \"PolicyVersion\" > 0");
            table.HasCheckConstraint("CK_realtime_history_identity", """
                "EventId" <> '00000000-0000-0000-0000-000000000000'::uuid
                AND "SourceMessageId" <> '00000000-0000-0000-0000-000000000000'::uuid
                """);
            table.HasCheckConstraint("CK_realtime_history_audience", """
                (("AudienceKind" = 0 AND "UserId" IS NULL)
                  OR ("AudienceKind" = 1 AND "UserId" IS NOT NULL AND "UserId" <> '00000000-0000-0000-0000-000000000000'::uuid)
                  OR ("AudienceKind" = 2 AND "UserId" IS NULL AND "ResourceId" IS NOT NULL))
                AND (("ResourceKey" IS NULL AND "ResourceId" IS NULL)
                  OR ("ResourceKey" IS NOT NULL AND "ResourceId" IS NOT NULL AND "ResourceId" <> '00000000-0000-0000-0000-000000000000'::uuid))
                """);
        });
        entity.HasKey(value => new { value.OrganizationId, value.Sequence });
        entity.Property(value => value.Sequence).ValueGeneratedNever();
        entity.Property(value => value.ModuleId).HasMaxLength(80);
        entity.Property(value => value.EventKey).HasMaxLength(160);
        entity.Property(value => value.PolicyOwnerModuleId).HasMaxLength(80);
        entity.Property(value => value.PolicyKey).HasMaxLength(160);
        entity.Property(value => value.ResourceKey).HasMaxLength(160);
        entity.HasIndex(value => new { value.OrganizationId, value.ModuleId, value.EventId }).IsUnique();
        entity.HasIndex(value => new { value.OrganizationId, value.CreatedAtUtc });
    });
}

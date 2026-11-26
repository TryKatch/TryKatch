using Microsoft.EntityFrameworkCore;
using Trykatch.Modules;

namespace Trykatch.Infrastructure.Idempotency;

/// <summary>Completed response only; the enclosing organization transaction owns its lifetime.</summary>
internal sealed class MutationReceiptRecord : IOrganizationOwned
{
    public Guid OrganizationId { get; init; }
    public Guid ActorId { get; init; }
    public string ModuleId { get; init; } = string.Empty;
    public string OperationId { get; init; } = string.Empty;
    public string KeyHash { get; init; } = string.Empty;
    public string RequestHash { get; init; } = string.Empty;
    public int StatusCode { get; init; }
    public string? ContentType { get; init; }
    public string? Location { get; init; }
    public byte[] Body { get; init; } = [];
    public string? ResourceKey { get; init; }
    public Guid? ResourceId { get; init; }
    public DateTimeOffset CompletedAtUtc { get; init; }
    public DateTimeOffset ExpiresAtUtc { get; init; }

    internal static void Configure(ModelBuilder modelBuilder) => modelBuilder.Entity<MutationReceiptRecord>(entity =>
    {
        entity.ToTable("mutation_receipts", "app", table =>
        {
            table.HasCheckConstraint("CK_mutation_receipts_principal", """
                "OrganizationId" <> '00000000-0000-0000-0000-000000000000'::uuid
                AND "ActorId" <> '00000000-0000-0000-0000-000000000000'::uuid
                """);
            table.HasCheckConstraint("CK_mutation_receipts_identity", """
                "ModuleId" ~ '^[A-Za-z0-9][A-Za-z0-9._:-]*$'
                AND "OperationId" ~ '^[A-Za-z0-9][A-Za-z0-9._:-]*$'
                AND "KeyHash" ~ '^[0-9A-F]{64}$' AND "RequestHash" ~ '^[0-9A-F]{64}$'
                """);
            table.HasCheckConstraint("CK_mutation_receipts_response", """
                "StatusCode" BETWEEN 200 AND 299 AND octet_length("Body") <= 1048576
                AND ("ContentType" IS NULL OR (length("ContentType") > 0 AND "ContentType" !~ '[[:cntrl:]]'))
                AND ("Location" IS NULL OR (left("Location", 1) = '/' AND left("Location", 2) <> '//'
                    AND strpos("Location", chr(92)) = 0 AND "Location" !~ '[[:cntrl:]]'))
                """);
            table.HasCheckConstraint("CK_mutation_receipts_resource", """
                ("ResourceKey" IS NULL AND "ResourceId" IS NULL)
                OR ("ResourceKey" IS NOT NULL AND "ResourceId" IS NOT NULL
                    AND "ResourceKey" ~ '^[A-Za-z0-9][A-Za-z0-9._:-]*$'
                    AND "ResourceId" <> '00000000-0000-0000-0000-000000000000'::uuid)
                """);
            table.HasCheckConstraint("CK_mutation_receipts_lifetime", """
                "ExpiresAtUtc" >= "CompletedAtUtc" + interval '1 minute'
                AND "ExpiresAtUtc" <= "CompletedAtUtc" + interval '24 hours'
                """);
        });
        entity.HasKey(value => new { value.OrganizationId, value.ActorId, value.ModuleId, value.OperationId, value.KeyHash });
        entity.Property(value => value.ModuleId).HasMaxLength(80);
        entity.Property(value => value.OperationId).HasMaxLength(160);
        entity.Property(value => value.KeyHash).HasMaxLength(64);
        entity.Property(value => value.RequestHash).HasMaxLength(64);
        entity.Property(value => value.ContentType).HasMaxLength(256);
        entity.Property(value => value.Location).HasMaxLength(2048);
        entity.Property(value => value.ResourceKey).HasMaxLength(120);
        entity.HasIndex(value => new { value.OrganizationId, value.ExpiresAtUtc });
    });
}

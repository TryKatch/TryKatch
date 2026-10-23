using Microsoft.EntityFrameworkCore;
using Trykatch.Modules;

namespace Trykatch.Infrastructure.Execution;

/// <summary>Committed with the business effect, independently of coordinator acknowledgement.</summary>
internal sealed class ModuleWorkCompletion : IOrganizationOwned
{
    public Guid OrganizationId { get; init; }
    public Guid WorkId { get; init; }
    public string OperationKey { get; init; } = string.Empty;
    public int OperationVersion { get; init; }
    public Guid ActorId { get; init; }
    public string PayloadHash { get; init; } = string.Empty;
    public DateTimeOffset CompletedAtUtc { get; init; }

    internal static void Configure(ModelBuilder modelBuilder) => modelBuilder.Entity<ModuleWorkCompletion>(entity =>
    {
        entity.ToTable("module_work_completions", "app", table =>
            table.HasCheckConstraint("CK_module_completion_version", "\"OperationVersion\" > 0"));
        entity.HasKey(x => new { x.OrganizationId, x.WorkId });
        entity.Property(x => x.OperationKey).HasMaxLength(120);
        entity.Property(x => x.PayloadHash).HasMaxLength(64);
        entity.HasIndex(x => new { x.OrganizationId, x.CompletedAtUtc });
    });
}

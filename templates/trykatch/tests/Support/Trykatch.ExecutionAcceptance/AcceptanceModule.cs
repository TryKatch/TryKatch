using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Trykatch.Application.Execution;
using Trykatch.Domain.Organizations;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Modules;

namespace Trykatch.ExecutionAcceptance;

public sealed class AcceptanceModule : IModule
{
    public const string Id = "execution-acceptance";
    public const string ResourceKey = "execution-acceptance.effects";
    public const string EventOperation = "execution-acceptance.apply-project-event";
    public const string JobOperation = "execution-acceptance.complete-job";
    public const string JobKey = "execution-acceptance.job";
    public const string ConsumerKey = "execution-acceptance.project-created";
    public const string EventGrant = "execution-acceptance.project-created";
    public static readonly Guid AuditActorId = Guid.Parse("dcf5b2bf-c778-4f49-a1d6-e85fe90c54f6");

    private static readonly string[] NoPermissions = [];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public ModuleDescriptor Descriptor { get; } = new(
        Id,
        "Execution process acceptance",
        "1.0.0",
        "Test-only effects for installed Projects event process acceptance.",
        ["projects"],
        [],
        ModuleCapabilities.BackgroundWork | ModuleCapabilities.Data,
        [])
    {
        DefaultDataOwnership = ModuleDataOwnership.Organization,
        DataResources =
        [
            new(ResourceKey, "app", "execution_acceptance_effects", ModuleDataOwnership.Organization,
                typeof(AcceptanceEffect).FullName, "execution_acceptance_effects_organization")
        ],
        Operations =
        [
            new(Id, EventOperation, 1, ModuleExecutionKind.CommittedEvent,
                "execution-acceptance.project-created-payload", 1, NoPermissions, ResourceKey, EventGrant, 2048),
            new(Id, JobOperation, 1, ModuleExecutionKind.UserCommand,
                "execution-acceptance.job-payload", 1, ["projects.manage"], ResourceKey, null, 1024)
        ],
        SystemGrants =
        [new(EventGrant, 1, Id, AuditActorId, [EventOperation], [ResourceKey])],
        EventConsumers =
        [new(ConsumerKey, Id, [new("projects.created", 1)], EventOperation, 1, EventGrant)],
        Jobs =
        [new(Id, JobKey, 1, JobOperation, 1, ModuleExecutionKind.UserCommand,
            "execution-acceptance.job-payload", 1, 1024)]
    };

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IApplicationModelContributor, AcceptanceModelContributor>();
        services.AddScoped<IModuleOperationHandler, AcceptanceJobHandler>();
        services.AddScoped<AcceptanceEventConsumer>();
        services.AddScoped<ModuleEventConsumerBinding>(provider => new(
            Id, ConsumerKey, provider.GetRequiredService<AcceptanceEventConsumer>()));
        services.AddScoped<IModuleOperationResourcePolicy, AcceptanceResourcePolicy>();
    }

    private sealed class AcceptanceModelContributor : IApplicationModelContributor
    {
        public string ModuleId => Id;

        public void Configure(ModelBuilder modelBuilder) => modelBuilder.Entity<AcceptanceEffect>(entity =>
        {
            entity.ToTable("execution_acceptance_effects", "app");
            entity.HasKey(effect => effect.Id);
            entity.HasIndex(effect => new { effect.OrganizationId, effect.Id });
            entity.HasIndex(effect => new { effect.OrganizationId, effect.EventMessageId })
                .IsUnique().HasFilter("\"EventMessageId\" IS NOT NULL");
            entity.Property(effect => effect.Kind).HasMaxLength(40);
        });
    }

    private sealed class AcceptanceResourcePolicy(ApplicationDbContext db) : IModuleOperationResourcePolicy
    {
        public string ModuleId => Id;
        public string PolicyKey => ResourceKey;

        public async Task<bool> AuthorizeAsync(
            ModuleOperationContext context,
            JsonElement payload,
            CancellationToken cancellationToken)
        {
            if (context.ExecutionKind == ModuleExecutionKind.CommittedEvent
                && context.OperationKey == EventOperation
                && context.OperationVersion == 1)
            {
                return ReadGuid(payload, "organizationId") == context.OrganizationId
                    && ReadGuid(payload, "actorId") == context.OriginActorId
                    && ReadGuid(payload, "projectId") != Guid.Empty
                    && context.EffectiveActorId == AuditActorId;
            }
            if (context.ExecutionKind != ModuleExecutionKind.UserCommand
                || context.OperationKey != JobOperation
                || context.OperationVersion != 1)
                return false;

            AcceptanceJobPayload? request;
            try { request = payload.Deserialize<AcceptanceJobPayload>(Json); }
            catch (JsonException) { return false; }
            if (request is null || request.TargetId == Guid.Empty) return false;

            return await db.Set<AcceptanceEffect>().AsNoTracking().AnyAsync(effect =>
                effect.Id == request.TargetId
                && effect.OrganizationId == context.OrganizationId
                && effect.Kind == AcceptanceEffect.SyntheticTarget
                && effect.ActorId == context.EffectiveActorId
                && effect.CompletedAtUtc == null, cancellationToken);
        }

        private static Guid ReadGuid(JsonElement payload, string name) =>
            payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty(name, out JsonElement value)
            && value.TryGetGuid(out Guid id)
                ? id
                : Guid.Empty;
    }

    private sealed class AcceptanceJobHandler(ApplicationDbContext db) : IModuleOperationHandler
    {
        public string ModuleId => Id;
        public string OperationKey => JobOperation;
        public int OperationVersion => 1;

        public async Task HandleAsync(
            ModuleOperationContext context,
            JsonElement payload,
            CancellationToken cancellationToken)
        {
            AcceptanceJobPayload request = payload.Deserialize<AcceptanceJobPayload>(Json)
                ?? throw new JsonException("Acceptance job payload is required.");
            AcceptanceEffect? target = await db.Set<AcceptanceEffect>().SingleOrDefaultAsync(effect =>
                effect.Id == request.TargetId
                && effect.OrganizationId == context.OrganizationId
                && effect.Kind == AcceptanceEffect.SyntheticTarget
                && effect.ActorId == context.EffectiveActorId
                && effect.CompletedAtUtc == null, cancellationToken);
            if (target is null) throw new InvalidOperationException("Acceptance job target is unavailable.");
            target.CompletedAtUtc = DateTimeOffset.UtcNow;
            target.EffectiveActorId = context.EffectiveActorId;
            target.OriginActorId = context.OriginActorId;
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}

public sealed class AcceptanceEffect : IOrganizationOwned
{
    public const string SyntheticTarget = "synthetic-target";
    public const string ProjectEvent = "project-created";

    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string Kind { get; set; } = string.Empty;
    public Guid ActorId { get; set; }
    public Guid? EventMessageId { get; set; }
    public Guid? ProjectId { get; set; }
    public Guid? EffectiveActorId { get; set; }
    public Guid? OriginActorId { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
}

public sealed record AcceptanceJobPayload(Guid TargetId);

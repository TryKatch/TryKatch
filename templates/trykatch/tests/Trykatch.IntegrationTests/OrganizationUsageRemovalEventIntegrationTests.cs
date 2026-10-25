using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Trykatch.Application;
using Trykatch.Application.Execution;
using Trykatch.Application.Outbox;
using Trykatch.Application.Quotas;
using Trykatch.Infrastructure;
using Trykatch.Infrastructure.Eventing;
using Trykatch.Infrastructure.Execution;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Modules;
using Trykatch.Modules.Documents.Infrastructure;
using Trykatch.Modules.Projects.Infrastructure;

namespace Trykatch.IntegrationTests;

public sealed partial class ModuleWorkExecutionIntegrationTests
{
    private const string RemovalEventKey = "probe.usage-removal-requested";
    private const string RemovalEventConsumer = "probe.usage-removal-consumer";
    private const string RemovalEventOperation = "probe.remove-usage";
    private const string RemovalEventGrant = "probe.removal-event-grant";
    private const string RemovalEventResourcePolicy = "probe.records";
    private static readonly JsonSerializerOptions RemovalEventJson = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public async Task CommittedEventRetryAndDuplicateDeliveryShareOneRemovalOperation()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync(["probe.write"]);
        await fixture.SeedRemovalQuotaProjectionAsync(user.OrganizationId);
        await using RemovalEventEnvironment environment = fixture.CreateRemovalEventEnvironment();
        SeededUsageEffect source = await fixture.SeedUsageRemovalEffectAsync(user, units: 8);
        Guid operationId = Guid.CreateVersion7();
        Guid firstMessage = await fixture.SeedRemovalRequestedEventAsync(environment, user, source, operationId);
        Guid duplicateMessage = await fixture.SeedRemovalRequestedEventAsync(environment, user, source, operationId);

        await fixture.FanOutRemovalEventUntilAvailableAsync(environment, firstMessage);
        Guid firstDelivery = await fixture.RemovalEventDeliveryIdAsync(firstMessage);
        await fixture.FanOutRemovalEventUntilAvailableAsync(environment, duplicateMessage);
        Guid duplicateDelivery = await fixture.RemovalEventDeliveryIdAsync(duplicateMessage);
        environment.Behavior.FailNextAfterDebit();
        (await fixture.RevokeMembershipAsync(user.OrganizationId, user.ActorId)).ShouldBe(1);

        (await environment.ProcessAsync(firstDelivery)).State.ShouldBe(ModuleExecutionState.RetryableFailure);
        (await fixture.ReadUsageEffectsAsync(user.OrganizationId, source.EffectId)).ShouldBe(1);
        (await fixture.SumUsageForResourceAsync(user.OrganizationId)).ShouldBe(8);
        (await fixture.ReadRemovalInboxCountAsync(user.OrganizationId, firstMessage)).ShouldBe(0);

        await fixture.MakeRemovalEventDueAsync(firstDelivery);
        (await environment.ProcessAsync(firstDelivery)).State.ShouldBe(ModuleExecutionState.Completed);
        (await fixture.ReadUsageEffectsAsync(user.OrganizationId, source.EffectId)).ShouldBe(2);
        (await fixture.SumUsageForResourceAsync(user.OrganizationId)).ShouldBe(0);
        (await fixture.ReadRemovalInboxCountAsync(user.OrganizationId, firstMessage)).ShouldBe(1);
        (await fixture.ReadRemovalDebitActorAsync(user.OrganizationId, source.EffectId))
            .ShouldBe(ProbeModule.AuditActorId);

        (await environment.ProcessAsync(duplicateDelivery)).State.ShouldBe(ModuleExecutionState.Completed);
        (await fixture.ReadUsageEffectsAsync(user.OrganizationId, source.EffectId)).ShouldBe(2);
        (await fixture.SumUsageForResourceAsync(user.OrganizationId)).ShouldBe(0);
        (await fixture.ReadRemovalInboxCountAsync(user.OrganizationId, duplicateMessage)).ShouldBe(1);
        environment.Behavior.Dispositions.ShouldBe([
            OrganizationUsageRemovalDisposition.Recorded,
            OrganizationUsageRemovalDisposition.Recorded,
            OrganizationUsageRemovalDisposition.AlreadyRecorded
        ]);
    }

    [TestMethod]
    public async Task CommittedEventWithoutExplicitQuotaGrantCannotRemoveUsage()
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync(["probe.write"]);
        await fixture.SeedRemovalQuotaProjectionAsync(user.OrganizationId);
        await using RemovalEventEnvironment environment = fixture.CreateRemovalEventEnvironment(allowQuota: false);
        SeededUsageEffect source = await fixture.SeedUsageRemovalEffectAsync(user, units: 8);
        Guid message = await fixture.SeedRemovalRequestedEventAsync(environment, user, source, Guid.CreateVersion7());
        await fixture.FanOutRemovalEventUntilAvailableAsync(environment, message);
        Guid delivery = await fixture.RemovalEventDeliveryIdAsync(message);

        (await environment.ProcessAsync(delivery)).State.ShouldBe(ModuleExecutionState.RetryableFailure);
        environment.Behavior.Dispositions.ShouldBe([OrganizationUsageRemovalDisposition.Denied]);
        (await fixture.ReadUsageEffectsAsync(user.OrganizationId, source.EffectId)).ShouldBe(1);
        (await fixture.SumUsageForResourceAsync(user.OrganizationId)).ShouldBe(8);
        (await fixture.ReadRemovalInboxCountAsync(user.OrganizationId, message)).ShouldBe(0);
    }

    private sealed record RemovalRequestedEvent(
        Guid SourceId,
        Guid SourceEffectId,
        long Units,
        Guid RemovalOperationId);

    private sealed class RemovalEventEnvironment(ServiceProvider services, RemovalEventBehavior behavior) : IAsyncDisposable
    {
        public ServiceProvider Services { get; } = services;
        public RemovalEventBehavior Behavior { get; } = behavior;

        public async Task FanOutAsync()
        {
            await using AsyncServiceScope scope = Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<OutboxBatchProcessor>().ProcessAsync(CancellationToken.None);
        }

        public async Task<ModuleExecutionOutcome> ProcessAsync(Guid deliveryId)
        {
            await using AsyncServiceScope scope = Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<ModuleEventDeliveryProcessor>()
                .ProcessAsync(deliveryId, CancellationToken.None);
        }

        public ValueTask DisposeAsync() => Services.DisposeAsync();
    }

    private sealed class RemovalEventBehavior
    {
        private int failNextAfterDebit;
        private readonly ConcurrentQueue<OrganizationUsageRemovalDisposition> dispositions = new();

        public IReadOnlyList<OrganizationUsageRemovalDisposition> Dispositions => dispositions.ToArray();
        public void FailNextAfterDebit() => Interlocked.Exchange(ref failNextAfterDebit, 1);
        public bool ShouldFailAfterDebit() => Interlocked.Exchange(ref failNextAfterDebit, 0) == 1;
        public void Record(OrganizationUsageRemovalDisposition disposition) => dispositions.Enqueue(disposition);
    }

    private sealed class EventRemovalProbeModule(bool allowQuota) : IModule
    {
        private readonly RemovalProbeModule inner = new();

        public ModuleDescriptor Descriptor { get; } = ExtendDescriptor(new RemovalProbeModule().Descriptor, allowQuota);

        private static ModuleDescriptor ExtendDescriptor(ModuleDescriptor source, bool allowQuota) => source with
        {
            Operations = source.Operations.Append(new ModuleOperationDescriptor(
                "probe", RemovalEventOperation, 1, ModuleExecutionKind.CommittedEvent,
                "probe.removal-request", 1, [], RemovalEventResourcePolicy, RemovalEventGrant, 2048)).ToArray(),
            SystemGrants = source.SystemGrants.Append(new ModuleSystemGrantDescriptor(
                RemovalEventGrant, 1, "probe", ProbeModule.AuditActorId,
                [RemovalEventOperation], [RemovalEventResourcePolicy])
            {
                AllowedQuotaResources = allowQuota ? ["probe.units"] : []
            }).ToArray(),
            IntegrationEvents = source.IntegrationEvents.Append(new IntegrationEventDescriptor(
                RemovalEventKey, 1, "probe", typeof(RemovalRequestedEvent),
                element => element.Deserialize<RemovalRequestedEvent>(RemovalEventJson)!, 2048)).ToArray(),
            EventConsumers = source.EventConsumers.Append(new ModuleEventConsumerDescriptor(
                RemovalEventConsumer, "probe", [new(RemovalEventKey, 1)],
                RemovalEventOperation, 1, RemovalEventGrant)).ToArray()
        };

        public void Register(IServiceCollection services, IConfiguration configuration)
        {
            inner.Register(services, configuration);
            // The same declared resource has a specialized marker policy for this event.
            ServiceDescriptor existingPolicy = services.Single(descriptor =>
                descriptor.ServiceType == typeof(IModuleOperationResourcePolicy)
                && descriptor.ImplementationType == typeof(ProbeResourcePolicy));
            services.Remove(existingPolicy);
            services.AddScoped<IModuleOperationResourcePolicy, RemovalEventResourcePolicyAdapter>();
            services.AddScoped<RemovalEventOperationHandler>();
            services.AddScoped<IModuleOperationHandler>(provider => provider.GetRequiredService<RemovalEventOperationHandler>());
            services.AddScoped<ModuleEventConsumerBinding>(provider => new(
                "probe", RemovalEventConsumer, provider.GetRequiredService<RemovalEventOperationHandler>()));
        }
    }

    private sealed class RemovalEventResourcePolicyAdapter(ApplicationDbContext application) : IModuleOperationResourcePolicy
    {
        private readonly ProbeResourcePolicy existing = new(application);
        public string ModuleId => "probe";
        public string PolicyKey => RemovalEventResourcePolicy;

        public Task<bool> AuthorizeAsync(
            ModuleOperationContext context,
            JsonElement payload,
            CancellationToken cancellationToken)
        {
            return context.OperationKey == RemovalEventOperation
                ? ConfirmMarkerAsync(context.OrganizationId, payload, cancellationToken)
                : existing.AuthorizeAsync(context, payload, cancellationToken);
        }

        private async Task<bool> ConfirmMarkerAsync(Guid organizationId, JsonElement payload, CancellationToken cancellationToken)
        {
            RemovalRequestedEvent? request = payload.Deserialize<RemovalRequestedEvent>(RemovalEventJson);
            if (request is null || request.SourceId == Guid.Empty || request.SourceEffectId == Guid.Empty
                || request.RemovalOperationId == Guid.Empty || request.Units <= 0)
                return false;
            string marker = $"permanent-removal/{request.SourceEffectId:N}";
            return await application.Database.SqlQuery<bool>($"""
                SELECT EXISTS (
                    SELECT 1 FROM app.organization_usage AS usage
                    JOIN app.probe_records AS record
                        ON record."OrganizationId" = usage."OrganizationId"
                        AND record."Id" = usage."SourceId"
                        AND record."RecipientActorId" = usage."ActorId"
                    WHERE usage."OrganizationId" = {organizationId} AND usage."ResourceKey" = 'probe.units'
                        AND usage."EffectId" = {request.SourceEffectId} AND usage."SourceId" = {request.SourceId}
                        AND usage."SourceEffectId" IS NULL AND usage."Units" >= {request.Units}
                        AND usage."Units" > 0 AND NOT record."CanDisclose" AND record."Marker" = {marker})
                    AS "Value"
                """).SingleAsync(cancellationToken);
        }
    }

    private sealed class RemovalEventOperationHandler(
        IOrganizationUsageLedger usageLedger,
        RemovalEventBehavior behavior) : IModuleOperationHandler, IModuleEventConsumer
    {
        public string ModuleId => "probe";
        public string OperationKey => RemovalEventOperation;
        public int OperationVersion => 1;

        public Task HandleAsync(ModuleOperationContext context, JsonElement payload, CancellationToken cancellationToken) =>
            RecordRemovalAsync(payload, cancellationToken);

        public Task HandleAsync(ModuleEventContext context, JsonElement payload, CancellationToken cancellationToken) =>
            RecordRemovalAsync(payload, cancellationToken);

        private async Task RecordRemovalAsync(JsonElement payload, CancellationToken cancellationToken)
        {
            RemovalRequestedEvent request = payload.Deserialize<RemovalRequestedEvent>(RemovalEventJson)
                ?? throw new InvalidOperationException("Removal event payload was unavailable.");
            OrganizationUsageRemovalResult result = await usageLedger.RecordRemovalAsync(
                "probe.units", request.SourceEffectId, request.Units, request.RemovalOperationId, cancellationToken);
            behavior.Record(result.Disposition);
            if (result.Disposition is not (OrganizationUsageRemovalDisposition.Recorded or OrganizationUsageRemovalDisposition.AlreadyRecorded))
                throw new InvalidOperationException("Test removal event could not record its confirmed accounting effect.");
            if (result.Disposition == OrganizationUsageRemovalDisposition.Recorded && behavior.ShouldFailAfterDebit())
                throw new InvalidOperationException("Injected failure after transactional removal append.");
        }
    }

    private sealed partial class ExecutionFixture
    {
        public RemovalEventEnvironment CreateRemovalEventEnvironment(bool allowQuota = true)
        {
            IConfiguration configuration = services.GetRequiredService<IConfiguration>();
            ServiceCollection registrations = new();
            registrations.AddSingleton(configuration);
            registrations.AddLogging();
            registrations.AddApplication();
            registrations.AddModules(configuration, [new ProjectsModule(), new DocumentsModule(), new EventRemovalProbeModule(allowQuota)]);
            registrations.AddInfrastructure(configuration);
            registrations.AddSingleton(new ProbeExecutionBehavior());
            RemovalEventBehavior behavior = new();
            registrations.AddSingleton(behavior);
            ServiceProvider provider = registrations.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
            return new(provider, behavior);
        }

        public async Task<Guid> SeedRemovalRequestedEventAsync(
            RemovalEventEnvironment environment,
            ProjectsSeededUser user,
            SeededUsageEffect source,
            Guid operationId)
        {
            Guid messageId = Guid.CreateVersion7();
            string payload = JsonSerializer.Serialize(new RemovalRequestedEvent(
                source.RecordId, source.EffectId, 8, operationId), RemovalEventJson);
            string catalogVersion = OutboxCatalogVersion.Compute(environment.Services.GetRequiredService<ModuleCatalog>());
            OutboxContext eventContext = new(user.OrganizationId, user.ActorId, user.ActorId, user.MembershipId,
                nameof(ModuleExecutionKind.UserCommand), "probe.command", 1, RemovalEventKey, 1, catalogVersion, null);
            await using ApplicationDbContext application = new(
                new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(owner).Options);
            application.OutboxMessages.Add(new OutboxMessage
            {
                Id = messageId,
                Type = RemovalEventKey,
                Payload = payload,
                ContextVersion = 1,
                ContextJson = JsonSerializer.Serialize(eventContext, RemovalEventJson),
                PayloadHash = OutboxPayloadHash.Compute(payload),
                OccurredAt = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero)
            });
            await application.SaveChangesAsync();
            return messageId;
        }

        public async Task FanOutRemovalEventUntilAvailableAsync(RemovalEventEnvironment environment, Guid messageId)
        {
            for (int attempt = 0; attempt < 8; attempt++)
            {
                await environment.FanOutAsync();
                if (await CountOwnerAsync("""
                    SELECT count(*) FROM infrastructure.module_event_deliveries WHERE "MessageId" = @message
                    """, ("message", messageId)) == 1)
                    return;
            }
            throw new InvalidOperationException("The seeded removal event did not reach the installed delivery consumer.");
        }

        public async Task<Guid> RemovalEventDeliveryIdAsync(Guid messageId)
        {
            await using NpgsqlConnection connection = new(owner);
            await connection.OpenAsync();
            await using NpgsqlCommand command = new("""
                SELECT "Id" FROM infrastructure.module_event_deliveries
                WHERE "MessageId" = @message AND "ConsumerKey" = @consumer
                """, connection);
            command.Parameters.AddWithValue("message", messageId);
            command.Parameters.AddWithValue("consumer", RemovalEventConsumer);
            return (Guid)(await command.ExecuteScalarAsync())!;
        }

        public Task MakeRemovalEventDueAsync(Guid deliveryId) => ExecuteOwnerAsync("""
            UPDATE infrastructure.module_event_deliveries
            SET "NextAttemptAtUtc" = CURRENT_TIMESTAMP - interval '1 second', "LeaseUntilUtc" = NULL
            WHERE "Id" = @delivery
            """, ("delivery", deliveryId));

        public Task<long> ReadRemovalInboxCountAsync(Guid organizationId, Guid messageId) => CountOwnerAsync("""
            SELECT count(*) FROM app.module_event_inbox
            WHERE "OrganizationId" = @organization AND "MessageId" = @message
            """, ("organization", organizationId), ("message", messageId));

        public async Task<Guid> ReadRemovalDebitActorAsync(Guid organizationId, Guid sourceEffectId)
        {
            await using NpgsqlConnection connection = new(owner);
            await connection.OpenAsync();
            await using NpgsqlCommand command = new("""
                SELECT "ActorId" FROM app.organization_usage
                WHERE "OrganizationId" = @organization AND "ResourceKey" = 'probe.units'
                    AND "SourceEffectId" = @sourceEffect
                """, connection);
            command.Parameters.AddWithValue("organization", organizationId);
            command.Parameters.AddWithValue("sourceEffect", sourceEffectId);
            return (Guid)(await command.ExecuteScalarAsync())!;
        }
    }
}

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Trykatch.Application.Outbox;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Modules;

namespace Trykatch.IntegrationTests;

public sealed partial class OutboxRecoveryIntegrationTests
{
    private static readonly JsonSerializerOptions TestJsonOptions = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public async Task StoredSizeAccountingMatchesPostgresJsonbForEscapesUtf8AndExponents()
    {
        await using RecoveryHost host = await RecoveryHost.StartAsync();

        const int payloadLimit = 1_048_576;
        const string prefix = "{\"naïve\":\"quoted \\\" punctuation, slash \\\\ café\",\"nested\":[1e+20,-2.5e-6,\"éclair\"],\"padding\":\"";
        const string suffix = "\"}";
        string basePayload = prefix + suffix;
        string baseStored = await host.RoundTripJsonbAsync(basePayload);
        // This object has three property separators, two member commas and two array commas.
        const int formattingBytes = 7;
        int paddingLength = payloadLimit - (Encoding.UTF8.GetByteCount(baseStored) - formattingBytes);

        foreach (int compactStoredLimit in new[] { payloadLimit, payloadLimit + 1 })
        {
            string payload = prefix + new string('x', paddingLength + compactStoredLimit - payloadLimit) + suffix;
            string stored = await host.RoundTripJsonbAsync(payload);
            (Encoding.UTF8.GetByteCount(stored) - formattingBytes).ShouldBe(compactStoredLimit);
            bool expectedFits = compactStoredLimit <= payloadLimit;
            OutboxPayloadSize.FitsStoredJson(stored, payloadLimit).ShouldBe(expectedFits);
            OutboxPayloadSize.FitsSerializedJson(Encoding.UTF8.GetBytes(payload), payloadLimit).ShouldBe(expectedFits);
            Encoding.UTF8.GetByteCount(payload).ShouldBeLessThanOrEqualTo(payloadLimit);
        }
    }

    [TestMethod]
    public async Task ContextualPayloadAtExactOneMibIsDeliveredAfterPostgresJsonbExpansion()
    {
        await using RecoveryHost host = await RecoveryHost.StartAsync(
            transportFailures: 0,
            configureServices: PayloadBoundaryModule.AddToCatalog);
        (await host.ReadServerVersionAsync()).ShouldStartWith("19.");

        const int payloadLimit = 1_048_576;
        const string prefix = "{\"value\":\"";
        const string suffix = "\"}";
        string exactPayload = prefix + new string('x', payloadLimit - Encoding.UTF8.GetByteCount(prefix + suffix)) + suffix;
        Encoding.UTF8.GetByteCount(exactPayload).ShouldBe(payloadLimit);

        string expandedPayload = await host.RoundTripJsonbAsync(exactPayload);
        Encoding.UTF8.GetByteCount(expandedPayload).ShouldBeGreaterThan(payloadLimit);

        ModuleCatalog catalog = host.Services.GetRequiredService<ModuleCatalog>();
        Guid organizationId = Guid.CreateVersion7();
        Guid actorId = Guid.CreateVersion7();
        Guid membershipId = Guid.CreateVersion7();
        OutboxContext context = new(
            organizationId, actorId, actorId, membershipId,
            nameof(ModuleExecutionKind.UserCommand), PayloadBoundaryModule.OperationKey, 1,
            PayloadBoundaryModule.EventKey, 1, OutboxCatalogVersion.Compute(catalog), null);

        Guid exactMessageId = Guid.CreateVersion7();
        await host.InsertContextualAsync(exactMessageId, PayloadBoundaryModule.EventKey, exactPayload, context);

        Guid oversizedMessageId = Guid.CreateVersion7();
        string oversizedPayload = exactPayload.Insert(exactPayload.Length - suffix.Length, "x");
        Encoding.UTF8.GetByteCount(oversizedPayload).ShouldBe(payloadLimit + 1);
        await host.InsertContextualAsync(oversizedMessageId, PayloadBoundaryModule.EventKey, oversizedPayload, context);

        await host.WaitForProcessedAsync(exactMessageId, generation: 0);
        await host.WaitForAsync(oversizedMessageId, exhausted: true);

        host.Transport.MessageIds.Count(id => id == exactMessageId).ShouldBe(1);
        host.Transport.MessageIds.ShouldNotContain(oversizedMessageId);
        (await host.ReadFailureCodeAsync(exactMessageId)).ShouldBeNull();
        (await host.CountTerminalEventsAsync(exactMessageId)).ShouldBe(0L);
        (await host.ReadFailureCodeAsync(oversizedMessageId)).ShouldBe("payload_too_large");
        (await host.CountTerminalEventsAsync(oversizedMessageId)).ShouldBe(1L);
    }

    private sealed class PayloadBoundaryModule : IModule, IModuleOperationHandler, IModuleOperationResourcePolicy
    {
        public const string OperationKey = "boundary.publish";
        public const string EventKey = "boundary.published";

        public ModuleDescriptor Descriptor { get; } = new("boundary", "Boundary", "1.0.0",
            "Exercises the outbox event size limit.", [], [], ModuleCapabilities.Api | ModuleCapabilities.BackgroundWork, [])
        {
            Permissions = [new("boundary.write", "Write boundary events", "Publish a test event")],
            Operations = [new("boundary", OperationKey, 1, ModuleExecutionKind.UserCommand,
                "boundary.publish.v1", 1, ["boundary.write"], "boundary.policy", null, 1_048_576)],
            IntegrationEvents = [new(EventKey, 1, "boundary", typeof(PayloadBoundaryEvent),
                payload => payload.Deserialize<PayloadBoundaryEvent>(TestJsonOptions)!, 1_048_576)]
        };

        public static void AddToCatalog(IServiceCollection services)
        {
            ServiceDescriptor registration = services.Single(service => service.ServiceType == typeof(ModuleCatalog));
            ModuleCatalog installed = (ModuleCatalog)registration.ImplementationInstance!;
            services.Remove(registration);
            PayloadBoundaryModule boundary = new();
            services.AddSingleton(new ModuleCatalog([.. installed.Modules, boundary]));
            services.AddSingleton<IModuleOperationHandler>(boundary);
            services.AddSingleton<IModuleOperationResourcePolicy>(boundary);
        }

        public string ModuleId => Descriptor.Id;
        string IModuleOperationHandler.OperationKey => OperationKey;
        public int OperationVersion => 1;
        public string PolicyKey => "boundary.policy";

        // Dispatch validates the persisted source. Re-executing its command would be a regression.
        public Task HandleAsync(ModuleOperationContext context, JsonElement payload, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Dispatch re-executed the source command.");

        public Task<bool> AuthorizeAsync(ModuleOperationContext context, JsonElement payload, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public void Register(IServiceCollection services, IConfiguration configuration) { }
    }

    private sealed record PayloadBoundaryEvent(string Value);

    private sealed partial class RecoveryHost
    {
        public async Task InsertContextualAsync(Guid id, string eventKey, string payload, OutboxContext context)
        {
            await using NpgsqlConnection connection = new(ownerConnection);
            await connection.OpenAsync();
            await using NpgsqlCommand command = new("""
                INSERT INTO platform.outbox_messages
                  ("Id", "Type", "Payload", "OccurredAt", "Attempts", "ReplayGeneration",
                   "ContextVersion", "ContextJson", "PayloadHash")
                VALUES (@id, @type, @payload::jsonb, CURRENT_TIMESTAMP, 0, 0, 1, @context::jsonb, @hash)
                """, connection);
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("type", eventKey);
            command.Parameters.AddWithValue("payload", payload);
            command.Parameters.AddWithValue("context", JsonSerializer.Serialize(context, TestJsonOptions));
            command.Parameters.AddWithValue("hash", OutboxPayloadHash.Compute(payload));
            await command.ExecuteNonQueryAsync();
        }

        public async Task<string> RoundTripJsonbAsync(string payload)
        {
            await using NpgsqlConnection connection = new(ownerConnection);
            await connection.OpenAsync();
            await using NpgsqlCommand command = new("SELECT @payload::jsonb::text", connection);
            command.Parameters.AddWithValue("payload", payload);
            return (string)(await command.ExecuteScalarAsync())!;
        }

        public async Task<string> ReadServerVersionAsync()
        {
            await using NpgsqlConnection connection = new(ownerConnection);
            await connection.OpenAsync();
            await using NpgsqlCommand command = new("SELECT current_setting('server_version')", connection);
            return (string)(await command.ExecuteScalarAsync())!;
        }
    }
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Trykatch.Application.Organizations;
using Trykatch.Application.Outbox;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Modules;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class OutboxContextTests
{
    private static readonly Guid OrganizationId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    private static readonly Guid ActorId = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
    private static readonly Guid MembershipId = Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc");
    private const string Permission = "projects.write";
    private const string OperationKey = "projects.update";
    private const string EventKey = "projects.changed";

    [TestMethod]
    public void NewEnqueueCapturesTrustedScopeAndExplicitRegistrationSeparatelyFromPayload()
    {
        Fixture fixture = CreateFixture();
        UntrustedMessage payload = new(
            Guid.Parse("dddddddd-dddd-4ddd-8ddd-dddddddddddd"),
            Guid.Parse("eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee"),
            "changed");

        fixture.Writer.Enqueue(payload, OperationKey, 1);

        OutboxMessage stored = fixture.DbContext.OutboxMessages.Local.ShouldHaveSingleItem();
        stored.Type.ShouldBe(EventKey);
        stored.ContextVersion.ShouldBe(1);
        const string canonicalPayload = "{\"actorId\":\"eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee\",\"change\":\"changed\",\"organizationId\":\"dddddddd-dddd-4ddd-8ddd-dddddddddddd\"}";
        stored.PayloadHash.ShouldBe(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalPayload))));
        stored.PayloadHash!.Length.ShouldBe(64);

        using JsonDocument payloadDocument = JsonDocument.Parse(stored.Payload);
        payloadDocument.RootElement.GetProperty("organizationId").GetGuid().ShouldBe(payload.OrganizationId);
        payloadDocument.RootElement.GetProperty("actorId").GetGuid().ShouldBe(payload.ActorId);

        using JsonDocument contextDocument = JsonDocument.Parse(stored.ContextJson!);
        JsonElement context = contextDocument.RootElement;
        context.GetProperty("organizationId").GetGuid().ShouldBe(OrganizationId);
        context.GetProperty("effectiveActorId").GetGuid().ShouldBe(ActorId);
        context.GetProperty("originActorId").GetGuid().ShouldBe(ActorId);
        context.GetProperty("membershipId").GetGuid().ShouldBe(MembershipId);
        context.GetProperty("executionKind").GetString().ShouldBe("UserCommand");
        context.GetProperty("operationKey").GetString().ShouldBe(OperationKey);
        context.GetProperty("operationVersion").GetInt32().ShouldBe(1);
        context.GetProperty("eventKey").GetString().ShouldBe(EventKey);
        context.GetProperty("schemaVersion").GetInt32().ShouldBe(1);
        string catalogVersion = context.GetProperty("catalogVersion").GetString()!;
        catalogVersion!.Length.ShouldBe(64);
        catalogVersion.All(char.IsAsciiHexDigit).ShouldBeTrue();
    }

    [TestMethod]
    public async Task CanonicalHashSurvivesJsonbKeyOrderingAndWhitespaceNormalization()
    {
        Fixture fixture = CreateFixture();
        fixture.Writer.Enqueue(new UntrustedMessage(
            Guid.Parse("dddddddd-dddd-4ddd-8ddd-dddddddddddd"),
            Guid.Parse("eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee"), "changed"), OperationKey, 1);
        OutboxMessage stored = fixture.DbContext.OutboxMessages.Local.ShouldHaveSingleItem();
        OutboxMessage jsonbNormalized = new()
        {
            Id = stored.Id,
            Type = stored.Type,
            Payload = " { \"organizationId\" : \"dddddddd-dddd-4ddd-8ddd-dddddddddddd\", \"change\" : \"changed\", \"actorId\" : \"eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee\" } ",
            OccurredAt = stored.OccurredAt,
            ContextVersion = stored.ContextVersion,
            ContextJson = stored.ContextJson,
            PayloadHash = stored.PayloadHash
        };
        RecordingTransport transport = new();
        OutboxDelivery delivery = new(transport, TimeProvider.System, new OutboxWorkerState(), NullLogger<OutboxDelivery>.Instance);

        (await delivery.DeliverAsync(jsonbNormalized, TimeSpan.FromSeconds(1), CancellationToken.None)).ShouldBeTrue();

        transport.Envelopes.ShouldHaveSingleItem();
    }

    [TestMethod]
    public void NewEnqueueRequiresResolvedNonemptyMembershipAndOrganizationIdentity()
    {
        Fixture unresolved = CreateFixture(new TestOrganizationContext(isResolved: false));

        Should.Throw<InvalidOperationException>(() => unresolved.Writer.Enqueue(new UntrustedMessage(Guid.NewGuid(), Guid.NewGuid(), "x"), OperationKey, 1));
        unresolved.DbContext.OutboxMessages.Local.ShouldBeEmpty();

        OrganizationAccess noMembership = new(OrganizationId, "workspace", ActorId, Guid.Empty, new HashSet<string> { Permission });
        Fixture membershipless = CreateFixture(new TestOrganizationContext(noMembership));

        Should.Throw<InvalidOperationException>(() => membershipless.Writer.Enqueue(new UntrustedMessage(Guid.NewGuid(), Guid.NewGuid(), "x"), OperationKey, 1));
        membershipless.DbContext.OutboxMessages.Local.ShouldBeEmpty();

        OrganizationAccess noOrganization = new(Guid.Empty, "workspace", ActorId, MembershipId, new HashSet<string> { Permission });
        Fixture organizationless = CreateFixture(new TestOrganizationContext(noOrganization));

        Should.Throw<InvalidOperationException>(() => organizationless.Writer.Enqueue(new UntrustedMessage(Guid.NewGuid(), Guid.NewGuid(), "x"), OperationKey, 1));
        organizationless.DbContext.OutboxMessages.Local.ShouldBeEmpty();
    }

    [TestMethod]
    public void NewEnqueueRequiresCurrentPermissionAndMatchingRegisteredOperationAndEvent()
    {
        Fixture denied = CreateFixture(permissionGranted: false);
        Should.Throw<UnauthorizedAccessException>(() => denied.Writer.Enqueue(new UntrustedMessage(Guid.NewGuid(), Guid.NewGuid(), "x"), OperationKey, 1));
        denied.DbContext.OutboxMessages.Local.ShouldBeEmpty();

        Fixture unknownOperation = CreateFixture(includeOperation: false);
        Should.Throw<InvalidOperationException>(() => unknownOperation.Writer.Enqueue(new UntrustedMessage(Guid.NewGuid(), Guid.NewGuid(), "x"), OperationKey, 1));

        Fixture unknownEvent = CreateFixture(includeEvent: false);
        Should.Throw<InvalidOperationException>(() => unknownEvent.Writer.Enqueue(new UntrustedMessage(Guid.NewGuid(), Guid.NewGuid(), "x"), OperationKey, 1));

        Fixture wrongVersion = CreateFixture();
        Should.Throw<InvalidOperationException>(() => wrongVersion.Writer.Enqueue(new UntrustedMessage(Guid.NewGuid(), Guid.NewGuid(), "x"), OperationKey, 2));
    }

    [TestMethod]
    public async Task LegacyEnqueueKeepsCompatibilityNameAndDoesNotGuessContext()
    {
        Fixture fixture = CreateFixture();

        fixture.Writer.Enqueue(new Trykatch.Modules.Projects.IntegrationEvents.ProjectChanged(
            OrganizationId, Guid.NewGuid(), "updated", ActorId));

        OutboxMessage stored = fixture.DbContext.OutboxMessages.Local.ShouldHaveSingleItem();
        stored.Type.ShouldBe("TrykatchApp.Application.Projects.ProjectChanged");
        stored.ContextVersion.ShouldBeNull();
        stored.ContextJson.ShouldBeNull();
        stored.PayloadHash.ShouldBeNull();

        RecordingTransport transport = new();
        OutboxDelivery delivery = new(transport, TimeProvider.System, new OutboxWorkerState(), NullLogger<OutboxDelivery>.Instance);
        (await delivery.DeliverAsync(stored, TimeSpan.FromSeconds(1), CancellationToken.None)).ShouldBeTrue();
        transport.Envelopes.ShouldHaveSingleItem().Context.ShouldBeNull();
    }

    [TestMethod]
    [DataRow("unknown-context-version")]
    [DataRow("changed-payload-hash")]
    [DataRow("missing-context")]
    [DataRow("unsupported-kind")]
    [DataRow("empty-identity")]
    [DataRow("mismatched-event")]
    public async Task InvalidVersionedMetadataIsTerminalWithoutTransport(string invalidation)
    {
        Fixture fixture = CreateFixture();
        fixture.Writer.Enqueue(new UntrustedMessage(Guid.NewGuid(), Guid.NewGuid(), "x"), OperationKey, 1);
        OutboxMessage stored = fixture.DbContext.OutboxMessages.Local.ShouldHaveSingleItem();
        OutboxMessage invalid = new()
        {
            Id = stored.Id,
            Type = stored.Type,
            Payload = invalidation == "changed-payload-hash" ? stored.Payload.Replace("\"change\":\"x\"", "\"change\":\"tampered\"", StringComparison.Ordinal) : stored.Payload,
            OccurredAt = stored.OccurredAt,
            ContextVersion = invalidation == "unknown-context-version" ? 2 : stored.ContextVersion,
            ContextJson = invalidation switch
            {
                "missing-context" => null,
                "unsupported-kind" => stored.ContextJson!.Replace("UserCommand", "ScheduledSystem", StringComparison.Ordinal),
                "empty-identity" => stored.ContextJson!.Replace(ActorId.ToString(), Guid.Empty.ToString(), StringComparison.Ordinal),
                "mismatched-event" => stored.ContextJson!.Replace(EventKey, "projects.deleted", StringComparison.Ordinal),
                _ => stored.ContextJson
            },
            PayloadHash = stored.PayloadHash
        };

        RecordingTransport transport = new();
        OutboxDelivery delivery = new(transport, TimeProvider.System, new OutboxWorkerState(), NullLogger<OutboxDelivery>.Instance);

        (await delivery.DeliverAsync(invalid, TimeSpan.FromSeconds(1), CancellationToken.None)).ShouldBeFalse();

        transport.Envelopes.ShouldBeEmpty();
        invalid.ExhaustedAt.ShouldNotBeNull();
        invalid.LastErrorCode.ShouldBe("outbox_context_invalid");
        invalid.LastErrorType.ShouldBe("context_validation");
    }

    [TestMethod]
    public void NewEnqueueRejectsPayloadsAboveTheHostLimitBeforeTracking()
    {
        Fixture fixture = CreateFixture();

        Should.Throw<InvalidOperationException>(() => fixture.Writer.Enqueue(
            new UntrustedMessage(Guid.NewGuid(), Guid.NewGuid(), new string('x', 1_048_576)), OperationKey, 1));

        fixture.DbContext.OutboxMessages.Local.ShouldBeEmpty();
    }

    private static Fixture CreateFixture(
        TestOrganizationContext? organization = null,
        bool permissionGranted = true,
        bool includeOperation = true,
        bool includeEvent = true)
    {
        ModuleDescriptor descriptor = new("projects", "Projects", "1.0.0", "Project operations", [], [],
            ModuleCapabilities.Api | ModuleCapabilities.BackgroundWork, [])
        {
            Permissions = [new PermissionDescriptor(Permission, "Write projects", "Write project data")],
            Operations = includeOperation
                ? [new ModuleOperationDescriptor("projects", OperationKey, 1, ModuleExecutionKind.UserCommand,
                    "projects.update.payload", 1, [Permission], "projects.item", null, 1_048_576)]
                : [],
            IntegrationEvents = includeEvent
                ? [new IntegrationEventDescriptor(EventKey, 1, "projects", typeof(UntrustedMessage), element => element.Clone(), 1_048_576)]
                : []
        };
        ModuleCatalog catalog = new([new StubModule(descriptor)]);
        DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql("Host=localhost;Database=outbox_context_test;Username=test;Password=test")
            .Options;
        TestOrganizationContext organizationContext = organization ?? new TestOrganizationContext(permissionGranted: permissionGranted);
        ApplicationDbContext dbContext = new(options, [], organizationContext, catalog);
        OutboxWriter writer = new(dbContext, catalog, organizationContext);
        return new Fixture(dbContext, writer);
    }

    private sealed record Fixture(ApplicationDbContext DbContext, OutboxWriter Writer);
    private sealed record UntrustedMessage(Guid OrganizationId, Guid ActorId, string Change);

    private sealed class StubModule(ModuleDescriptor descriptor) : IModule
    {
        public ModuleDescriptor Descriptor { get; } = descriptor;
        public void Register(IServiceCollection services, IConfiguration configuration) { }
    }

    private sealed class TestOrganizationContext : IOrganizationContext
    {
        private readonly OrganizationAccess? access;

        public TestOrganizationContext(bool isResolved = true, bool permissionGranted = true)
        {
            if (isResolved)
                access = new OrganizationAccess(OutboxContextTests.OrganizationId, "workspace", OutboxContextTests.ActorId, OutboxContextTests.MembershipId,
                    permissionGranted ? new HashSet<string> { Permission } : new HashSet<string>());
        }

        public TestOrganizationContext(OrganizationAccess access) => this.access = access;
        public bool IsResolved => access is not null;
        public Guid OrganizationId => access?.OrganizationId ?? Guid.Empty;
        public string OrganizationSlug => access?.OrganizationSlug ?? string.Empty;
        public Guid ActorId => access?.ActorId ?? Guid.Empty;
        public Guid MembershipId => access?.MembershipId ?? Guid.Empty;
        public IReadOnlySet<string> Permissions => access?.Permissions ?? new HashSet<string>();
    }

    private sealed class RecordingTransport : IOutboxTransport
    {
        public List<OutboxEnvelope> Envelopes { get; } = [];
        public Task PublishAsync(OutboxEnvelope envelope, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Envelopes.Add(envelope);
            return Task.CompletedTask;
        }
    }
}

#if TRYKATCH_EMAIL
using System.Globalization;
using System.Data.Common;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Trykatch.Application;
using Trykatch.Application.Auditing;
using Trykatch.Application.Mailing;
using Trykatch.Application.Organizations;
using Trykatch.Infrastructure.Execution;
using Trykatch.Infrastructure;
using Trykatch.Infrastructure.Mailing;
using Trykatch.Infrastructure.Modules.Email;
using Trykatch.Infrastructure.Organizations;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Modules;

namespace Trykatch.IntegrationTests;

public sealed partial class ModuleWorkExecutionIntegrationTests
{
    private static readonly ModuleEmailTemplateRef ProbeEmailTemplate = new("probe", "probe.notice", 1);
    private static readonly ModuleEmailDisclosurePolicyRef ProbeEmailPolicy = new("probe", "probe.disclose", 1);

    [TestMethod]
    public async Task EmailAdmissionDeduplicatesAndProtectsPrivateIntentInsideTheBusinessTransaction()
    {
        SeededWork seed = await fixture.SeedUserWorkAsync("probe.command");
        ModuleEmailMessage message = EmailMessageFor(seed);
        await using AsyncServiceScope scope = fixture.EmailServices.CreateAsyncScope();
        await using IOrganizationDataScope transaction = await ExecutionFixture.BeginEmailScopeAsync(scope, seed);
        OrganizationModuleEmailQueue queue = scope.ServiceProvider.GetRequiredService<OrganizationModuleEmailQueue>();

        ModuleWorkReceipt accepted = await queue.EnqueueAsync(message);
        ModuleWorkReceipt duplicate = await queue.EnqueueAsync(message);
        ModuleWorkReceipt conflict = await queue.EnqueueAsync(message with { MessageId = Guid.NewGuid() });
        await transaction.CommitAsync(CancellationToken.None);
        ModuleWorkReceipt committedDuplicate = await fixture.SubmitEmailAsync(seed, message);

        accepted.State.ShouldBe(ModuleWorkState.Accepted);
        duplicate.State.ShouldBe(ModuleWorkState.AlreadyAccepted);
        duplicate.WorkId.ShouldBe(accepted.WorkId);
        committedDuplicate.WorkId.ShouldBe(accepted.WorkId);
        committedDuplicate.State.ShouldBe(ModuleWorkState.AlreadyAccepted);
        conflict.State.ShouldBe(ModuleWorkState.Rejected);
        conflict.WorkId.ShouldBe(Guid.Empty);
        (await fixture.CountEmailRowsAsync(seed.OrganizationId)).ShouldBe(1);
        OrganizationEmailDeliveryRecord row = (await fixture.EmailStore.FindAsync(seed.OrganizationId, accepted.WorkId, CancellationToken.None))!;
        row.ProtectedIntent.ShouldNotContain("private-input-body");
        row.ProtectedIntent.ShouldNotContain(seed.PayloadRecordId.ToString());
        row.MessageId.ShouldBe(message.LogicalDedupId);
        EmailDeliveryLease lease = (await fixture.EmailStore.ClaimAsync(1, CancellationToken.None)).Single();
        lease.Context.DeliveryId.ShouldBe(accepted.WorkId);
        await fixture.EmailStore.CompleteAsync(lease, new(EmailAttemptDisposition.Acknowledged), CancellationToken.None);
    }

    [TestMethod]
    public async Task EmailAdmissionAndBusinessWriteRollBackTogether()
    {
        SeededWork seed = await fixture.SeedUserWorkAsync("probe.command");
        long auditsBefore = await fixture.CountAuditIntentsAsync(seed.OrganizationId);
        await using (AsyncServiceScope scope = fixture.EmailServices.CreateAsyncScope())
        {
            await using IOrganizationDataScope transaction = await ExecutionFixture.BeginEmailScopeAsync(scope, seed);
            ApplicationDbContext application = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await OrganizationTransactionEnlistment.EnlistAsync(scope.ServiceProvider.GetRequiredService<OrganizationControlPlaneDbContext>(),
                application, seed.OrganizationId, seed.ActorId, CancellationToken.None);
            await application.Database.ExecuteSqlInterpolatedAsync($"UPDATE app.probe_records SET \"Marker\"={"email-transaction"} WHERE \"Id\"={seed.PayloadRecordId}");
            OrganizationControlPlaneDbContext control = scope.ServiceProvider.GetRequiredService<OrganizationControlPlaneDbContext>();
            control.AuditIntents.Add(AuditIntent.Create(Guid.CreateVersion7(), seed.OrganizationId, seed.ActorId, "email.fixture",
                new AuditTarget("probe", seed.PayloadRecordId.ToString(), "Email fixture"),
                new Dictionary<string, string?> { ["status"] = "queued" }, DateTimeOffset.UtcNow));
            await control.SaveChangesAsync();
            (await scope.ServiceProvider.GetRequiredService<OrganizationModuleEmailQueue>().EnqueueAsync(EmailMessageFor(seed)))
                .State.ShouldBe(ModuleWorkState.Accepted);
        }

        (await fixture.CountEmailRowsAsync(seed.OrganizationId)).ShouldBe(0);
        (await fixture.ReadProbeMarkerAsync(seed)).ShouldBe("seed");
        (await fixture.CountAuditIntentsAsync(seed.OrganizationId)).ShouldBe(auditsBefore);
    }

    [TestMethod]
    public async Task EmailRetryAfterLostOuterCommitAcknowledgementReturnsTheOriginalDelivery()
    {
        SeededWork seed = await fixture.SeedUserWorkAsync("probe.command");
        ModuleEmailMessage message = EmailMessageFor(seed);
        ModuleWorkReceipt original;
        EmailOuterCommitFault fault = fixture.EmailServices.GetRequiredService<EmailOuterCommitFault>();
        await using (AsyncServiceScope scope = fixture.EmailServices.CreateAsyncScope())
        {
            await using IOrganizationDataScope transaction = await ExecutionFixture.BeginEmailScopeAsync(scope, seed);
            original = await scope.ServiceProvider.GetRequiredService<OrganizationModuleEmailQueue>().EnqueueAsync(message);
            original.State.ShouldBe(ModuleWorkState.Accepted);
            fault.Arm();
            await Should.ThrowAsync<IOException>(() => transaction.CommitAsync(CancellationToken.None));
        }

        ModuleWorkReceipt recovered = await fixture.SubmitEmailAsync(seed, message);

        fault.Fired.ShouldBeTrue();
        recovered.State.ShouldBe(ModuleWorkState.AlreadyAccepted);
        recovered.WorkId.ShouldBe(original.WorkId);
        (await fixture.CountEmailRowsAsync(seed.OrganizationId)).ShouldBe(1);
        await fixture.CompleteEmailFixtureAsync(original.WorkId);
    }

    [TestMethod]
    public async Task EmailDuplicateReceiptRequiresCurrentMembershipBeforeReuse()
    {
        SeededWork seed = await fixture.SeedUserWorkAsync("probe.command");
        OrganizationAccess stale = await fixture.ResolveAccessAsync(seed.OrganizationId, seed.ActorId);
        ModuleEmailMessage message = EmailMessageFor(seed);
        (await fixture.SubmitEmailAsync(seed, message)).State.ShouldBe(ModuleWorkState.Accepted);
        await fixture.RevokeMembershipAsync(seed.OrganizationId, seed.ActorId);

        ModuleWorkReceipt receipt = await fixture.SubmitEmailAsync(seed, message, stale);

        receipt.State.ShouldBe(ModuleWorkState.Rejected);
        receipt.WorkId.ShouldBe(Guid.Empty);
        EmailDeliveryLease lease = (await fixture.EmailStore.ClaimAsync(1, CancellationToken.None)).Single();
        EmailDeliveryPreparation preparation = await fixture.EmailSource.PrepareAsync(lease, CancellationToken.None);
        preparation.Disposition.ShouldBe(EmailPreparationDisposition.Suppressed);
        await fixture.EmailSource.CompleteAsync(lease, new(EmailAttemptDisposition.Suppressed, preparation.FailureCode), CancellationToken.None);
    }

    [TestMethod]
    public async Task RevokedSourceAccessSuppressesQueuedEmail()
    {
        SeededWork seed = await fixture.SeedUserWorkAsync("probe.command");
        (await fixture.SubmitEmailAsync(seed, EmailMessageFor(seed))).State.ShouldBe(ModuleWorkState.Accepted);
        EmailDeliveryLease lease = (await fixture.EmailStore.ClaimAsync(1, CancellationToken.None)).Single();
        await fixture.SetEmailSourceDisclosureAsync(seed, false);

        EmailDeliveryPreparation prepared = await fixture.EmailSource.PrepareAsync(lease, CancellationToken.None);

        prepared.Disposition.ShouldBe(EmailPreparationDisposition.Suppressed);
        prepared.Message.ShouldBeNull();
        (await fixture.EmailSource.CompleteAsync(lease, new(EmailAttemptDisposition.Suppressed, prepared.FailureCode), CancellationToken.None)).ShouldBeTrue();
    }

    [TestMethod]
    public async Task EmailPreparationUsesCurrentCanonicalSourceDataAndRejectsTamperedLease()
    {
        SeededWork seed = await fixture.SeedUserWorkAsync("probe.command");
        (await fixture.SubmitEmailAsync(seed, EmailMessageFor(seed))).State.ShouldBe(ModuleWorkState.Accepted);
        EmailDeliveryLease lease = (await fixture.EmailStore.ClaimAsync(1, CancellationToken.None)).Single();
        await fixture.SetEmailSourceMarkerAsync(seed, "current-source");

        EmailDeliveryPreparation prepared = await fixture.EmailSource.PrepareAsync(lease, CancellationToken.None);
        EmailDeliveryPreparation tampered = await fixture.EmailSource.PrepareAsync(lease with
        {
            Context = lease.Context with { BoundaryOwnerId = Guid.NewGuid() }
        }, CancellationToken.None);

        prepared.Disposition.ShouldBe(EmailPreparationDisposition.Ready);
        prepared.Message.ShouldNotBeNull().TextBody.ShouldBe("current-source");
        prepared.Message!.TextBody.ShouldNotContain("private-input-body");
        tampered.Disposition.ShouldBe(EmailPreparationDisposition.Suppressed);
        await fixture.EmailSource.CompleteAsync(lease, new(EmailAttemptDisposition.Acknowledged), CancellationToken.None);
    }

    [TestMethod]
    public async Task ScheduledSystemEmailUsesFiniteHostGrantWithoutFabricatingMembership()
    {
        SeededWork seed = await fixture.SeedScheduledWorkAsync();
        ModuleWorkReceipt receipt = await fixture.SubmitSystemEmailAsync(seed, EmailMessageFor(seed));

        receipt.State.ShouldBe(ModuleWorkState.Accepted);
        EmailDeliveryLease lease = (await fixture.EmailStore.ClaimAsync(1, CancellationToken.None)).Single();
        (await fixture.EmailSource.PrepareAsync(lease, CancellationToken.None)).Disposition.ShouldBe(EmailPreparationDisposition.Ready);
        await fixture.EmailSource.CompleteAsync(lease, new(EmailAttemptDisposition.Acknowledged), CancellationToken.None);
    }

    [TestMethod]
    public async Task EmailCompetingAdmissionWaitsForCommittedReceipt()
    {
        SeededWork seed = await fixture.SeedUserWorkAsync("probe.command");
        ModuleEmailMessage message = EmailMessageFor(seed);
        await using AsyncServiceScope scope = fixture.EmailServices.CreateAsyncScope();
        await using IOrganizationDataScope transaction = await ExecutionFixture.BeginEmailScopeAsync(scope, seed);
        ModuleWorkReceipt first = await scope.ServiceProvider.GetRequiredService<OrganizationModuleEmailQueue>().EnqueueAsync(message);
        Task<ModuleWorkReceipt> competing = fixture.SubmitEmailAsync(seed, message);
        await fixture.WaitForEmailAdmissionWaiterAsync();
        await transaction.CommitAsync(CancellationToken.None);

        ModuleWorkReceipt second = await competing.WaitAsync(TimeSpan.FromSeconds(10));

        second.State.ShouldBe(ModuleWorkState.AlreadyAccepted);
        second.WorkId.ShouldBe(first.WorkId);
        (await fixture.CountEmailRowsAsync(seed.OrganizationId)).ShouldBe(1);
        await fixture.CompleteEmailFixtureAsync(first.WorkId);
    }

    [TestMethod]
    public async Task EmailSystemContextWithoutHostExecutionProvenanceIsRejectedEvenForAMember()
    {
        SeededWork seed = await fixture.SeedUserWorkAsync("probe.command");
        await using AsyncServiceScope scope = fixture.EmailServices.CreateAsyncScope();
        await using IOrganizationDataScope transaction = await scope.ServiceProvider.GetRequiredService<IOrganizationDataScopeFactory>()
            .BeginAsync(seed.OrganizationId, seed.ActorId, CancellationToken.None);
        scope.ServiceProvider.GetRequiredService<OrganizationContext>().InitializeSystem(new(seed.OrganizationId, "work", seed.ActorId,
            new HashSet<string>(StringComparer.Ordinal) { "probe.write" }));

        ModuleWorkReceipt receipt = await scope.ServiceProvider.GetRequiredService<OrganizationModuleEmailQueue>().EnqueueAsync(EmailMessageFor(seed));

        receipt.State.ShouldBe(ModuleWorkState.Rejected);
        receipt.WorkId.ShouldBe(Guid.Empty);
        (await fixture.CountEmailRowsAsync(seed.OrganizationId)).ShouldBe(0);
    }

    [TestMethod]
    public async Task CommittedEventEmailRevalidatesSourceWithoutRequiringOriginMembership()
    {
        SeededWork seed = await fixture.SeedCommittedEventWorkAsync();
        await fixture.RevokeMembershipAsync(seed.OrganizationId, seed.OriginActorId);

        ModuleWorkReceipt receipt = await fixture.SubmitSystemEmailAsync(seed, EmailMessageFor(seed));

        receipt.State.ShouldBe(ModuleWorkState.Accepted);
        EmailDeliveryLease lease = (await fixture.EmailStore.ClaimAsync(1, CancellationToken.None)).Single();
        (await fixture.EmailSource.PrepareAsync(lease, CancellationToken.None)).Disposition.ShouldBe(EmailPreparationDisposition.Ready);
        await fixture.EmailSource.CompleteAsync(lease, new(EmailAttemptDisposition.Acknowledged), CancellationToken.None);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SourcePolicyRecheckedBeforeSmtpRetry(bool revokeSource)
    {
        SeededWork seed = await fixture.SeedUserWorkAsync("probe.command");
        ModuleWorkReceipt receipt = await fixture.SubmitEmailAsync(seed, EmailMessageFor(seed));
        await using LoopbackSmtpServer rejected = new(recipientStatusCode: 450);
        await using LoopbackSmtpServer recovered = new();
        RecordingEmailTransport transport = new(SmtpFor(rejected), fixture.CountActiveEmailBusinessTransactionsAsync);
        using ServiceProvider dispatchServices = new ServiceCollection()
            .AddScoped<IEmailDeliverySource>(_ => fixture.EmailSource)
            .AddScoped<IEmailTransport>(_ => transport).BuildServiceProvider();
        QueuedEmailDispatcher dispatcher = new(dispatchServices.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System);

        await dispatcher.DispatchOnceAsync();
        OrganizationEmailDeliveryRecord retry = (await fixture.EmailStore.FindAsync(seed.OrganizationId, receipt.WorkId, CancellationToken.None))!;
        retry.State.ShouldBe("queued");
        retry.LastSmtpStatusCode.ShouldBe(450);
        retry.NextAttemptAtUtc.ShouldNotBeNull();
        rejected.ReceivedMessage.ShouldBeFalse();
        transport.Inner = SmtpFor(recovered);
        await fixture.SetEmailSourceMarkerAsync(seed, "current on retry");
        if (revokeSource) await fixture.SetEmailSourceDisclosureAsync(seed, false);
        await fixture.MakeEmailRetryDueAsync(receipt.WorkId);

        await dispatcher.DispatchOnceAsync();

        OrganizationEmailDeliveryRecord final = (await fixture.EmailStore.FindAsync(seed.OrganizationId, receipt.WorkId, CancellationToken.None))!;
        final.State.ShouldBe(revokeSource ? "suppressed" : "delivered");
        final.Attempts.ShouldBe(2);
        transport.Calls.ShouldBe(revokeSource ? 1 : 2);
        transport.ActiveBusinessTransactions.ShouldAllBe(count => count == 0);
        recovered.ReceivedMessage.ShouldBe(!revokeSource);
        if (!revokeSource)
        {
            recovered.MessageData.ShouldNotBeNull().ShouldContain("current on retry");
            recovered.MessageData!.ShouldNotContain("private-input-body");
        }
        else recovered.MessageData.ShouldBeNull();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task EmailPreparationRechecksCurrentInstalledTemplateAndGrant(bool removeModule)
    {
        SeededWork seed = await fixture.SeedScheduledWorkAsync();
        ModuleWorkReceipt receipt = await fixture.SubmitSystemEmailAsync(seed, EmailMessageFor(seed));
        receipt.State.ShouldBe(ModuleWorkState.Accepted);
        EmailDeliveryLease lease = (await fixture.EmailStore.ClaimAsync(1, CancellationToken.None)).Single();
        using ServiceProvider changed = fixture.EmailServicesWithChangedCatalog(removeModule);
        OrganizationEmailDeliverySource source = changed.GetRequiredService<OrganizationEmailDeliverySource>();

        EmailDeliveryPreparation preparation = await source.PrepareAsync(lease, CancellationToken.None);

        preparation.Disposition.ShouldBe(EmailPreparationDisposition.Suppressed);
        preparation.FailureCode.ShouldBe(removeModule ? EmailFailureCode.TemplateUnavailable : EmailFailureCode.SourceDenied);
        preparation.Message.ShouldBeNull();
        await source.CompleteAsync(lease, new(EmailAttemptDisposition.Suppressed, preparation.FailureCode), CancellationToken.None);
    }

    private static SmtpEmailTransport SmtpFor(LoopbackSmtpServer server) => new(Options.Create(new SmtpOptions
    {
        Host = "localhost", Port = server.Port, From = "fixture@example.test", Security = SmtpTransportSecurity.StartTls
    }), server.TrustedClientFactory());

    private static ModuleEmailMessage EmailMessageFor(SeededWork seed) => new(Guid.NewGuid(), Guid.NewGuid(),
        ProbeEmailTemplate, ProbeEmailPolicy, new("probe.records", seed.PayloadRecordId.ToString()),
        new(seed.OriginActorId.ToString()), JsonSerializer.SerializeToElement(new { title = "private-input-body" }), "email-fixture");

    private sealed partial class ExecutionFixture
    {
        public ServiceProvider EmailServices => services;
        public Task WaitForEmailAdmissionWaiterAsync() => WaitForSubmissionLockWaiterAsync();
        public Task MakeEmailRetryDueAsync(Guid deliveryId) => ExecuteOwnerAsync(
            "UPDATE infrastructure.email_deliveries SET \"NextAttemptAtUtc\"=now()-interval '1 second' WHERE \"Id\"=@id", ("id", deliveryId));
        public Task<long> CountActiveEmailBusinessTransactionsAsync() => CountOwnerAsync(
            "SELECT count(*) FROM pg_stat_activity WHERE datname=current_database() AND usename='trykatch_org_runtime' AND xact_start IS NOT NULL");
        public async Task CompleteEmailFixtureAsync(Guid deliveryId)
        {
            EmailDeliveryLease lease = (await EmailStore.ClaimAsync(1, CancellationToken.None)).Single();
            lease.Context.DeliveryId.ShouldBe(deliveryId);
            await EmailStore.CompleteAsync(lease, new(EmailAttemptDisposition.Acknowledged), CancellationToken.None);
        }

        public ServiceProvider EmailServicesWithChangedCatalog(bool removeModule)
        {
            IConfiguration configuration = services.GetRequiredService<IConfiguration>();
            IModule[] modules = services.GetRequiredService<ModuleCatalog>().Modules
                .Where(module => !removeModule || module.Descriptor.Id != "probe")
                .Select(module => module.Descriptor.Id == "probe"
                    ? new EmailDescriptorOverride(module, module.Descriptor with
                    {
                        SystemGrants = module.Descriptor.SystemGrants.Select(grant => grant with { GrantVersion = grant.GrantVersion + 1 }).ToArray()
                    }) : module).ToArray();
            ServiceCollection changed = new();
            changed.AddSingleton(configuration);
            changed.AddLogging();
            changed.AddApplication();
            changed.AddModules(configuration, modules);
            changed.AddInfrastructure(configuration);
            changed.AddSingleton(new ProbeExecutionBehavior());
            AddEmailProbeServices(changed, configuration, includeProbe: !removeModule);
            // The same host key provider decrypts records after catalog changes; no ephemeral key recreation is implied.
            changed.AddSingleton(services.GetRequiredService<IEmailPayloadProtector>());
            return changed.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        }
        public static Task<IOrganizationDataScope> BeginEmailScopeAsync(AsyncServiceScope scope, SeededWork seed) =>
            InitializeSubmissionScopeAsync(scope, seed, null);
        public Task SetEmailSourceDisclosureAsync(SeededWork seed, bool allowed) => ExecuteOwnerAsync(
            "UPDATE app.probe_records SET \"CanDisclose\"=@allowed WHERE \"Id\"=@id", ("allowed", allowed), ("id", seed.PayloadRecordId));
        public Task SetEmailSourceMarkerAsync(SeededWork seed, string marker) => ExecuteOwnerAsync(
            "UPDATE app.probe_records SET \"Marker\"=@marker WHERE \"Id\"=@id", ("marker", marker), ("id", seed.PayloadRecordId));
        public OrganizationEmailDeliveryStore EmailStore => services.GetRequiredService<OrganizationEmailDeliveryStore>();
        public OrganizationEmailDeliverySource EmailSource => services.GetRequiredService<OrganizationEmailDeliverySource>();

        private static void AddEmailProbeServices(ServiceCollection registrations, IConfiguration configuration, bool includeProbe = true)
        {
            registrations.AddSingleton<EmailOuterCommitFault>();
            registrations.AddDbContext<OrganizationControlPlaneDbContext>((provider, options) =>
                options.AddInterceptors(provider.GetRequiredService<EmailOuterCommitFault>()));
            registrations.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
            registrations.AddSingleton<IEmailPayloadProtector, DataProtectionEmailPayloadProtector>();
            registrations.AddSingleton(provider => new OrganizationEmailDeliveryStore(
                new DbContextOptionsBuilder<OrganizationEmailDeliveryDbContext>()
                    .UseNpgsql(configuration.GetConnectionString(RuntimeDatabaseConnectionContract.WorkCoordinator)!).Options,
                provider.GetRequiredService<TimeProvider>()));
            registrations.AddScoped<EmailTemplateRegistry>(provider => new(
                provider.GetRequiredService<ModuleCatalog>(), includeProbe ? [new(ProbeEmailTemplate, new ProbeEmailRenderer())] : [],
                includeProbe ? [new(ProbeEmailPolicy, new ProbeEmailDisclosure(provider.GetRequiredService<ApplicationDbContext>()))] : []));
            registrations.AddScoped<OrganizationModuleEmailPolicy>();
            registrations.AddScoped<OrganizationModuleEmailQueue>();
            registrations.AddSingleton<OrganizationEmailDeliverySource>();
        }

        public async Task<ModuleWorkReceipt> SubmitEmailAsync(SeededWork seed, ModuleEmailMessage message, OrganizationAccess? stale = null)
        {
            await using AsyncServiceScope scope = services.CreateAsyncScope();
            await using IOrganizationDataScope transaction = await InitializeSubmissionScopeAsync(scope, seed, stale);
            ModuleWorkReceipt receipt = await scope.ServiceProvider.GetRequiredService<OrganizationModuleEmailQueue>().EnqueueAsync(message);
            await transaction.CommitAsync(CancellationToken.None);
            return receipt;
        }

        public async Task<ModuleWorkReceipt> SubmitSystemEmailAsync(SeededWork seed, ModuleEmailMessage message)
        {
            await using AsyncServiceScope scope = services.CreateAsyncScope();
            await using IOrganizationDataScope transaction = await scope.ServiceProvider.GetRequiredService<IOrganizationDataScopeFactory>()
                .BeginAsync(seed.OrganizationId, seed.EffectiveActorId, CancellationToken.None);
            scope.ServiceProvider.GetRequiredService<OrganizationContext>().InitializeSystem(new(seed.OrganizationId, "work",
                seed.EffectiveActorId, new HashSet<string>(StringComparer.Ordinal)));
            ModuleOperationRegistry operations = scope.ServiceProvider.GetRequiredService<ModuleOperationRegistry>();
            operations.TryGet("probe", seed.OperationKey, 1, out ModuleOperationRegistration? registration).ShouldBeTrue();
            await using ModuleWorkDbContext persistedWork = new(new DbContextOptionsBuilder<ModuleWorkDbContext>()
                .UseNpgsql(services.GetRequiredService<IConfiguration>().GetConnectionString(RuntimeDatabaseConnectionContract.WorkCoordinator)!).Options);
            string json = await persistedWork.ModuleWorkItems.Where(row => row.Id == seed.WorkId).Select(row => row.ExecutionContextJson).SingleAsync();
            ModuleWorkEnvelope envelope = JsonSerializer.Deserialize<ModuleWorkEnvelope>(json, WebJson)!;
            scope.ServiceProvider.GetRequiredService<ModuleWorkExecutionContext>().Initialize(envelope.Identity, registration!.Descriptor);
            ModuleWorkReceipt receipt = await scope.ServiceProvider.GetRequiredService<OrganizationModuleEmailQueue>().EnqueueAsync(message);
            await transaction.CommitAsync(CancellationToken.None);
            return receipt;
        }

        public Task<long> CountEmailRowsAsync(Guid organizationId) => CountOwnerAsync(
            "SELECT count(*) FROM infrastructure.email_deliveries WHERE \"OrganizationId\"=@organization", ("organization", organizationId));
    }

    private sealed class EmailOuterCommitFault : DbTransactionInterceptor
    {
        private int armed;
        public bool Fired { get; private set; }
        public void Arm() { Fired = false; Interlocked.Exchange(ref armed, 1); }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context is OrganizationControlPlaneDbContext && Interlocked.Exchange(ref armed, 0) == 1)
            {
                Fired = true;
                throw new IOException("Fixture lost the acknowledgement after the organization transaction committed.");
            }
            return Task.CompletedTask;
        }
    }

    private sealed class EmailDescriptorOverride(IModule inner, ModuleDescriptor descriptor) : IModule
    {
        public ModuleDescriptor Descriptor => descriptor;
        public void Register(IServiceCollection registrations, IConfiguration configuration) => inner.Register(registrations, configuration);
    }

    private sealed class RecordingEmailTransport(IEmailTransport inner, Func<Task<long>> countTransactions) : IEmailTransport
    {
        public IEmailTransport Inner { get; set; } = inner;
        public int Calls { get; private set; }
        public List<long> ActiveBusinessTransactions { get; } = [];
        public async Task<EmailTransportResult> SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            Calls++;
            ActiveBusinessTransactions.Add(await countTransactions());
            return await Inner.SendAsync(message, cancellationToken);
        }
    }

    private sealed class ProbeEmailDisclosure(ApplicationDbContext application) : IModuleEmailDisclosurePolicy
    {
        public async Task<ModuleEmailDisclosureDecision> ResolveAsync(ModuleEmailDisclosureContext context,
            ModuleEmailMessage message, CancellationToken cancellationToken)
        {
            if (!Guid.TryParse(message.Source.ResourceId, out Guid sourceId)
                || !Guid.TryParse(message.Recipient.RecipientKey, out Guid recipientId))
                return new(ModuleEmailDisclosureState.Denied);
            ProbeRecord? source = await application.Set<ProbeRecord>().AsNoTracking()
                .SingleOrDefaultAsync(row => row.Id == sourceId && row.OrganizationId == context.Identity.OrganizationId, cancellationToken);
            if (source is null || !source.CanDisclose || source.RecipientActorId != recipientId)
                return new(ModuleEmailDisclosureState.Denied);
            return new(ModuleEmailDisclosureState.Allowed, recipientId.ToString("N", CultureInfo.InvariantCulture) + "@example.test",
                JsonSerializer.SerializeToElement(new { title = source.Marker }));
        }
    }

    private sealed class ProbeEmailRenderer : IModuleEmailTemplateRenderer
    {
        public JsonElement PreviewData => JsonSerializer.SerializeToElement(new { title = "Synthetic fixture" });
        public ModuleEmailContent Render(JsonElement canonicalData) => new("Probe email", canonicalData.GetProperty("title").GetString()!);
    }
}
#endif

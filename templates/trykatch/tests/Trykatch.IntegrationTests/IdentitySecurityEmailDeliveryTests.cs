#if TRYKATCH_EMAIL
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Testcontainers.PostgreSql;
using Trykatch.Application.Mailing;
using Trykatch.Identity;
using Trykatch.Infrastructure.Modules.Email;
using Trykatch.Modules;

namespace Trykatch.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
// Owner-backed disposable PostgreSQL fixtures exercise Identity and queue mechanics, not runtime-role grants or host composition.
public sealed class IdentitySecurityEmailDeliveryTests
{
    private const string TestPassword = "Local-only!Identity-Email-Password-42";

    [TestMethod]
    public async Task EnqueueProtectsIntentAndReturnsIdempotentOrConflictReceipt()
    {
        await using TestHost host = await TestHost.StartAsync();
        ApplicationUser user = await host.CreateUserAsync("reset-enqueue@trykatch.test");
        string token = await host.Users.GeneratePasswordResetTokenAsync(user);
        Guid deliveryId = Guid.NewGuid();
        PasswordResetEmailIntent intent = host.CreateIntent(user, deliveryId, token, displayName: "stale display name");

        host.Submission.IsConfigured.ShouldBeTrue();
        host.Submission.Boundary.ShouldBe(EmailDeliveryBoundary.Identity);
        ModuleWorkReceipt accepted = await host.Submission.EnqueuePasswordResetAsync(intent, CancellationToken.None);
        accepted.WorkId.ShouldBe(deliveryId);
        accepted.State.ShouldBe(ModuleWorkState.Accepted);

        IdentityEmailDeliveryRecord stored = (await host.Store.FindAsync(deliveryId, CancellationToken.None))!;
        stored.TemplateKey.ShouldBe("core.password-reset");
        stored.TemplateVersion.ShouldBe(1);
        stored.UserId.ShouldBe(user.Id);
        stored.ProtectedIntent.ShouldNotContain(token);
        stored.ProtectedIntent.ShouldNotContain(intent.Recipient);

        ModuleWorkReceipt duplicate = await host.Submission.EnqueuePasswordResetAsync(
            intent with { DisplayName = "different stale display name" }, CancellationToken.None);
        duplicate.State.ShouldBe(ModuleWorkState.AlreadyAccepted);

        ApplicationUser current = await host.Users.FindByIdAsync(user.Id.ToString()) ?? throw new InvalidOperationException();
        (await host.Users.UpdateSecurityStampAsync(current)).Succeeded.ShouldBeTrue();
        string replacementToken = await host.Users.GeneratePasswordResetTokenAsync(current);
        ModuleWorkReceipt conflict = await host.Submission.EnqueuePasswordResetAsync(
            host.CreateIntent(current, deliveryId, replacementToken), CancellationToken.None);
        conflict.State.ShouldBe(ModuleWorkState.Rejected);
        conflict.FailureCode.ShouldNotBeNull();
        conflict.FailureCode!.ShouldNotContain(replacementToken);
        (await host.Store.FindAsync(deliveryId, CancellationToken.None))!.ProtectedIntent.ShouldBe(stored.ProtectedIntent);
    }

    [TestMethod]
    public async Task PreparationUnprotectsTypedIntentAndUsesCurrentIdentityDisplayName()
    {
        await using TestHost host = await TestHost.StartAsync();
        ApplicationUser user = await host.CreateUserAsync("roundtrip@trykatch.test", displayName: "Current display name");
        string token = await host.Users.GeneratePasswordResetTokenAsync(user);
        PasswordResetEmailIntent intent = host.CreateIntent(user, Guid.NewGuid(), token, displayName: "Submitted display name");
        (await host.Submission.EnqueuePasswordResetAsync(intent, CancellationToken.None)).State.ShouldBe(ModuleWorkState.Accepted);

        EmailDeliveryLease lease = (await host.Submission.ClaimAsync(1, CancellationToken.None)).Single();
        EmailDeliveryPreparation prepared = await host.Submission.PrepareAsync(lease, CancellationToken.None);

        prepared.Disposition.ShouldBe(EmailPreparationDisposition.Ready);
        prepared.FailureCode.ShouldBe(EmailFailureCode.None);
        prepared.Message.ShouldNotBeNull();
        prepared.Message.Recipient.ShouldBe(user.Email);
        host.Renderer.CallCount.ShouldBe(1);
        host.Renderer.LastIntent.ShouldNotBeNull();
        host.Renderer.LastIntent.DisplayName.ShouldBe("Current display name");
        host.Renderer.LastIntent.Token.ShouldBe(token);
        host.Renderer.LastIntent.ResetUrl.ShouldBe(intent.ResetUrl);
    }

    [TestMethod]
    public async Task InvalidOriginsOrResetQueryAreRejectedWithoutReturningPrivateDetails()
    {
        await using TestHost host = await TestHost.StartAsync();
        ApplicationUser user = await host.CreateUserAsync("bad-link@trykatch.test");
        string token = await host.Users.GeneratePasswordResetTokenAsync(user);
        string valid = host.CreateIntent(user, Guid.NewGuid(), token).ResetUrl;
        string[] invalidUrls =
        [
            "https://attacker.trykatch.test/portal/reset-password?email=bad-link%40trykatch.test&token=" + TokenValue(token),
            valid + "&other=value",
            valid + "&email=bad-link%40trykatch.test",
            "https://user@reset.trykatch.test/portal/reset-password?email=bad-link%40trykatch.test&token=" + TokenValue(token),
            valid + "#fragment",
            "http://reset.trykatch.test/portal/reset-password?email=bad-link%40trykatch.test&token=" + TokenValue(token),
            "https://reset.trykatch.test/portal/reset-password?email=other%40trykatch.test&token=" + TokenValue(token),
            "https://reset.trykatch.test/portal/reset-password?email=bad-link%40trykatch.test&token=not-the-token"
        ];

        foreach (string resetUrl in invalidUrls)
        {
            PasswordResetEmailIntent intent = host.CreateIntent(user, Guid.NewGuid(), token) with { ResetUrl = resetUrl };
            ModuleWorkReceipt receipt = await host.Submission.EnqueuePasswordResetAsync(intent, CancellationToken.None);
            receipt.State.ShouldBe(ModuleWorkState.Rejected);
            receipt.FailureCode.ShouldNotBeNull();
            receipt.FailureCode!.ShouldNotContain(token);
            receipt.FailureCode.ShouldNotContain(user.Email!);
        }
    }

    [TestMethod]
    public async Task PreparationRechecksRecipientConfirmationSuspensionAndTokenUse()
    {
        await using TestHost host = await TestHost.StartAsync();

        ApplicationUser changedEmailUser = await host.CreateUserAsync("before-change@trykatch.test");
        EmailDeliveryLease changedEmailLease = await host.EnqueueAndClaimAsync(changedEmailUser);
        changedEmailUser.Email = "after-change@trykatch.test";
        changedEmailUser.UserName = changedEmailUser.Email;
        (await host.Users.UpdateAsync(changedEmailUser)).Succeeded.ShouldBeTrue();
        AssertSuppressed(await host.Submission.PrepareAsync(changedEmailLease, CancellationToken.None), EmailFailureCode.RecipientChanged);

        ApplicationUser unconfirmedUser = await host.CreateUserAsync("unconfirmed@trykatch.test");
        EmailDeliveryLease unconfirmedLease = await host.EnqueueAndClaimAsync(unconfirmedUser);
        unconfirmedUser.EmailConfirmed = false;
        (await host.Users.UpdateAsync(unconfirmedUser)).Succeeded.ShouldBeTrue();
        AssertSuppressed(await host.Submission.PrepareAsync(unconfirmedLease, CancellationToken.None), EmailFailureCode.RecipientChanged);

        ApplicationUser suspendedUser = await host.CreateUserAsync("suspended@trykatch.test");
        EmailDeliveryLease suspendedLease = await host.EnqueueAndClaimAsync(suspendedUser);
        suspendedUser.IsPlatformAccessSuspended = true;
        (await host.Users.UpdateAsync(suspendedUser)).Succeeded.ShouldBeTrue();
        AssertSuppressed(await host.Submission.PrepareAsync(suspendedLease, CancellationToken.None), EmailFailureCode.SourceDenied);

        ApplicationUser consumedUser = await host.CreateUserAsync("consumed@trykatch.test");
        EmailDeliveryLease consumedLease = await host.EnqueueAndClaimAsync(consumedUser);
        (await host.Submission.PrepareAsync(consumedLease, CancellationToken.None)).Disposition.ShouldBe(EmailPreparationDisposition.Ready);
        (await host.Users.ResetPasswordAsync(consumedUser, host.LastToken!, "New-local!Identity-Email-Password-84")).Succeeded.ShouldBeTrue();
        AssertSuppressed(await host.Submission.PrepareAsync(consumedLease, CancellationToken.None), EmailFailureCode.TokenInvalid);
    }

    [TestMethod]
    public async Task AdmissionAndPreparationReloadIdentityStateOutsideTheTrackedUserManager()
    {
        await using TestHost host = await TestHost.StartAsync();
        ApplicationUser user = await host.CreateUserAsync("database-current-state@trykatch.test");
        string token = await host.Users.GeneratePasswordResetTokenAsync(user);
        PasswordResetEmailIntent intent = host.CreateIntent(user, Guid.NewGuid(), token);

        await using (IdentityDbContext identity = new(host.IdentityOptions))
        {
            await identity.Users.Where(row => row.Id == user.Id)
                .ExecuteUpdateAsync(update => update.SetProperty(row => row.EmailConfirmed, false));
        }

        ModuleWorkReceipt unconfirmed = await host.Submission.EnqueuePasswordResetAsync(intent, CancellationToken.None);
        unconfirmed.State.ShouldBe(ModuleWorkState.Rejected);
        unconfirmed.FailureCode.ShouldBe("recipient_changed");

        await using (IdentityDbContext identity = new(host.IdentityOptions))
        {
            await identity.Users.Where(row => row.Id == user.Id)
                .ExecuteUpdateAsync(update => update.SetProperty(row => row.EmailConfirmed, true));
        }
        (await host.Submission.EnqueuePasswordResetAsync(intent, CancellationToken.None)).State.ShouldBe(ModuleWorkState.Accepted);
        EmailDeliveryLease lease = (await host.Submission.ClaimAsync(1, CancellationToken.None)).Single();

        await using (IdentityDbContext identity = new(host.IdentityOptions))
        {
            await identity.Users.Where(row => row.Id == user.Id).ExecuteUpdateAsync(update => update
                .SetProperty(row => row.Email, "changed-in-database@trykatch.test")
                .SetProperty(row => row.UserName, "changed-in-database@trykatch.test")
                .SetProperty(row => row.NormalizedEmail, "CHANGED-IN-DATABASE@TRYKATCH.TEST")
                .SetProperty(row => row.NormalizedUserName, "CHANGED-IN-DATABASE@TRYKATCH.TEST")
                .SetProperty(row => row.SecurityStamp, Guid.NewGuid().ToString()));
        }

        AssertSuppressed(await host.Submission.PrepareAsync(lease, CancellationToken.None), EmailFailureCode.RecipientChanged);
    }

    [TestMethod]
    public void DevelopmentFallbackAcceptsLoopbackOriginAndAnyPortOnly()
    {
        IdentityEmailLinkPolicy policy = new(new ConfigurationBuilder().Build(), isDevelopment: true);
        string token = "original-token";
        string encoded = TokenValue(token);
        string email = "loopback@trykatch.test";

        policy.IsAvailable.ShouldBeTrue();
        policy.IsValid($"http://127.0.0.1:5173/reset-password?email={Uri.EscapeDataString(email)}&token={encoded}", email, token).ShouldBeTrue();
        policy.IsValid($"http://localhost:4319/reset-password?email={Uri.EscapeDataString(email)}&token={encoded}", email, token).ShouldBeTrue();
        policy.IsValid($"http://example.test:5173/reset-password?email={Uri.EscapeDataString(email)}&token={encoded}", email, token).ShouldBeFalse();
        policy.IsValid($"ftp://127.0.0.1:5173/reset-password?email={Uri.EscapeDataString(email)}&token={encoded}", email, token).ShouldBeFalse();
    }

    [TestMethod]
    public async Task UnclassifiedRendererFailureIsNotConvertedIntoAResult()
    {
        await using TestHost host = await TestHost.StartAsync();
        ApplicationUser user = await host.CreateUserAsync("renderer-failure@trykatch.test");
        EmailDeliveryLease lease = await host.EnqueueAndClaimAsync(user);
        host.Renderer.Failure = new UnexpectedRendererException();

        await Should.ThrowAsync<UnexpectedRendererException>(() => host.Submission.PrepareAsync(lease, CancellationToken.None));
    }

    [TestMethod]
    public async Task PreparationRejectsContextTamperingStaleLeaseAndExpiredLease()
    {
        await using TestHost host = await TestHost.StartAsync();
        ApplicationUser user = await host.CreateUserAsync("tamper@trykatch.test");
        string token = await host.Users.GeneratePasswordResetTokenAsync(user);
        PasswordResetEmailIntent firstIntent = host.CreateIntent(user, Guid.NewGuid(), token);
        PasswordResetEmailIntent secondIntent = host.CreateIntent(user, Guid.NewGuid(), token);
        (await host.Submission.EnqueuePasswordResetAsync(firstIntent, CancellationToken.None)).State.ShouldBe(ModuleWorkState.Accepted);
        (await host.Submission.EnqueuePasswordResetAsync(secondIntent, CancellationToken.None)).State.ShouldBe(ModuleWorkState.Accepted);
        IReadOnlyList<EmailDeliveryLease> leases = await host.Submission.ClaimAsync(10, CancellationToken.None);
        EmailDeliveryLease firstLease = leases.Single(lease => lease.Context.DeliveryId == firstIntent.DeliveryId);
        EmailDeliveryLease secondLease = leases.Single(lease => lease.Context.DeliveryId == secondIntent.DeliveryId);

        EmailDeliveryLease staleLease = firstLease with { LeaseId = Guid.NewGuid() };
        AssertSuppressed(await host.Submission.PrepareAsync(staleLease, CancellationToken.None), EmailFailureCode.SourceDenied);

        await using (IdentityEmailDeliveryDbContext queue = new(host.QueueOptions))
        {
            IdentityEmailDeliveryRecord source = (await queue.EmailDeliveries.SingleAsync(row => row.Id == firstIntent.DeliveryId));
            IdentityEmailDeliveryRecord target = (await queue.EmailDeliveries.SingleAsync(row => row.Id == secondIntent.DeliveryId));
            target.ProtectedIntent = source.ProtectedIntent;
            await queue.SaveChangesAsync();
        }
        AssertSuppressed(await host.Submission.PrepareAsync(secondLease, CancellationToken.None), EmailFailureCode.PayloadInvalid);

        EmailDeliveryLease expiringLease = await host.EnqueueAndClaimAsync(await host.CreateUserAsync("expired-lease@trykatch.test"));
        host.Clock.Advance(EmailDeliveryPolicy.LeaseDuration.Add(TimeSpan.FromSeconds(1)));
        AssertSuppressed(await host.Submission.PrepareAsync(expiringLease, CancellationToken.None), EmailFailureCode.SourceDenied);
    }

    [TestMethod]
    public async Task ExpiredPasswordResetTokenIsSuppressedUsingConfiguredProviderLifetime()
    {
        await using TestHost host = await TestHost.StartAsync(tokenLifespan: TimeSpan.FromSeconds(1));
        ApplicationUser user = await host.CreateUserAsync("expired-token@trykatch.test");
        EmailDeliveryLease lease = await host.EnqueueAndClaimAsync(user);
        await Task.Delay(TimeSpan.FromSeconds(2));

        AssertSuppressed(await host.Submission.PrepareAsync(lease, CancellationToken.None), EmailFailureCode.TokenInvalid);
    }

    private static void AssertSuppressed(EmailDeliveryPreparation preparation, EmailFailureCode failureCode)
    {
        preparation.Disposition.ShouldBe(EmailPreparationDisposition.Suppressed);
        preparation.Message.ShouldBeNull();
        preparation.FailureCode.ShouldBe(failureCode);
    }

    private static string TokenValue(string token) => WebEncoders.Base64UrlEncode(System.Text.Encoding.UTF8.GetBytes(token));

    private sealed class CapturingRenderer : IPasswordResetEmailRenderer
    {
        public int CallCount { get; private set; }
        public PasswordResetEmailIntent? LastIntent { get; private set; }
        public Exception? Failure { get; set; }

        public EmailPreparedMessage Render(PasswordResetEmailIntent intent)
        {
            CallCount++;
            if (Failure is not null)
                throw Failure;
            LastIntent = intent;
            return new EmailPreparedMessage(intent.Recipient, "Reset password", "Use the reset link.");
        }
    }

    private sealed class UnexpectedRendererException : Exception { }

    private sealed class TestHost : IAsyncDisposable
    {
        private readonly PostgreSqlContainer _postgres;
        private readonly ServiceProvider _services;
        private readonly AsyncServiceScope _scope;
        private readonly string _publicUrl = "https://reset.trykatch.test/portal";

        public UserManager<ApplicationUser> Users { get; }
        public IdentityEmailDeliveryStore Store { get; }
        public DbContextOptions<IdentityEmailDeliveryDbContext> QueueOptions { get; }
        public DbContextOptions<IdentityDbContext> IdentityOptions { get; }
        public CapturingRenderer Renderer { get; }
        public IdentitySecurityEmailDelivery Submission { get; }
        public FakeTimeProvider Clock { get; }
        public string? LastToken { get; private set; }

        private TestHost(
            PostgreSqlContainer postgres,
            ServiceProvider services,
            AsyncServiceScope scope,
            DbContextOptions<IdentityDbContext> identityOptions,
            DbContextOptions<IdentityEmailDeliveryDbContext> queueOptions,
            UserManager<ApplicationUser> users,
            IdentityEmailDeliveryStore store,
            CapturingRenderer renderer,
            IdentitySecurityEmailDelivery submission,
            FakeTimeProvider clock)
        {
            _postgres = postgres;
            _services = services;
            _scope = scope;
            IdentityOptions = identityOptions;
            QueueOptions = queueOptions;
            Users = users;
            Store = store;
            Renderer = renderer;
            Submission = submission;
            Clock = clock;
        }

        public static async Task<TestHost> StartAsync(TimeSpan? tokenLifespan = null)
        {
            PostgreSqlContainer postgres = new PostgreSqlBuilder(
                "postgres:18.6-alpine3.23@sha256:697c180dbf244d3ce4a8f4cbc0156cde840af055c1bf8b76aebe422a4822086f").Build();
            await postgres.StartAsync();
            string connectionString = postgres.GetConnectionString();
            DbContextOptions<IdentityDbContext> identityOptions = new DbContextOptionsBuilder<IdentityDbContext>()
                .UseNpgsql(connectionString).Options;
            await using (IdentityDbContext identity = new(identityOptions))
                await identity.Database.MigrateAsync();

            DbContextOptions<IdentityEmailDeliveryDbContext> queueOptions = new DbContextOptionsBuilder<IdentityEmailDeliveryDbContext>()
                .UseNpgsql(connectionString).Options;
            ServiceCollection services = new();
            services.AddLogging();
            services.AddDataProtection().SetApplicationName("IdentitySecurityEmailDeliveryTests");
            services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
            services.Configure<DataProtectionTokenProviderOptions>(options =>
                options.TokenLifespan = tokenLifespan ?? TimeSpan.FromHours(1));
            services.AddDbContext<IdentityDbContext>(options => options.UseNpgsql(connectionString));
            services.AddIdentityCore<ApplicationUser>()
                .AddRoles<IdentityRole<Guid>>()
                .AddEntityFrameworkStores<IdentityDbContext>()
                .AddDefaultTokenProviders();
            ServiceProvider provider = services.BuildServiceProvider(validateScopes: true);
            AsyncServiceScope scope = provider.CreateAsyncScope();
            UserManager<ApplicationUser> users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            FakeTimeProvider clock = new(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));
            IdentityEmailDeliveryStore store = new(queueOptions, clock);
            CapturingRenderer renderer = new();
            IDataProtectionProvider protection = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>();
            IEmailPayloadProtector protector = new DataProtectionEmailPayloadProtector(protection);
            IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Application:PublicUrl"] = "https://reset.trykatch.test/portal"
            }).Build();
            IdentityEmailLinkPolicy linkPolicy = new(configuration, isDevelopment: false);
            IdentitySecurityEmailDelivery submission = new(store, users, protector, renderer, linkPolicy, clock);
            return new TestHost(postgres, provider, scope, identityOptions, queueOptions, users, store, renderer, submission, clock);
        }

        public async Task<ApplicationUser> CreateUserAsync(string email, string displayName = "Current user")
        {
            ApplicationUser user = new()
            {
                Id = Guid.NewGuid(),
                Email = email,
                UserName = email,
                EmailConfirmed = true,
                DisplayName = displayName
            };
            IdentityResult result = await Users.CreateAsync(user, TestPassword);
            result.Succeeded.ShouldBeTrue(string.Join("; ", result.Errors.Select(error => error.Description)));
            return user;
        }

        public PasswordResetEmailIntent CreateIntent(ApplicationUser user, Guid deliveryId, string token, string? displayName = null)
        {
            LastToken = token;
            string recipient = user.Email ?? throw new InvalidOperationException("Test user email was absent.");
            string encodedToken = TokenValue(token);
            string url = $"{_publicUrl}/reset-password?email={Uri.EscapeDataString(recipient)}&token={encodedToken}";
            return new PasswordResetEmailIntent(deliveryId, user.Id, recipient,
                displayName ?? user.DisplayName, token, url);
        }

        public async Task<EmailDeliveryLease> EnqueueAndClaimAsync(ApplicationUser user)
        {
            string token = await Users.GeneratePasswordResetTokenAsync(user);
            PasswordResetEmailIntent intent = CreateIntent(user, Guid.NewGuid(), token);
            (await Submission.EnqueuePasswordResetAsync(intent, CancellationToken.None)).State.ShouldBe(ModuleWorkState.Accepted);
            return (await Submission.ClaimAsync(1, CancellationToken.None)).Single();
        }

        public async ValueTask DisposeAsync()
        {
            await _scope.DisposeAsync();
            await _services.DisposeAsync();
            await _postgres.DisposeAsync();
        }
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount) => _now = _now.Add(amount);
    }
}
#endif

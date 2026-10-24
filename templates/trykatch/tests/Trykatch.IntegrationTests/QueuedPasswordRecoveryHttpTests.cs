using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Testcontainers.PostgreSql;
using Trykatch.Api.Controllers;
using Trykatch.Application.Identity;
using Trykatch.Application.Mailing;
using Trykatch.Identity;
using Trykatch.Modules;

namespace Trykatch.IntegrationTests;

public sealed partial class AuthenticationSecurityTests
{
#if TRYKATCH_EMAIL
    [TestMethod]
    public async Task ProductionPasswordRecoveryCommitsProtectedIntentUsingTheIdentityRole()
    {
        using IdentityCertificateFixture certificates = new();
        await using AccountSecurityStepUpTests.SecurityHost host = await AccountSecurityStepUpTests.SecurityHost.StartAsync();
        Dictionary<string, string?> settings = certificates.Settings();
        settings.Remove("ConnectionStrings:trykatch-identity");
        settings["DataProtection:Certificate:Path"] = certificates.Create("recovery-key-ring");
        settings["DataProtection:Certificate:Password"] = IdentityCertificateFixture.Password;
        settings["Application:PublicUrl"] = "https://app.trykatch.test";
        settings["Email:Host"] = "localhost";
        settings["Email:Port"] = "2525";
        settings["Email:From"] = "test@example.test";
        settings["Email:Security"] = "StartTls";
        await host.RestartAsync(settings, configureServices: services =>
        {
            // Isolate HTTP admission from the running poller; transport/retry behavior has separate qualification.
            ServiceDescriptor worker = services.Single(descriptor => descriptor.ServiceType == typeof(IHostedService)
                && descriptor.ImplementationType?.Name == "QueuedEmailDeliveryWorker");
            services.Remove(worker);
        });
        using HttpClient client = host.CreateClient();
        string antiforgery = await GetAntiforgeryTokenAsync(client);
        List<string> bodies = [];
        foreach (string email in new[] { AccountSecurityStepUpTests.SecurityHost.Email, "unknown-production@trykatch.test" })
        {
            using HttpResponseMessage response = await PostWithAntiforgeryAsync(client, "/api/v1/auth/password/forgot", antiforgery, new { email });
            response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
            bodies.Add(await response.Content.ReadAsStringAsync());
            ForgotPasswordResponse body = (await response.Content.ReadFromJsonAsync<ForgotPasswordResponse>())!;
            body.DeliveryConfigured.ShouldBeTrue();
            body.DevelopmentResetUrl.ShouldBeNull();
        }
        bodies[0].ShouldBe(bodies[1]);

        using IServiceScope scope = host.Services.CreateScope();
        UserManager<ApplicationUser> users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        ApplicationUser user = (await users.FindByEmailAsync(AccountSecurityStepUpTests.SecurityHost.Email))!;
        await using IdentityEmailDeliveryDbContext queue = new(new DbContextOptionsBuilder<IdentityEmailDeliveryDbContext>()
            .UseNpgsql(host.OwnerConnection).Options);
        IdentityEmailDeliveryRecord row = await queue.EmailDeliveries.AsNoTracking().SingleAsync(candidate => candidate.UserId == user.Id);
        row.State.ShouldBe("queued");
        row.Attempts.ShouldBe(0);
        row.ProtectedIntent.ShouldNotContain(user.Email!);
        row.ProtectedIntent.ShouldNotContain("reset-password");
    }
#endif

    [TestMethod]
    public async Task PasswordRecoveryQueuesTheOriginalTokenWithoutDisclosingAccountState()
    {
        await using PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:18.6-alpine3.23@sha256:697c180dbf244d3ce4a8f4cbc0156cde840af055c1bf8b76aebe422a4822086f").Build();
        await postgres.StartAsync();
        await ApplyMigrationsAsync(postgres.GetConnectionString());
        RecoverySubmission submission = new();
        LegacyRecoveryNotifier legacy = new();
        await using WebApplicationFactory<Program> factory = RecoveryFactory(postgres.GetConnectionString(), submission, legacy);
        ApplicationUser eligible = await CreateConfirmedUserAsync(factory, "queued-recovery@trykatch.test", AdministratorPassword);
        ApplicationUser unconfirmed = await CreateConfirmedUserAsync(factory, "unconfirmed-recovery@trykatch.test", AdministratorPassword);
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            UserManager<ApplicationUser> users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            ApplicationUser current = (await users.FindByIdAsync(unconfirmed.Id.ToString()))!;
            current.EmailConfirmed = false;
            (await users.UpdateAsync(current)).Succeeded.ShouldBeTrue();
        }

        using HttpClient client = CreateClient(factory);
        client.DefaultRequestHeaders.Add("Origin", "http://127.0.0.1:5173");
        string antiforgery = await GetAntiforgeryTokenAsync(client);
        List<ForgotPasswordResponse> responses = [];
        foreach (string email in new[] { "unknown-recovery@trykatch.test", unconfirmed.Email!, eligible.Email! })
        {
            using HttpResponseMessage response = await PostWithAntiforgeryAsync(client, "/api/v1/auth/password/forgot", antiforgery, new { email });
            response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
            string json = await response.Content.ReadAsStringAsync();
            json.ShouldNotContain("workId");
            json.ShouldNotContain("emailQueued");
            ForgotPasswordResponse body = (await response.Content.ReadFromJsonAsync<ForgotPasswordResponse>())!;
            body.DeliveryConfigured.ShouldBeTrue();
            responses.Add(body);
        }

        submission.Intents.Count.ShouldBe(1);
        legacy.Calls.ShouldBe(0);
        responses.Select(response => response.Message).Distinct().Count().ShouldBe(1);
        responses[0].DevelopmentResetUrl.ShouldBeNull();
        responses[1].DevelopmentResetUrl.ShouldBeNull();
        PasswordResetEmailIntent intent = submission.Intents.Single();
        intent.DeliveryId.ShouldNotBe(Guid.Empty);
        intent.UserId.ShouldBe(eligible.Id);
        intent.Recipient.ShouldBe(eligible.Email);
        intent.DisplayName.ShouldBe(eligible.DisplayName);
        intent.ResetUrl.ShouldBe(responses[2].DevelopmentResetUrl);
        Uri resetUrl = new(intent.ResetUrl);
        resetUrl.GetLeftPart(UriPartial.Authority).ShouldBe("http://127.0.0.1:5173");
        var query = QueryHelpers.ParseQuery(resetUrl.Query);
        query["email"].ToString().ShouldBe(intent.Recipient);
        Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(query["token"].ToString())).ShouldBe(intent.Token);
        using IServiceScope verificationScope = factory.Services.CreateScope();
        UserManager<ApplicationUser> manager = verificationScope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        ApplicationUser persisted = (await manager.FindByIdAsync(eligible.Id.ToString()))!;
        (await manager.VerifyUserTokenAsync(persisted, manager.Options.Tokens.PasswordResetTokenProvider,
            UserManager<ApplicationUser>.ResetPasswordTokenPurpose, intent.Token)).ShouldBeTrue();
    }

    [TestMethod]
    [DataRow(ModuleWorkState.Unavailable, false)]
    [DataRow(ModuleWorkState.Rejected, true)]
    public async Task PasswordRecoveryKeepsConfigurationGlobalWhenQueueDeclines(ModuleWorkState state, bool configured)
    {
        await using PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:18.6-alpine3.23@sha256:697c180dbf244d3ce4a8f4cbc0156cde840af055c1bf8b76aebe422a4822086f").Build();
        await postgres.StartAsync();
        await ApplyMigrationsAsync(postgres.GetConnectionString());
        RecoverySubmission submission = new() { State = state, IsConfigured = configured };
        LegacyRecoveryNotifier legacy = new();
        await using WebApplicationFactory<Program> factory = RecoveryFactory(postgres.GetConnectionString(), submission, legacy);
        ApplicationUser user = await CreateConfirmedUserAsync(factory, "declined-recovery@trykatch.test", AdministratorPassword);
        using HttpClient client = CreateClient(factory);
        string antiforgery = await GetAntiforgeryTokenAsync(client);
        List<ForgotPasswordResponse> responses = [];
        foreach (string email in new[] { user.Email!, "unknown@trykatch.test" })
        {
            using HttpResponseMessage response = await PostWithAntiforgeryAsync(client, "/api/v1/auth/password/forgot", antiforgery, new { email });
            response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
            string json = await response.Content.ReadAsStringAsync();
            json.ShouldNotContain("planted-private-receipt");
            json.ShouldNotContain("emailQueued");
            ForgotPasswordResponse body = (await response.Content.ReadFromJsonAsync<ForgotPasswordResponse>())!;
            body.DeliveryConfigured.ShouldBe(configured);
            responses.Add(body);
        }
        responses[0].Message.ShouldBe(responses[1].Message);
        responses[0].DevelopmentResetUrl.ShouldNotBeNull();
        responses[1].DevelopmentResetUrl.ShouldBeNull();
        submission.Intents.Count.ShouldBe(1);
        legacy.Calls.ShouldBe(0);
    }

    [TestMethod]
    public async Task PasswordRecoveryFailureDoesNotLogPrivateIntentOrException()
    {
        await using PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:18.6-alpine3.23@sha256:697c180dbf244d3ce4a8f4cbc0156cde840af055c1bf8b76aebe422a4822086f").Build();
        await postgres.StartAsync();
        await ApplyMigrationsAsync(postgres.GetConnectionString());
        RecoverySubmission submission = new() { ThrowPrivateFailure = true };
        LegacyRecoveryNotifier legacy = new();
        RecoveryLog log = new();
        await using WebApplicationFactory<Program> factory = RecoveryFactory(postgres.GetConnectionString(), submission, legacy, log);
        ApplicationUser user = await CreateConfirmedUserAsync(factory, "private-recovery@trykatch.test", AdministratorPassword);
        using HttpClient client = CreateClient(factory);
        string antiforgery = await GetAntiforgeryTokenAsync(client);
        using HttpResponseMessage response = await PostWithAntiforgeryAsync(client, "/api/v1/auth/password/forgot", antiforgery, new { email = user.Email });
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await response.Content.ReadFromJsonAsync<ForgotPasswordResponse>())!.DeliveryConfigured.ShouldBeTrue();
        submission.Intents.Count.ShouldBe(1);
        legacy.Calls.ShouldBe(0);
        log.Events.Count.ShouldBe(1);
        var entry = log.Events.Single();
        entry.Exception.ShouldBeNull();
        foreach (string privateValue in new[] { user.Email!, submission.Intents.Single().Token, submission.Intents.Single().ResetUrl, "planted-private-exception" })
            entry.Message.ShouldNotContain(privateValue);
    }

    private static WebApplicationFactory<Program> RecoveryFactory(
        string connection, RecoverySubmission submission, LegacyRecoveryNotifier legacy, RecoveryLog? log = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseEnvironment("Development");
            host.UseSetting("ConnectionStrings:trykatchdb", connection);
            string workCoordinator = new NpgsqlConnectionStringBuilder(connection)
            {
                Username = PostgresRuntimeRoleFixture.WorkCoordinatorRole,
                Password = PostgresRuntimeRoleFixture.WorkCoordinatorPassword
            }.ConnectionString;
            host.UseSetting("ConnectionStrings:trykatch-work", workCoordinator);
            host.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:trykatch-work"] = workCoordinator
                }));
            host.ConfigureTestServices(services =>
            {
                services.RemoveAll<IIdentitySecurityEmailSubmission>();
                services.AddSingleton<IIdentitySecurityEmailSubmission>(submission);
                services.RemoveAll<IAccountRecoveryNotifier>();
                services.AddSingleton<IAccountRecoveryNotifier>(legacy);
                if (log is not null) services.AddSingleton<ILogger<AuthenticationController>>(log);
            });
        });

    private sealed class RecoverySubmission : IIdentitySecurityEmailSubmission
    {
        public bool IsConfigured { get; init; } = true;
        public ModuleWorkState State { get; init; } = ModuleWorkState.Accepted;
        public bool ThrowPrivateFailure { get; init; }
        public List<PasswordResetEmailIntent> Intents { get; } = [];

        public Task<ModuleWorkReceipt> EnqueuePasswordResetAsync(PasswordResetEmailIntent intent, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Intents.Add(intent);
            if (ThrowPrivateFailure)
            {
                InvalidOperationException exception = new($"planted-private-exception {intent.Recipient} {intent.Token} {intent.ResetUrl}",
                    new IOException("planted-private-exception"));
                exception.Data["private-intent"] = intent;
                throw exception;
            }
            return Task.FromResult(new ModuleWorkReceipt(intent.DeliveryId, State, FailureCode: "planted-private-receipt"));
        }
    }

    private sealed class LegacyRecoveryNotifier : IAccountRecoveryNotifier
    {
        public bool IsConfigured => true;
        public int Calls { get; private set; }
        public Task SendPasswordResetAsync(string recipient, string displayName, string resetUrl, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }

    private sealed class RecoveryLog : ILogger<AuthenticationController>
    {
        public ConcurrentQueue<(string Message, Exception? Exception)> Events { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Events.Enqueue((formatter(state, exception), exception));
    }
}

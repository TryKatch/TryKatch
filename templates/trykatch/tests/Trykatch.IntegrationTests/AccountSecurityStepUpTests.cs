using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Testcontainers.PostgreSql;
using Trykatch.Identity;
using Trykatch.Infrastructure.Modules;
using Trykatch.Infrastructure.Organizations;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Modules;
using Trykatch.Modules.Documents.Infrastructure;

namespace Trykatch.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class AccountSecurityStepUpTests
{
    [TestMethod]
    public async Task AuthenticatedCookieAloneCannotStartEnrollment()
    {
        await using SecurityHost host = await SecurityHost.StartAsync();
        using HttpClient client = await host.SignInAsync();

        using HttpResponseMessage response = await PostAsync(client, "/mfa/setup", new { });

        await AssertProblemAsync(response, HttpStatusCode.Forbidden, "reauthentication_required");
    }

    [TestMethod]
    public async Task VerifiedEnrollmentRequiresNewFactorAndEndsSession()
    {
        await using SecurityHost host = await SecurityHost.StartAsync();
        using HttpClient client = await host.SignInAsync();
        JsonElement setup = await StartEnrollmentAsync(client);
        (await client.GetFromJsonAsync<JsonElement>("/api/v1/account")).GetProperty("twoFactorEnabled").GetBoolean().ShouldBeFalse();
        using HttpResponseMessage wrong = await PostAsync(client, "/mfa/enable", new { enrollmentId = setup.GetProperty("enrollmentId"), code = "invalid" });
        await AssertProblemAsync(wrong, HttpStatusCode.Forbidden, "reauthentication_failed");

        JsonElement recovery = await SuccessAsync(client, "/mfa/enable", new
        {
            enrollmentId = setup.GetProperty("enrollmentId"),
            code = Totp(setup.GetProperty("sharedKey").GetString()!, host.Clock.GetUtcNow())
        });

        recovery.GetProperty("codes").GetArrayLength().ShouldBe(10);
        recovery.GetProperty("signInRequired").GetBoolean().ShouldBeTrue();
        (await client.GetAsync("/api/v1/auth/session")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        using HttpClient newSession = await host.SignInWithMfaAsync(Totp(setup.GetProperty("sharedKey").GetString()!));
        (await newSession.GetFromJsonAsync<JsonElement>("/api/v1/account")).GetProperty("twoFactorEnabled").GetBoolean().ShouldBeTrue();
    }

    private static async Task<string> GrantAsync(HttpClient client, string purpose = "mfa.enroll", string? code = null, bool isRecoveryCode = false)
    {
        JsonElement result = await SuccessAsync(client, "/reauthenticate", new { purpose, password = SecurityHost.Password, code, isRecoveryCode });
        return result.GetProperty("grant").GetString()!;
    }

    [TestMethod]
    [DataRow("expiry")]
    [DataRow("replay")]
    [DataRow("session")]
    [DataRow("purpose")]
    public async Task GrantsRejectExpiryReplayDifferentSessionsAndPurposes(string condition)
    {
        await using SecurityHost host = await SecurityHost.StartAsync();
        using HttpClient first = await host.SignInAsync();
        using HttpClient second = await host.SignInAsync();
        string grant = await GrantAsync(first, condition == "purpose" ? "mfa.disable" : "mfa.enroll");
        if (condition == "expiry") host.Clock.Advance(TimeSpan.FromMinutes(5));
        if (condition == "replay") await SuccessAsync(first, "/mfa/setup", new { grant });

        using HttpResponseMessage rejected = await PostAsync(condition == "session" ? second : first, "/mfa/setup", new { grant });

        await AssertProblemAsync(rejected, HttpStatusCode.Forbidden, condition == "expiry" ? "grant_expired" : "reauthentication_required");
        if (condition == "session") await SuccessAsync(first, "/mfa/setup", new { grant });
    }

    [TestMethod]
    public async Task OneGrantCannotBeConsumedConcurrently()
    {
        await using SecurityHost host = await SecurityHost.StartAsync();
        using HttpClient client = await host.SignInAsync();
        string grant = await GrantAsync(client);

        HttpResponseMessage[] responses = await Task.WhenAll(PostAsync(client, "/mfa/setup", new { grant }), PostAsync(client, "/mfa/setup", new { grant }));

        try
        {
            responses.Count(response => response.StatusCode == HttpStatusCode.OK).ShouldBe(1);
            await AssertProblemAsync(responses.Single(response => response.StatusCode != HttpStatusCode.OK), HttpStatusCode.Forbidden, "reauthentication_required");
        }
        finally { foreach (HttpResponseMessage response in responses) response.Dispose(); }
    }

    [TestMethod]
    [DataRow("password")]
    [DataRow("factor")]
    [DataRow("missing-factor")]
    public async Task EnrolledAccountsRequirePasswordAndCurrentFactor(string incorrect)
    {
        await using SecurityHost host = await SecurityHost.StartAsync();
        var enrollment = await EnrollAsync(host);
        using HttpClient client = await host.SignInWithMfaAsync(Totp(enrollment.Key));

        using HttpResponseMessage rejected = await PostAsync(client, "/reauthenticate", new
        {
            purpose = "mfa.replace",
            password = incorrect == "password" ? "Wrong!Password42" : SecurityHost.Password,
            code = incorrect == "factor" ? "invalid" : incorrect == "missing-factor" ? null : Totp(enrollment.Key)
        });

        await AssertProblemAsync(rejected, HttpStatusCode.Forbidden, "reauthentication_failed");
        foreach (string path in new[] { "/mfa/setup", "/mfa/recovery-codes", "/mfa/disable" })
        {
            using HttpResponseMessage cookieOnly = await PostAsync(client, path, new { });
            await AssertProblemAsync(cookieOnly, HttpStatusCode.Forbidden, "reauthentication_required");
        }
    }

    [TestMethod]
    public async Task RecoveryFactorIsConsumedOnceAndDoesNotReplaceThePassword()
    {
        await using SecurityHost host = await SecurityHost.StartAsync();
        var enrollment = await EnrollAsync(host);
        using HttpClient client = await host.SignInWithMfaAsync(Totp(enrollment.Key));
        string recovery = enrollment.Codes[0];
        using HttpResponseMessage wrongPassword = await PostAsync(client, "/reauthenticate", new
        {
            purpose = "mfa.replace",
            password = "Wrong!Password42",
            code = recovery,
            isRecoveryCode = true
        });
        await AssertProblemAsync(wrongPassword, HttpStatusCode.Forbidden, "reauthentication_failed");
        string grant = await GrantAsync(client, "mfa.replace", recovery, true);
        using HttpResponseMessage replay = await PostAsync(client, "/reauthenticate", new
        {
            purpose = "mfa.replace",
            password = SecurityHost.Password,
            code = recovery,
            isRecoveryCode = true
        });
        await AssertProblemAsync(replay, HttpStatusCode.Forbidden, "reauthentication_failed");
        await SuccessAsync(client, "/mfa/setup", new { grant });
    }

    [TestMethod]
    [DataRow("cancel")]
    [DataRow("expire")]
    public async Task AbandonedReplacementPreservesOldFactorAndRecoveryCodes(string operation)
    {
        await using SecurityHost host = await SecurityHost.StartAsync();
        var enrollment = await EnrollAsync(host);
        using HttpClient client = await host.SignInWithMfaAsync(Totp(enrollment.Key));
        JsonElement replacement = await StartEnrollmentAsync(client, "mfa.replace", Totp(enrollment.Key));
        replacement.GetProperty("sharedKey").GetString().ShouldNotBe(enrollment.Key);
        if (operation == "cancel") await SuccessAsync(client, "/mfa/cancel", new { enrollmentId = replacement.GetProperty("enrollmentId") });
        else host.Clock.Advance(TimeSpan.FromMinutes(10));

        using HttpResponseMessage rejected = await PostAsync(client, "/mfa/enable", new
        {
            enrollmentId = replacement.GetProperty("enrollmentId"),
            code = Totp(replacement.GetProperty("sharedKey").GetString()!, host.Clock.GetUtcNow())
        });
        await AssertProblemAsync(rejected, operation == "cancel" ? HttpStatusCode.Conflict : HttpStatusCode.Forbidden,
            operation == "cancel" ? "enrollment_conflict" : "grant_expired");
        using HttpClient oldFactor = await host.SignInWithMfaAsync(Totp(enrollment.Key));
        using HttpClient oldRecovery = await host.SignInWithMfaAsync(enrollment.Codes[0], true);
    }

    [TestMethod]
    public async Task ConfirmedReplacementInvalidatesOldFactorAndRecoveryCodes()
    {
        await using SecurityHost host = await SecurityHost.StartAsync();
        var enrollment = await EnrollAsync(host);
        using HttpClient client = await host.SignInWithMfaAsync(Totp(enrollment.Key));
        JsonElement replacement = await StartEnrollmentAsync(client, "mfa.replace", Totp(enrollment.Key));
        string replacementKey = replacement.GetProperty("sharedKey").GetString()!;
        JsonElement codes = await SuccessAsync(client, "/mfa/enable", new
        {
            enrollmentId = replacement.GetProperty("enrollmentId"),
            code = Totp(replacementKey, host.Clock.GetUtcNow())
        });
        using HttpClient newSession = await host.SignInWithMfaAsync(Totp(replacementKey));
        foreach ((string code, bool isRecoveryCode) in new[] { (Totp(enrollment.Key), false), (enrollment.Codes[0], true) })
        {
            using HttpClient oldSession = host.CreateClient();
            using HttpResponseMessage password = await PostAsync(oldSession, "/api/v1/auth/login", new { email = SecurityHost.Email, password = SecurityHost.Password, rememberMe = false });
            password.StatusCode.ShouldBe((HttpStatusCode)428);
            using HttpResponseMessage rejected = await PostAsync(oldSession, "/api/v1/auth/login/mfa", new { code, isRecoveryCode, rememberMe = false, rememberClient = false });
            rejected.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }
        using HttpClient newRecovery = await host.SignInWithMfaAsync(codes.GetProperty("codes")[0].GetString()!, true);
    }

    private static async Task<(string Key, string[] Codes)> EnrollAsync(SecurityHost host)
    {
        using HttpClient client = await host.SignInAsync();
        JsonElement setup = await StartEnrollmentAsync(client);
        string key = setup.GetProperty("sharedKey").GetString()!;
        JsonElement result = await SuccessAsync(client, "/mfa/enable", new { enrollmentId = setup.GetProperty("enrollmentId"), code = Totp(key, host.Clock.GetUtcNow()) });
        return (key, result.GetProperty("codes").EnumerateArray().Select(value => value.GetString()!).ToArray());
    }

    [TestMethod]
    public async Task ExistingAuthenticatorAndRecoveryCodesSurviveTheMigrationWithoutRedisclosure()
    {
        await using SecurityHost host = await SecurityHost.StartAsync(legacyMfa: true);
        using HttpClient client = await host.SignInWithMfaAsync(Totp(SecurityHost.LegacyKey));
        using HttpResponseMessage cookieOnly = await PostAsync(client, "/mfa/setup", new { });
        await AssertProblemAsync(cookieOnly, HttpStatusCode.Forbidden, "reauthentication_required");
        JsonElement pending = await StartEnrollmentAsync(client, "mfa.replace", Totp(SecurityHost.LegacyKey));
        pending.GetProperty("sharedKey").GetString().ShouldNotBe(SecurityHost.LegacyKey);
        using HttpClient oldAuthenticator = await host.SignInWithMfaAsync(Totp(SecurityHost.LegacyKey));
        using HttpClient oldRecovery = await host.SignInWithMfaAsync(SecurityHost.LegacyRecoveryCode, true);
    }

    [TestMethod]
    public async Task PendingEnrollmentCannotBeConfirmedFromAnotherSessionOrConfirmedTwice()
    {
        await using SecurityHost host = await SecurityHost.StartAsync();
        using HttpClient first = await host.SignInAsync();
        using HttpClient second = await host.SignInAsync();
        JsonElement setup = await StartEnrollmentAsync(first);
        string key = setup.GetProperty("sharedKey").GetString()!;
        var request = new { enrollmentId = setup.GetProperty("enrollmentId"), code = Totp(key, host.Clock.GetUtcNow()) };
        using HttpResponseMessage wrongSession = await PostAsync(second, "/mfa/enable", request);
        await AssertProblemAsync(wrongSession, HttpStatusCode.Conflict, "enrollment_conflict");
        await SuccessAsync(first, "/mfa/enable", request);
        using HttpClient refreshed = await host.SignInWithMfaAsync(Totp(key));
        using HttpResponseMessage replay = await PostAsync(refreshed, "/mfa/enable", request);
        await AssertProblemAsync(replay, HttpStatusCode.Conflict, "enrollment_conflict");
        using HttpResponseMessage staleCookie = await PostAsync(second, "/reauthenticate", new
        {
            purpose = "mfa.replace",
            password = SecurityHost.Password,
            code = Totp(key)
        });
        await AssertProblemAsync(staleCookie, HttpStatusCode.Forbidden, "reauthentication_required");
    }

    [TestMethod]
    public async Task ConcurrentConfirmationReturnsRecoveryCodesOnlyOnce()
    {
        await using SecurityHost host = await SecurityHost.StartAsync();
        using HttpClient client = await host.SignInAsync();
        JsonElement setup = await StartEnrollmentAsync(client);
        var request = new { enrollmentId = setup.GetProperty("enrollmentId"), code = Totp(setup.GetProperty("sharedKey").GetString()!, host.Clock.GetUtcNow()) };
        HttpResponseMessage[] results = await Task.WhenAll(PostAsync(client, "/mfa/enable", request), PostAsync(client, "/mfa/enable", request));
        try
        {
            results.Count(response => response.StatusCode == HttpStatusCode.OK).ShouldBe(1);
            HttpResponseMessage denied = results.Single(response => response.StatusCode != HttpStatusCode.OK);
            denied.StatusCode.ShouldBeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized);
        }
        finally { foreach (HttpResponseMessage result in results) result.Dispose(); }
    }

    [TestMethod]
    public async Task RecoveryCodeRegenerationReplacesCodesAndSignsOutCurrentSession()
    {
        await using SecurityHost host = await SecurityHost.StartAsync();
        var enrollment = await EnrollAsync(host);
        using HttpClient client = await host.SignInWithMfaAsync(Totp(enrollment.Key));
        JsonElement replacement = await SuccessAsync(client, "/mfa/recovery-codes", new
        {
            grant = await GrantAsync(client, "mfa.recovery-codes", Totp(enrollment.Key))
        });
        (await client.GetAsync("/api/v1/auth/session")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        using HttpClient oldCodeClient = host.CreateClient();
        using HttpResponseMessage password = await PostAsync(oldCodeClient, "/api/v1/auth/login", new { email = SecurityHost.Email, password = SecurityHost.Password, rememberMe = false });
        password.StatusCode.ShouldBe((HttpStatusCode)428);
        using HttpResponseMessage oldCode = await PostAsync(oldCodeClient, "/api/v1/auth/login/mfa", new { code = enrollment.Codes[0], isRecoveryCode = true, rememberMe = false, rememberClient = false });
        oldCode.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        using HttpClient current = await host.SignInWithMfaAsync(replacement.GetProperty("codes")[0].GetString()!, true);
    }

    [TestMethod]
    public async Task DisablingRequiresItsOwnProofAndSignsOutCurrentSession()
    {
        await using SecurityHost host = await SecurityHost.StartAsync();
        var enrollment = await EnrollAsync(host);
        using HttpClient client = await host.SignInWithMfaAsync(Totp(enrollment.Key));
        string wrongPurpose = await GrantAsync(client, "mfa.recovery-codes", Totp(enrollment.Key));
        using HttpResponseMessage denied = await PostAsync(client, "/mfa/disable", new { grant = wrongPurpose });
        await AssertProblemAsync(denied, HttpStatusCode.Forbidden, "reauthentication_required");
        await SuccessAsync(client, "/mfa/disable", new { grant = await GrantAsync(client, "mfa.disable", Totp(enrollment.Key)) });
        (await client.GetAsync("/api/v1/auth/session")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        using HttpClient passwordOnly = await host.SignInAsync();
        (await passwordOnly.GetFromJsonAsync<JsonElement>("/api/v1/account")).GetProperty("twoFactorEnabled").GetBoolean().ShouldBeFalse();
    }

    [TestMethod]
    public async Task UnsupportedExternalCredentialsDoNotSilentlyDowngradeAssurance()
    {
        await using SecurityHost host = await SecurityHost.StartAsync();
        using HttpClient client = await host.SignInAsync();
        // Fixture a valid external session: the identity retains its stamp but no
        // longer has a local password. No equivalent provider flow exists yet.
        await using (AsyncServiceScope scope = host.Services.CreateAsyncScope())
        {
            UserManager<ApplicationUser> users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            ApplicationUser user = (await users.FindByEmailAsync(SecurityHost.Email))!;
            user.PasswordHash = null;
            (await users.UpdateAsync(user)).Succeeded.ShouldBeTrue();
        }
        using HttpResponseMessage denied = await PostAsync(client, "/reauthenticate", new { purpose = "mfa.enroll", password = "Any value" });
        await AssertProblemAsync(denied, HttpStatusCode.UnprocessableEntity, "reauthentication_unsupported");
    }

    [TestMethod]
    public async Task WrongProofsLockOutTheAccountAndAntiforgeryIsRequired()
    {
        await using SecurityHost host = await SecurityHost.StartAsync();
        using HttpClient client = await host.SignInAsync();
        using HttpResponseMessage missingCsrf = await client.PostAsJsonAsync("/api/v1/account/security/reauthenticate", new { purpose = "mfa.enroll", password = SecurityHost.Password });
        missingCsrf.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        for (int attempt = 0; attempt < 5; attempt++)
        {
            using HttpResponseMessage wrong = await PostAsync(client, "/reauthenticate", new { purpose = "mfa.enroll", password = "Incorrect!Password42" });
            await AssertProblemAsync(wrong, HttpStatusCode.Forbidden, "reauthentication_failed");
        }
        using HttpResponseMessage correct = await PostAsync(client, "/reauthenticate", new { purpose = "mfa.enroll", password = SecurityHost.Password });
        await AssertProblemAsync(correct, HttpStatusCode.Forbidden, "reauthentication_failed");
    }

    [TestMethod]
    public async Task SuccessfulEnrollmentConfirmationResetsEarlierFailedAttempts()
    {
        await using SecurityHost host = await SecurityHost.StartAsync();
        using HttpClient client = await host.SignInAsync();
        JsonElement setup = await StartEnrollmentAsync(client);
        for (int attempt = 0; attempt < 4; attempt++)
        {
            using HttpResponseMessage wrong = await PostAsync(client, "/mfa/enable", new
            {
                enrollmentId = setup.GetProperty("enrollmentId"),
                code = "invalid"
            });
            await AssertProblemAsync(wrong, HttpStatusCode.Forbidden, "reauthentication_failed");
        }

        await SuccessAsync(client, "/mfa/enable", new
        {
            enrollmentId = setup.GetProperty("enrollmentId"),
            code = Totp(setup.GetProperty("sharedKey").GetString()!, host.Clock.GetUtcNow())
        });

        using HttpClient login = host.CreateClient();
        using HttpResponseMessage wrongPassword = await PostAsync(login, "/api/v1/auth/login", new
        {
            email = SecurityHost.Email,
            password = "Wrong!Password42",
            rememberMe = false
        });
        wrongPassword.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        using HttpResponseMessage correctPassword = await PostAsync(login, "/api/v1/auth/login", new
        {
            email = SecurityHost.Email,
            password = SecurityHost.Password,
            rememberMe = false
        });
        correctPassword.StatusCode.ShouldBe((HttpStatusCode)428);
    }

    [TestMethod]
    public async Task RecoveryProofStorageFailureRollsBackWithoutConsumingTheFactor()
    {
        await using SecurityHost host = await SecurityHost.StartAsync(legacyMfa: true);
        using HttpClient client = await host.SignInWithMfaAsync(Totp(SecurityHost.LegacyKey));
        await host.FailFirstIdentityUpdateAsync();

        using HttpResponseMessage failed = await PostAsync(client, "/reauthenticate", new
        {
            purpose = "mfa.replace",
            password = SecurityHost.Password,
            code = SecurityHost.LegacyRecoveryCode,
            isRecoveryCode = true
        });

        await AssertProblemAsync(failed, HttpStatusCode.BadRequest, "identity_validation");
        await host.RemoveIdentityUpdateFailureAsync();
        (await GrantAsync(client, "mfa.replace", SecurityHost.LegacyRecoveryCode, true)).ShouldNotBeNullOrWhiteSpace();
    }

    private static async Task<JsonElement> StartEnrollmentAsync(HttpClient client, string purpose = "mfa.enroll", string? code = null) =>
        await SuccessAsync(client, "/mfa/setup", new { grant = await GrantAsync(client, purpose, code) });

    internal static string Totp(string key, DateTimeOffset? now = null)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        string bits = string.Concat(key.Select(character => Convert.ToString(alphabet.IndexOf(character, StringComparison.Ordinal), 2).PadLeft(5, '0')));
        byte[] secret = Enumerable.Range(0, bits.Length / 8).Select(index => Convert.ToByte(bits.Substring(index * 8, 8), 2)).ToArray();
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() / 30);
#pragma warning disable CA5350 // Identity's authenticator format is RFC 6238 with HMAC-SHA1.
        byte[] hash = HMACSHA1.HashData(secret, counter);
#pragma warning restore CA5350
        int offset = hash[^1] & 15;
        int value = ((hash[offset] & 127) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (value % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }

    internal static async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, object body)
    {
        JsonElement antiforgery = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/antiforgery");
        using HttpRequestMessage request = new(HttpMethod.Post, path.StartsWith("/api/", StringComparison.Ordinal) ? path : "/api/v1/account/security" + path)
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Add("X-CSRF-TOKEN", antiforgery.GetProperty("token").GetString());
        return await client.SendAsync(request);
    }

    private static async Task<JsonElement> SuccessAsync(HttpClient client, string path, object body)
    {
        using HttpResponseMessage response = await PostAsync(client, path, body);
        response.IsSuccessStatusCode.ShouldBeTrue($"Unexpected HTTP {(int)response.StatusCode}");
        if (!path.StartsWith("/api/", StringComparison.Ordinal)) (response.Headers.CacheControl?.NoStore).ShouldBe(true);
        return response.StatusCode == HttpStatusCode.NoContent ? default : await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        response.StatusCode.ShouldBe(status);
        JsonElement problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("title").GetString().ShouldBe(code);
        problem.TryGetProperty("sharedKey", out _).ShouldBeFalse();
        problem.TryGetProperty("grant", out _).ShouldBeFalse();
        problem.TryGetProperty("codes", out _).ShouldBeFalse();
    }

    internal sealed class TestClock : TimeProvider
    {
        private long ticks = DateTimeOffset.UtcNow.UtcTicks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        public void Advance(TimeSpan duration) => Interlocked.Add(ref ticks, duration.Ticks);
    }

    internal sealed class SecurityHost(WebApplicationFactory<Program> factory, PostgreSqlContainer? postgres, string ownerConnection, string? maintenanceConnection, string? databaseName, TestClock clock, Dictionary<string, string?> settings) : IAsyncDisposable
    {
        public const string Email = "security-admin@trykatch.test";
        public const string Password = "Local-only!Security-Password-42";
        public const string LegacyKey = "JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP";
        public const string LegacyRecoveryCode = "ABCD2-EFGH3";
        public TestClock Clock => clock;
        public IServiceProvider Services => factory.Services;
        public string OwnerConnection => ownerConnection;

        public async Task RestartAsync(IReadOnlyDictionary<string, string?> overrides, string environment = "Production", Action<IServiceCollection>? configureServices = null)
        {
            await factory.DisposeAsync();
            foreach ((string key, string? value) in overrides) settings[key] = value;
            factory = CreateFactory(settings, clock, environment, configureServices);
        }

        private static WebApplicationFactory<Program> CreateFactory(Dictionary<string, string?> settings, TestClock clock, string environment, Action<IServiceCollection>? configureServices = null) =>
            new WebApplicationFactory<Program>().WithWebHostBuilder(webHost =>
            {
                webHost.UseEnvironment(environment);
                foreach ((string key, string? value) in settings) webHost.UseSetting(key, value);
                webHost.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings));
                webHost.ConfigureTestServices(services =>
                {
                    services.AddSingleton<TimeProvider>(clock);
                    configureServices?.Invoke(services);
                });
            });

        public static async Task<SecurityHost> StartAsync(bool legacyMfa = false)
        {
            string? configured = Environment.GetEnvironmentVariable("TRYKATCH_TEST_POSTGRES");
            PostgreSqlContainer? postgres = string.IsNullOrWhiteSpace(configured)
                ? new PostgreSqlBuilder("postgres:18.6-alpine3.23@sha256:697c180dbf244d3ce4a8f4cbc0156cde840af055c1bf8b76aebe422a4822086f").Build() : null;
            if (postgres is not null) await postgres.StartAsync();
            string ownerConnection = postgres?.GetConnectionString() ?? configured!;
            string? databaseName = null;
            if (postgres is null)
            {
                databaseName = $"trykatch_step_up_{Guid.NewGuid():N}";
                await using NpgsqlConnection maintenance = new(configured);
                await maintenance.OpenAsync();
                await using NpgsqlCommand create = new($"CREATE DATABASE \"{databaseName}\"", maintenance);
                await create.ExecuteNonQueryAsync();
                ownerConnection = new NpgsqlConnectionStringBuilder(configured) { Database = databaseName }.ConnectionString;
            }
            try
            {
                var connections = await PostgresRuntimeRoleFixture.CreateConnectionStringsAsync(ownerConnection);
                await using IdentityDbContext identity = new(new DbContextOptionsBuilder<IdentityDbContext>().UseNpgsql(ownerConnection).Options);
                if (legacyMfa)
                {
                    await identity.GetService<IMigrator>().MigrateAsync("20260907220644_AddPlatformAccessDirectory");
                    ApplicationUser legacyUser = new()
                    {
                        Id = Guid.NewGuid(),
                        UserName = Email,
                        NormalizedUserName = Email.ToUpperInvariant(),
                        Email = Email,
                        NormalizedEmail = Email.ToUpperInvariant(),
                        EmailConfirmed = true,
                        TwoFactorEnabled = true,
                        SecurityStamp = Guid.NewGuid().ToString("N"),
                        ConcurrencyStamp = Guid.NewGuid().ToString("N"),
                        LockoutEnabled = true,
                        DisplayName = "Existing MFA user"
                    };
                    legacyUser.PasswordHash = new PasswordHasher<ApplicationUser>().HashPassword(legacyUser, Password);
                    identity.Users.Add(legacyUser);
                    identity.UserTokens.AddRange(
                        new IdentityUserToken<Guid> { UserId = legacyUser.Id, LoginProvider = "[AspNetUserStore]", Name = "AuthenticatorKey", Value = LegacyKey },
                        new IdentityUserToken<Guid> { UserId = legacyUser.Id, LoginProvider = "[AspNetUserStore]", Name = "RecoveryCodes", Value = LegacyRecoveryCode });
                    await identity.SaveChangesAsync();
                }
                await identity.Database.MigrateAsync();
                await using PlatformDbContext platform = new(new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(ownerConnection).Options);
                await platform.Database.MigrateAsync();
                ProjectsModule projects = new();
                DocumentsModule documents = new();
                ModuleCatalog catalog = new([projects, documents]);
                await using ApplicationDbContext application = new(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(ownerConnection).Options,
                    [new ProjectsModelContributor(), new DocumentsModelContributor()], moduleCatalog: catalog);
                await application.Database.MigrateAsync();
                await using NpgsqlConnection connection = new(ownerConnection);
                await connection.OpenAsync();
                foreach (ModuleMigration migration in documents.Migrations)
                {
                    await using NpgsqlCommand command = new(migration.Sql, connection);
                    await command.ExecuteNonQueryAsync();
                }
                await InstalledSchemaCatalog.SynchronizeAsync(ownerConnection, catalog.Descriptors.SelectMany(module =>
                    module.DataResources.Select(resource => new InstalledDataResource(module.Id, resource))));
                RuntimeDatabaseRoles runtimeRoles = new(PostgresRuntimeRoleFixture.OrganizationRole, PostgresRuntimeRoleFixture.PlatformRole,
                    PostgresRuntimeRoleFixture.IdentityRole, PostgresRuntimeRoleFixture.OutboxRole);
                await RuntimeRoleProvisioner.ProvisionAsync(ownerConnection, runtimeRoles);
                (await PostgresIsolationInspector.InspectAsync(ownerConnection, runtimeRoles, catalog.Descriptors)).ThrowIfInvalid();
                Dictionary<string, string?> settings = new()
                {
                    ["ConnectionStrings:trykatchdb"] = connections.Organization,
                    ["ConnectionStrings:trykatch-organization"] = connections.Organization,
                    ["ConnectionStrings:trykatch-platform"] = connections.Platform,
                    ["ConnectionStrings:trykatch-identity"] = connections.Identity,
                    ["ConnectionStrings:trykatch-outbox"] = connections.Outbox,
                    ["Bootstrap:PlatformAdminEmail"] = Email,
                    ["Bootstrap:PlatformAdminPassword"] = Password,
                    ["DevelopmentDemo:Enabled"] = "false"
                };
                TestClock clock = new();
                WebApplicationFactory<Program> factory = CreateFactory(settings, clock, "Development");
                return new(factory, postgres, ownerConnection, configured, databaseName, clock, settings);
            }
            catch
            {
                if (postgres is not null) await postgres.DisposeAsync();
                else await DropDatabaseAsync(configured!, databaseName!);
                throw;
            }
        }

        public HttpClient CreateClient(string? cookie = null)
        {
            HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://localhost"),
                AllowAutoRedirect = false,
                HandleCookies = cookie is null
            });
            if (cookie is not null) client.DefaultRequestHeaders.Add("Cookie", cookie);
            return client;
        }

        public async Task<HttpClient> SignInAsync()
        {
            HttpClient client = CreateClient();
            await SuccessAsync(client, "/api/v1/auth/login", new { email = Email, password = Password, rememberMe = false });
            return client;
        }

        public async Task<HttpClient> SignInWithMfaAsync(string code, bool isRecoveryCode = false)
        {
            HttpClient client = CreateClient();
            using HttpResponseMessage password = await PostAsync(client, "/api/v1/auth/login", new { email = Email, password = Password, rememberMe = false });
            password.StatusCode.ShouldBe((HttpStatusCode)428);
            await SuccessAsync(client, "/api/v1/auth/login/mfa", new { code, isRecoveryCode, rememberMe = false, rememberClient = false });
            return client;
        }

        public async Task FailFirstIdentityUpdateAsync()
        {
            await using NpgsqlConnection connection = new(ownerConnection);
            await connection.OpenAsync();
            await using NpgsqlCommand command = new("""
                CREATE SEQUENCE identity.test_identity_updates;
                GRANT USAGE ON SEQUENCE identity.test_identity_updates TO trykatch_identity_runtime;
                CREATE FUNCTION identity.test_fail_identity_update() RETURNS trigger
                LANGUAGE plpgsql AS $failure$
                BEGIN
                    -- Sequences deliberately survive savepoint rollback: only the
                    -- first real store update fails, later updates can succeed.
                    IF nextval('identity.test_identity_updates') = 1 THEN RETURN NULL; END IF;
                    RETURN NEW;
                END
                $failure$;
                CREATE TRIGGER test_fail_identity_update BEFORE UPDATE ON identity."AspNetUsers"
                    FOR EACH ROW EXECUTE FUNCTION identity.test_fail_identity_update();
                """, connection);
            await command.ExecuteNonQueryAsync();
        }

        public async Task RemoveIdentityUpdateFailureAsync()
        {
            await using NpgsqlConnection connection = new(ownerConnection);
            await connection.OpenAsync();
            await using NpgsqlCommand command = new("""
                DROP TRIGGER test_fail_identity_update ON identity."AspNetUsers";
                DROP FUNCTION identity.test_fail_identity_update();
                DROP SEQUENCE identity.test_identity_updates;
                """, connection);
            await command.ExecuteNonQueryAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await factory.DisposeAsync();
            if (postgres is not null) await postgres.DisposeAsync();
            else await DropDatabaseAsync(maintenanceConnection!, databaseName!);
        }

        private static async Task DropDatabaseAsync(string connection, string database)
        {
            await using NpgsqlConnection maintenance = new(connection);
            await maintenance.OpenAsync();
            await using NpgsqlCommand drop = new($"DROP DATABASE \"{database}\" WITH (FORCE)", maintenance);
            await drop.ExecuteNonQueryAsync();
        }
    }
}

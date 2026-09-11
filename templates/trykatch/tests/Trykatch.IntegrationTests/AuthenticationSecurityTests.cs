using System.Net;
using System.Net.Http.Json;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Trykatch.Api.Controllers;
using Trykatch.Identity;
using Trykatch.Infrastructure.Modules;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Modules;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using Shouldly;
using Testcontainers.PostgreSql;

namespace Trykatch.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
public sealed class AuthenticationSecurityTests
{
    private const string AdministratorEmail = "admin@trykatch.test";
    private const string AdministratorPassword = "Local-only!Administrator-Password-42";

    [TestMethod]
    public async Task CookieAndOpenIdConnectSecurityControlsWorkTogether()
    {
        await using PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:18.6-alpine3.23@sha256:697c180dbf244d3ce4a8f4cbc0156cde840af055c1bf8b76aebe422a4822086f").Build();
        await postgres.StartAsync();
        await ApplyMigrationsAsync(postgres.GetConnectionString());

        await using WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(webHost =>
            {
                webHost.UseEnvironment("Development");
                // AddInfrastructure reads the connection string while Program is registering
                // services, before late application-configuration callbacks are applied.
                webHost.UseSetting("ConnectionStrings:trykatchdb", postgres.GetConnectionString());
                webHost.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:trykatchdb"] = postgres.GetConnectionString(),
                    ["Bootstrap:PlatformAdminEmail"] = AdministratorEmail,
                    ["Bootstrap:PlatformAdminPassword"] = AdministratorPassword,
                    ["OpenIddict:Clients:0:ClientId"] = "automation-tests",
                    ["OpenIddict:Clients:0:DisplayName"] = "Automation tests",
                    ["OpenIddict:Clients:0:GrantType"] = "client_credentials",
                    ["OpenIddict:Clients:0:ClientSecret"] = "Client-credential-test-secret-42!",
                    ["OpenIddict:Clients:1:ClientId"] = "browser-tests",
                    ["OpenIddict:Clients:1:DisplayName"] = "Browser tests",
                    ["OpenIddict:Clients:1:GrantType"] = "authorization_code",
                    ["OpenIddict:Clients:1:RedirectUris:0"] = "https://client.trykatch.test/callback"
                }));
                webHost.ConfigureTestServices(services => services.PostConfigure<SecurityStampValidatorOptions>(options =>
                    options.ValidationInterval = TimeSpan.Zero));
            });

        using HttpClient client = CreateClient(factory);
        string antiforgery = await GetAntiforgeryTokenAsync(client);

        HttpResponseMessage missingAntiforgery = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            email = AdministratorEmail,
            password = AdministratorPassword,
            rememberMe = false
        });
        missingAntiforgery.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        await AssertJsonNullabilityBoundaryAsync(client, antiforgery);

        HttpResponseMessage signedIn = await PostWithAntiforgeryAsync(client, "/api/v1/auth/login", antiforgery, new
        {
            email = AdministratorEmail,
            password = AdministratorPassword,
            rememberMe = false
        });
        signedIn.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync("/api/v1/auth/session")).StatusCode.ShouldBe(HttpStatusCode.OK);

        antiforgery = await GetAntiforgeryTokenAsync(client);
        HttpResponseMessage signedOut = await PostWithAntiforgeryAsync(client, "/api/v1/auth/logout", antiforgery, new { });
        signedOut.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.GetAsync("/api/v1/auth/session")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        await AssertLockoutAsync(factory);
        await AssertSecurityStampInvalidatesSessionAsync(factory);
        await AssertPasswordResetAsync(factory);
        await AssertMultiFactorAndRecoveryCodeAsync(factory);
        await AssertOpenIdConnectGrantPolicyAsync(factory);
        await AssertPlatformSuspensionRevokesExistingAndFreshSessionsAsync(factory);
    }

    private static async Task AssertPlatformSuspensionRevokesExistingAndFreshSessionsAsync(WebApplicationFactory<Program> factory)
    {
        using HttpClient anonymous = CreateClient(factory);
        (await anonymous.GetAsync("/api/v1/platform-users")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        using HttpClient existingSession = CreateClient(factory);
        string antiforgery = await GetAntiforgeryTokenAsync(existingSession);
        HttpResponseMessage signedIn = await PostWithAntiforgeryAsync(existingSession, "/api/v1/auth/login", antiforgery, new
        {
            email = AdministratorEmail,
            password = AdministratorPassword,
            rememberMe = false
        });
        signedIn.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await existingSession.GetAsync("/api/v1/platform-users")).StatusCode.ShouldBe(HttpStatusCode.OK);

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            UserManager<ApplicationUser> users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            ApplicationUser administrator = await users.FindByEmailAsync(AdministratorEmail)
                ?? throw new InvalidOperationException("The platform administrator was not persisted.");
            administrator.IsPlatformAccessSuspended = true;
            (await users.UpdateAsync(administrator)).Succeeded.ShouldBeTrue();
        }

        HttpResponseMessage existingSessionState = await existingSession.GetAsync("/api/v1/auth/session");
        existingSessionState.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await existingSession.GetAsync("/api/v1/me/organizations")).StatusCode.ShouldBe(HttpStatusCode.OK);
        HttpResponseMessage stalePlatformRequest = await existingSession.GetAsync("/api/v1/platform-users");
        stalePlatformRequest.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await stalePlatformRequest.Content.ReadAsStringAsync());

        using HttpClient freshSession = CreateClient(factory);
        antiforgery = await GetAntiforgeryTokenAsync(freshSession);
        HttpResponseMessage freshSignIn = await PostWithAntiforgeryAsync(freshSession, "/api/v1/auth/login", antiforgery, new
        {
            email = AdministratorEmail,
            password = AdministratorPassword,
            rememberMe = false
        });
        freshSignIn.StatusCode.ShouldBe(HttpStatusCode.OK);
        SessionResponse? session = await freshSignIn.Content.ReadFromJsonAsync<SessionResponse>();
        session.ShouldNotBeNull();
        session.IsPlatformAdministrator.ShouldBeFalse();
        session.HasPlatformAccess.ShouldBeFalse();
        session.PlatformRole.ShouldBeNull();
        session.PlatformPermissions.ShouldBeEmpty();
        (await freshSession.GetAsync("/api/v1/platform-users")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private static async Task AssertJsonNullabilityBoundaryAsync(HttpClient client, string antiforgery)
    {
        HttpResponseMessage explicitNull = await PostWithAntiforgeryAsync(client, "/api/v1/auth/login", antiforgery, new
        {
            email = (string?)null,
            password = AdministratorPassword,
            rememberMe = false
        });
        explicitNull.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        HttpResponseMessage missingRequiredValue = await PostWithAntiforgeryAsync(client, "/api/v1/auth/login", antiforgery, new
        {
            email = AdministratorEmail,
            rememberMe = false
        });
        missingRequiredValue.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private static async Task AssertPasswordResetAsync(WebApplicationFactory<Program> factory)
    {
        const string email = "recovery@trykatch.test";
        const string oldPassword = "Local-only!Recovery-Password-42";
        const string newPassword = "Local-only!Recovered-Password-84";
        await CreateConfirmedUserAsync(factory, email, oldPassword);

        using HttpClient activeSession = CreateClient(factory);
        string antiforgery = await GetAntiforgeryTokenAsync(activeSession);
        (await PostWithAntiforgeryAsync(activeSession, "/api/v1/auth/login", antiforgery, new
        {
            email,
            password = oldPassword,
            rememberMe = false
        })).StatusCode.ShouldBe(HttpStatusCode.OK);

        using HttpClient recovery = CreateClient(factory);
        recovery.DefaultRequestHeaders.Add("Origin", "http://127.0.0.1:5173");
        antiforgery = await GetAntiforgeryTokenAsync(recovery);
        HttpResponseMessage unknown = await PostWithAntiforgeryAsync(recovery, "/api/v1/auth/password/forgot", antiforgery, new
        {
            email = "missing@trykatch.test"
        });
        unknown.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        using JsonDocument unknownPayload = JsonDocument.Parse(await unknown.Content.ReadAsStringAsync());
        unknownPayload.RootElement.GetProperty("developmentResetUrl").ValueKind.ShouldBe(JsonValueKind.Null);

        HttpResponseMessage requested = await PostWithAntiforgeryAsync(recovery, "/api/v1/auth/password/forgot", antiforgery, new { email });
        requested.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        using JsonDocument payload = JsonDocument.Parse(await requested.Content.ReadAsStringAsync());
        string resetUrl = payload.RootElement.GetProperty("developmentResetUrl").GetString()
            ?? throw new InvalidOperationException("Development password recovery did not return a reset URL.");
        Uri resetUri = new(resetUrl);
        resetUri.GetLeftPart(UriPartial.Authority).ShouldBe("http://127.0.0.1:5173");
        Dictionary<string, Microsoft.Extensions.Primitives.StringValues> query = QueryHelpers.ParseQuery(resetUri.Query);

        HttpResponseMessage reset = await PostWithAntiforgeryAsync(recovery, "/api/v1/auth/password/reset", antiforgery, new
        {
            email = query["email"].ToString(),
            token = query["token"].ToString(),
            newPassword,
            confirmPassword = newPassword
        });
        reset.StatusCode.ShouldBe(HttpStatusCode.NoContent, await reset.Content.ReadAsStringAsync());

        HttpResponseMessage replay = await PostWithAntiforgeryAsync(recovery, "/api/v1/auth/password/reset", antiforgery, new
        {
            email = query["email"].ToString(),
            token = query["token"].ToString(),
            newPassword,
            confirmPassword = newPassword
        });
        replay.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await activeSession.GetAsync("/api/v1/auth/session")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        using HttpClient signIn = CreateClient(factory);
        antiforgery = await GetAntiforgeryTokenAsync(signIn);
        (await PostWithAntiforgeryAsync(signIn, "/api/v1/auth/login", antiforgery, new { email, password = oldPassword, rememberMe = false })).StatusCode
            .ShouldBe(HttpStatusCode.Unauthorized);
        (await PostWithAntiforgeryAsync(signIn, "/api/v1/auth/login", antiforgery, new { email, password = newPassword, rememberMe = false })).StatusCode
            .ShouldBe(HttpStatusCode.OK);
    }

    private static async Task AssertSecurityStampInvalidatesSessionAsync(WebApplicationFactory<Program> factory)
    {
        const string email = "stamp@trykatch.test";
        const string password = "Local-only!Security-Stamp-Password-42";
        ApplicationUser createdUser = await CreateConfirmedUserAsync(factory, email, password);
        using HttpClient client = CreateClient(factory);
        string antiforgery = await GetAntiforgeryTokenAsync(client);
        HttpResponseMessage signedIn = await PostWithAntiforgeryAsync(client, "/api/v1/auth/login", antiforgery, new
        {
            email,
            password,
            rememberMe = false
        });
        signedIn.StatusCode.ShouldBe(HttpStatusCode.OK);

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            UserManager<ApplicationUser> users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            ApplicationUser user = await users.FindByIdAsync(createdUser.Id.ToString())
                ?? throw new InvalidOperationException("The security-stamp test identity was not persisted.");
            (await users.UpdateSecurityStampAsync(user)).Succeeded.ShouldBeTrue();
        }

        (await client.GetAsync("/api/v1/auth/session")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private static async Task AssertLockoutAsync(WebApplicationFactory<Program> factory)
    {
        const string email = "locked@trykatch.test";
        const string password = "Local-only!Lockout-Password-42";
        await CreateConfirmedUserAsync(factory, email, password);
        using HttpClient client = CreateClient(factory);
        string antiforgery = await GetAntiforgeryTokenAsync(client);

        HttpStatusCode lastStatus = HttpStatusCode.OK;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            HttpResponseMessage response = await PostWithAntiforgeryAsync(client, "/api/v1/auth/login", antiforgery, new
            {
                email,
                password = "Incorrect!Password-42",
                rememberMe = false
            });
            lastStatus = response.StatusCode;
        }

        lastStatus.ShouldBe((HttpStatusCode)423);
        HttpResponseMessage correctPassword = await PostWithAntiforgeryAsync(client, "/api/v1/auth/login", antiforgery, new
        {
            email,
            password,
            rememberMe = false
        });
        correctPassword.StatusCode.ShouldBe((HttpStatusCode)423);
    }

    private static async Task AssertMultiFactorAndRecoveryCodeAsync(WebApplicationFactory<Program> factory)
    {
        const string email = "mfa@trykatch.test";
        const string password = "Local-only!Multi-Factor-Password-42";
        ApplicationUser createdUser = await CreateConfirmedUserAsync(factory, email, password);
        string recoveryCode;
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            UserManager<ApplicationUser> users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            ApplicationUser user = await users.FindByIdAsync(createdUser.Id.ToString())
                ?? throw new InvalidOperationException("The MFA test identity was not persisted.");
            (await users.ResetAuthenticatorKeyAsync(user)).Succeeded.ShouldBeTrue();
            (await users.SetTwoFactorEnabledAsync(user, true)).Succeeded.ShouldBeTrue();
            recoveryCode = (await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 1)).ShouldHaveSingleItem();
        }

        using HttpClient client = CreateClient(factory);
        string antiforgery = await GetAntiforgeryTokenAsync(client);
        HttpResponseMessage passwordStep = await PostWithAntiforgeryAsync(client, "/api/v1/auth/login", antiforgery, new
        {
            email,
            password,
            rememberMe = false
        });
        passwordStep.StatusCode.ShouldBe((HttpStatusCode)428);

        string authenticatorCode;
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            UserManager<ApplicationUser> users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            ApplicationUser user = await users.FindByIdAsync(createdUser.Id.ToString())
                ?? throw new InvalidOperationException("The MFA test identity was not persisted.");
            string key = await users.GetAuthenticatorKeyAsync(user)
                ?? throw new InvalidOperationException("The MFA test identity has no authenticator key.");
            authenticatorCode = GenerateAuthenticatorCode(key);
        }

        HttpResponseMessage mfaStep = await PostWithAntiforgeryAsync(client, "/api/v1/auth/login/mfa", antiforgery, new
        {
            code = authenticatorCode,
            isRecoveryCode = false,
            rememberMe = false,
            rememberClient = false
        });
        mfaStep.StatusCode.ShouldBe(HttpStatusCode.OK, await mfaStep.Content.ReadAsStringAsync());

        antiforgery = await GetAntiforgeryTokenAsync(client);
        await PostWithAntiforgeryAsync(client, "/api/v1/auth/logout", antiforgery, new { });
        antiforgery = await GetAntiforgeryTokenAsync(client);
        await PostWithAntiforgeryAsync(client, "/api/v1/auth/login", antiforgery, new { email, password, rememberMe = false });
        HttpResponseMessage recovery = await PostWithAntiforgeryAsync(client, "/api/v1/auth/login/mfa", antiforgery, new
        {
            code = recoveryCode,
            isRecoveryCode = true,
            rememberMe = false,
            rememberClient = false
        });
        recovery.StatusCode.ShouldBe(HttpStatusCode.OK);

        antiforgery = await GetAntiforgeryTokenAsync(client);
        await PostWithAntiforgeryAsync(client, "/api/v1/auth/logout", antiforgery, new { });
        antiforgery = await GetAntiforgeryTokenAsync(client);
        await PostWithAntiforgeryAsync(client, "/api/v1/auth/login", antiforgery, new { email, password, rememberMe = false });
        HttpResponseMessage replay = await PostWithAntiforgeryAsync(client, "/api/v1/auth/login/mfa", antiforgery, new
        {
            code = recoveryCode,
            isRecoveryCode = true,
            rememberMe = false,
            rememberClient = false
        });
        replay.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private static async Task AssertOpenIdConnectGrantPolicyAsync(WebApplicationFactory<Program> factory)
    {
        using HttpClient client = CreateClient(factory);
        HttpResponseMessage token = await client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = OpenIddictConstants.GrantTypes.ClientCredentials,
            ["client_id"] = "automation-tests",
            ["client_secret"] = "Client-credential-test-secret-42!",
            ["scope"] = "trykatch-api"
        }));
        token.StatusCode.ShouldBe(HttpStatusCode.OK);
        using JsonDocument tokenPayload = JsonDocument.Parse(await token.Content.ReadAsStringAsync());
        tokenPayload.RootElement.GetProperty("access_token").GetString().ShouldNotBeNullOrWhiteSpace();

        HttpResponseMessage passwordGrant = await client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = OpenIddictConstants.GrantTypes.Password,
            ["client_id"] = "automation-tests",
            ["username"] = AdministratorEmail,
            ["password"] = AdministratorPassword
        }));
        passwordGrant.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        using IServiceScope scope = factory.Services.CreateScope();
        IOpenIddictApplicationManager applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        object browser = await applications.FindByClientIdAsync("browser-tests")
            ?? throw new InvalidOperationException("Browser test client was not seeded.");
        ImmutableArray<string> requirements = await applications.GetRequirementsAsync(browser);
        requirements.ShouldContain(OpenIddictConstants.Requirements.Features.ProofKeyForCodeExchange);
        ImmutableArray<string> permissions = await applications.GetPermissionsAsync(browser);
        permissions.ShouldNotContain(OpenIddictConstants.Permissions.GrantTypes.Password);
        permissions.ShouldNotContain(OpenIddictConstants.Permissions.GrantTypes.Implicit);
    }

    private static HttpClient CreateClient(WebApplicationFactory<Program> factory) => factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"),
        AllowAutoRedirect = false,
        HandleCookies = true
    });

    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client)
    {
        using JsonDocument payload = JsonDocument.Parse(await client.GetStringAsync("/api/v1/auth/antiforgery"));
        return payload.RootElement.GetProperty("token").GetString()
            ?? throw new InvalidOperationException("Antiforgery token was not returned.");
    }

    private static Task<HttpResponseMessage> PostWithAntiforgeryAsync(HttpClient client, string url, string token, object body)
    {
        HttpRequestMessage request = new(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-CSRF-TOKEN", token);
        return client.SendAsync(request);
    }

    private static string GenerateAuthenticatorCode(string base32Key)
    {
        byte[] key = DecodeBase32(base32Key);
        Span<byte> counter = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(counter, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
#pragma warning disable CA5350 // ASP.NET Core Identity's RFC 6238 authenticator provider uses HMAC-SHA1 by specification.
        byte[] hash = HMACSHA1.HashData(key, counter);
#pragma warning restore CA5350
        int offset = hash[^1] & 0x0f;
        int value = ((hash[offset] & 0x7f) << 24)
            | ((hash[offset + 1] & 0xff) << 16)
            | ((hash[offset + 2] & 0xff) << 8)
            | (hash[offset + 3] & 0xff);
        return (value % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }

    private static byte[] DecodeBase32(string value)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        List<byte> bytes = [];
        int buffer = 0;
        int bits = 0;
        foreach (char character in value.TrimEnd('=').ToUpperInvariant())
        {
            int digit = alphabet.IndexOf(character, StringComparison.Ordinal);
            if (digit < 0) throw new FormatException("Authenticator key is not valid Base32.");
            buffer = (buffer << 5) | digit;
            bits += 5;
            if (bits < 8) continue;
            bits -= 8;
            bytes.Add((byte)(buffer >> bits));
            buffer &= (1 << bits) - 1;
        }
        return [.. bytes];
    }

    private static async Task<ApplicationUser> CreateConfirmedUserAsync(WebApplicationFactory<Program> factory, string email, string password)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        UserManager<ApplicationUser> users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        ApplicationUser user = new()
        {
            Id = Guid.CreateVersion7(),
            Email = email,
            UserName = email,
            EmailConfirmed = true,
            LockoutEnabled = true,
            DisplayName = email.Split('@')[0],
            CreatedAt = DateTimeOffset.UtcNow
        };
        IdentityResult result = await users.CreateAsync(user, password);
        result.Succeeded.ShouldBeTrue(string.Join("; ", result.Errors.Select(error => error.Description)));
        return user;
    }

    private static async Task ApplyMigrationsAsync(string connectionString)
    {
        await PostgresRuntimeRoleFixture.EnsureRuntimeRolesAsync(connectionString);
        await using IdentityDbContext identity = new(new DbContextOptionsBuilder<IdentityDbContext>().UseNpgsql(connectionString).Options);
        await identity.Database.MigrateAsync();
        await using PlatformDbContext platform = new(new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(connectionString).Options);
        await platform.Database.MigrateAsync();
        await using ApplicationDbContext application = new(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(connectionString).Options,
            [new ProjectsModelContributor()],
            moduleCatalog: new ModuleCatalog([new ProjectsModule()]));
        await application.Database.MigrateAsync();
    }
}

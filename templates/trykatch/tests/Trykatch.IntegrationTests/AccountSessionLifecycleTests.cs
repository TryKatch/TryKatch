using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Identity;
using Npgsql;
using Shouldly;
using Trykatch.Identity;
using static Trykatch.IntegrationTests.AccountSecurityStepUpTests;

namespace Trykatch.IntegrationTests;

[TestClass, TestCategory("Integration")]
public sealed class AccountSessionLifecycleTests
{
    [TestMethod]
    public async Task ApplicationCookiePersistsOnlyHashedIdentityAndLogoutRevokesOnlyThatBrowser()
    {
        await using SecurityHost host = await SecurityHost.StartAsync();
        using HttpClient first = host.CreateClient();
        using HttpClient second = host.CreateClient();
        var firstSession = await SignInAsync(host, first);
        var secondSession = await SignInAsync(host, second);
        firstSession.Hash.ShouldNotBe(secondSession.Hash);
        await using NpgsqlConnection connection = new(host.OwnerConnection);
        await connection.OpenAsync();
        await using (NpgsqlCommand relation = new("SELECT to_regclass('identity.account_sessions')::text", connection))
            (await relation.ExecuteScalarAsync()).ShouldBe("identity.account_sessions", "Cookie issuance requires a durable session record.");
        await using (NpgsqlCommand record = new("""
            SELECT "UserId", "SecurityStamp", "ExpiresAt", "OrganizationId", "WorkspaceGeneration", "RevokedAt"
            FROM identity.account_sessions WHERE "SessionHash" = @hash
            """, connection))
        {
            record.Parameters.AddWithValue("hash", firstSession.Hash);
            await using NpgsqlDataReader reader = await record.ExecuteReaderAsync();
            (await reader.ReadAsync()).ShouldBeTrue();
            reader.GetGuid(0).ShouldBe(Guid.Parse(firstSession.Ticket.Principal.FindFirstValue(ClaimTypes.NameIdentifier)!));
            reader.GetString(1).ShouldBe(firstSession.Ticket.Principal.FindFirstValue("AspNet.Identity.SecurityStamp"));
            (reader.GetFieldValue<DateTimeOffset>(2) - firstSession.Ticket.Properties.ExpiresUtc!.Value).Duration().ShouldBeLessThan(TimeSpan.FromMilliseconds(1));
            reader.IsDBNull(3).ShouldBeTrue();
            reader.GetGuid(4).ShouldNotBe(Guid.Empty);
            reader.IsDBNull(5).ShouldBeTrue();
        }
        using (HttpResponseMessage logout = await PostAsync(first, "/api/v1/auth/logout", new { }))
            logout.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        using HttpClient copied = host.CreateClient(firstSession.Cookie);
        (await copied.GetAsync("/api/v1/auth/session")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await second.GetAsync("/api/v1/auth/session")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FrameworkRenewalExtendsExistingRowWithoutExtendingCapturedTickets(bool stampRenewal)
    {
        await using SecurityHost host = await SecurityHost.StartAsync();
        await ShortLifetimeAsync(host, stampRenewal);
        using HttpClient client = host.CreateClient();
        var original = await SignInAsync(host, client);
        host.Clock.Advance(TimeSpan.FromMinutes(6));
        using HttpClient copied = host.CreateClient(original.Cookie);
        using HttpResponseMessage renewal = await copied.GetAsync("/api/v1/auth/session");
        renewal.StatusCode.ShouldBe(HttpStatusCode.OK);
        var renewed = ReadSession(host, renewal);
        renewed.Hash.ShouldBe(original.Hash);
        renewed.Ticket.Properties.ExpiresUtc!.Value.ShouldBeGreaterThan(original.Ticket.Properties.ExpiresUtc!.Value);
        await using NpgsqlConnection connection = new(host.OwnerConnection);
        await connection.OpenAsync();
        await using (NpgsqlCommand row = new("SELECT \"ExpiresAt\" FROM identity.account_sessions WHERE \"SessionHash\" = @hash", connection))
        {
            row.Parameters.AddWithValue("hash", original.Hash);
            await using NpgsqlDataReader reader = await row.ExecuteReaderAsync();
            (await reader.ReadAsync()).ShouldBeTrue();
            DateTimeOffset expiry = reader.GetFieldValue<DateTimeOffset>(0);
            expiry.ShouldBeGreaterThan(original.Ticket.Properties.ExpiresUtc!.Value);
        }
        host.Clock.Advance(TimeSpan.FromMinutes(5));
        (await copied.GetAsync("/api/v1/auth/session")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        using HttpClient renewedClient = host.CreateClient(renewed.Cookie);
        (await renewedClient.GetAsync("/api/v1/auth/session")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("revoked")]
    [DataRow("expired")]
    [DataRow("stamp")]
    public async Task InvalidDurableSessionCancelsAnAlreadyRequestedSlidingRenewal(string invalid)
    {
        await using SecurityHost host = await SecurityHost.StartAsync();
        await ShortLifetimeAsync(host, stampRenewal: invalid == "stamp");
        using HttpClient client = host.CreateClient();
        var session = await SignInAsync(host, client);
        await using NpgsqlConnection connection = new(host.OwnerConnection);
        await connection.OpenAsync();
        string sql = invalid switch
        {
            "missing" => "DELETE FROM identity.account_sessions WHERE \"SessionHash\" = @hash",
            "revoked" => "UPDATE identity.account_sessions SET \"RevokedAt\" = CURRENT_TIMESTAMP WHERE \"SessionHash\" = @hash",
            "expired" => "UPDATE identity.account_sessions SET \"ExpiresAt\" = \"CreatedAt\" + INTERVAL '1 second' WHERE \"SessionHash\" = @hash",
            _ => "UPDATE identity.\"AspNetUsers\" SET \"SecurityStamp\" = 'a-new-security-stamp' WHERE \"Id\" = (SELECT \"UserId\" FROM identity.account_sessions WHERE \"SessionHash\" = @hash)"
        };
        await using (NpgsqlCommand change = new(sql, connection))
        {
            change.Parameters.AddWithValue("hash", session.Hash);
            (await change.ExecuteNonQueryAsync()).ShouldBe(1);
        }
        host.Clock.Advance(TimeSpan.FromMinutes(6));
        using HttpClient copied = host.CreateClient(session.Cookie);
        using HttpResponseMessage denied = await copied.GetAsync("/api/v1/auth/session");
        denied.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        if (denied.Headers.TryGetValues("Set-Cookie", out var cookies))
            cookies.Where(cookie => cookie.StartsWith("__Host-trykatch=", StringComparison.Ordinal))
                .ShouldAllBe(cookie => cookie.StartsWith("__Host-trykatch=;", StringComparison.Ordinal));
        using HttpResponseMessage retry = await copied.GetAsync("/api/v1/auth/session");
        retry.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        await using NpgsqlCommand count = new("SELECT count(*) FROM identity.account_sessions WHERE \"SessionHash\" = @hash AND \"RevokedAt\" IS NULL AND \"ExpiresAt\" > @now", connection);
        count.Parameters.AddWithValue("hash", session.Hash);
        count.Parameters.AddWithValue("now", host.Clock.GetUtcNow());
        (await count.ExecuteScalarAsync()).ShouldBe(0L);
    }

    [TestMethod]
    public async Task FreshSignInReplacesThisBrowsersPreviousIdentityWithoutAffectingAnotherDevice()
    {
        await using SecurityHost host = await SecurityHost.StartAsync();
        using HttpClient browser = host.CreateClient();
        var original = await SignInAsync(host, browser);
        using HttpClient otherDevice = await host.SignInAsync();
        var replacement = await SignInAsync(host, browser);
        replacement.Hash.ShouldNotBe(original.Hash);
        using HttpClient stolenOriginal = host.CreateClient(original.Cookie);
        (await stolenOriginal.GetAsync("/api/v1/auth/session")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await browser.GetAsync("/api/v1/auth/session")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await otherDevice.GetAsync("/api/v1/auth/session")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [TestMethod]
    [DataRow("legacy")]
    [DataRow("malformed")]
    [DataRow("unknown")]
    public async Task ProtectedLegacyOrUnregisteredSessionIdentityCannotCreateARow(string condition)
    {
        await using SecurityHost host = await SecurityHost.StartAsync();
        using HttpClient browser = host.CreateClient();
        var original = await SignInAsync(host, browser);
        if (condition == "legacy") original.Ticket.Properties.Items.Remove(AccountSecuritySession.PropertyName);
        else original.Ticket.Properties.Items[AccountSecuritySession.PropertyName] = condition == "malformed" ? "invalid-session-identity" : Guid.NewGuid().ToString("N");
        CookieAuthenticationOptions options = host.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(Trykatch.Identity.AuthenticationSchemes.ApplicationCookie);
        string cookie = "__Host-trykatch=" + options.TicketDataFormat.Protect(original.Ticket);
        using HttpClient copied = host.CreateClient(cookie);
        using HttpResponseMessage denied = await copied.GetAsync("/api/v1/auth/session");
        denied.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        await using NpgsqlConnection connection = new(host.OwnerConnection);
        await connection.OpenAsync();
        await using NpgsqlCommand count = new("SELECT count(*) FROM identity.account_sessions", connection);
        (await count.ExecuteScalarAsync()).ShouldBe(1L);
    }

    [TestMethod]
    public async Task StepUpGrantsAndPendingMfaBindToTheHashedDurableSessionIdentity()
    {
        await using SecurityHost host = await SecurityHost.StartAsync();
        using HttpClient browser = host.CreateClient();
        var session = await SignInAsync(host, browser);
        using HttpResponseMessage reauthenticate = await PostAsync(browser, "/reauthenticate", new
        {
            purpose = "mfa.enroll", password = SecurityHost.Password
        });
        reauthenticate.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement grant = await reauthenticate.Content.ReadFromJsonAsync<JsonElement>();
        await using NpgsqlConnection connection = new(host.OwnerConnection);
        await connection.OpenAsync();
        await using (NpgsqlCommand recent = new("SELECT \"SessionId\" FROM identity.recent_assurance_grants", connection))
            (await recent.ExecuteScalarAsync()).ShouldBe(session.Hash);
        using HttpResponseMessage setup = await PostAsync(browser, "/mfa/setup", new { grant = grant.GetProperty("grant").GetString() });
        setup.StatusCode.ShouldBe(HttpStatusCode.OK);
        await using NpgsqlCommand pending = new("SELECT \"SessionId\" FROM identity.pending_mfa_enrollments", connection);
        (await pending.ExecuteScalarAsync()).ShouldBe(session.Hash);
    }

    private static Task ShortLifetimeAsync(SecurityHost host, bool stampRenewal) => host.RestartAsync(
        new Dictionary<string, string?>(), "Development", services =>
        {
            services.AddOptions<CookieAuthenticationOptions>(Trykatch.Identity.AuthenticationSchemes.ApplicationCookie)
                .Configure<TimeProvider>((options, clock) =>
                {
                    options.TimeProvider = clock;
                    options.ExpireTimeSpan = TimeSpan.FromMinutes(10);
                });
            services.AddOptions<SecurityStampValidatorOptions>().Configure<TimeProvider>((options, clock) =>
            {
                options.TimeProvider = clock;
                options.ValidationInterval = stampRenewal ? TimeSpan.Zero : TimeSpan.FromDays(1);
            });
        });

    internal static async Task<(string Cookie, string Hash, AuthenticationTicket Ticket)> SignInAsync(SecurityHost host, HttpClient client)
    {
        using HttpResponseMessage response = await PostAsync(client, "/api/v1/auth/login", new
        {
            email = SecurityHost.Email, password = SecurityHost.Password, rememberMe = true
        });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return ReadSession(host, response);
    }

    private static (string Cookie, string Hash, AuthenticationTicket Ticket) ReadSession(SecurityHost host, HttpResponseMessage response)
    {
        string cookie = response.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith("__Host-trykatch=", StringComparison.Ordinal)).Split(';')[0];
        CookieAuthenticationOptions options = host.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(Trykatch.Identity.AuthenticationSchemes.ApplicationCookie);
        AuthenticationTicket ticket = options.TicketDataFormat.Unprotect(Uri.UnescapeDataString(cookie[(cookie.IndexOf('=') + 1)..]))!;
        ticket.ShouldNotBeNull();
        string sid = ticket.Properties.Items[AccountSecuritySession.PropertyName]!;
        return (cookie, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sid))), ticket);
    }
}

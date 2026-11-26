using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Trykatch.Application.Realtime;
using static Trykatch.IntegrationTests.AccountSecurityStepUpTests;
using static Trykatch.IntegrationTests.WorkspaceSessionLifecycleTests;

namespace Trykatch.IntegrationTests;

[TestClass, TestCategory("Integration")]
public sealed class RealtimeHistoryHttpTests
{
    [TestMethod]
    public async Task DisabledRealtimeKeepsCookieScopedStateAndHistoryAvailable()
    {
        await using SecurityHost host = await SecurityHost.StartAsync();
        using HttpClient anonymous = host.CreateClient();
        (await anonymous.GetAsync("/api/v1/realtime/state")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.GetAsync("/api/v1/realtime/history")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        using HttpClient browser = host.CreateClient();
        await AccountSessionLifecycleTests.SignInAsync(host, browser);
        (await browser.GetAsync("/api/v1/realtime/state")).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        await CreateOrganizationForCurrentUserAsync(browser, "realtime-history-default");
        JsonElement state = await StateAsync(browser);
        state.GetProperty("enabled").GetBoolean().ShouldBeFalse();
        state.GetProperty("generation").GetGuid().ShouldNotBe(Guid.Empty);
        string cursor = state.GetProperty("cursor").GetString()!;
        cursor.ShouldNotBeNullOrWhiteSpace();
        using HttpResponseMessage history = await browser.GetAsync("/api/v1/realtime/history?cursor=" + Uri.EscapeDataString(cursor));
        history.StatusCode.ShouldBe(HttpStatusCode.OK, await history.Content.ReadAsStringAsync());
        JsonElement page = await history.Content.ReadFromJsonAsync<JsonElement>();
        page.GetProperty("items").GetArrayLength().ShouldBe(0);
        page.GetProperty("hasMore").GetBoolean().ShouldBeFalse();
        page.GetProperty("generation").GetGuid().ShouldBe(state.GetProperty("generation").GetGuid());
        (await browser.GetAsync("/api/v1/realtime/history")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [TestMethod]
    [DataRow("malformed")]
    [DataRow("oversized")]
    [DataRow("future")]
    public async Task InvalidHistoryContinuationReturnsExplicitResetWithoutNotificationData(string condition)
    {
        await using SecurityHost host = await SecurityHost.StartAsync();
        using HttpClient browser = host.CreateClient();
        var session = await AccountSessionLifecycleTests.SignInAsync(host, browser);
        Guid organization = await CreateOrganizationForCurrentUserAsync(browser, "realtime-history-reset");
        JsonElement state = await StateAsync(browser);
        string cursor = condition == "oversized" ? new string('x', 2049) : "invalid-protected-cursor";
        if (condition == "future")
        {
            using IServiceScope scope = host.Services.CreateScope();
            IRealtimeCursorProtector protector = scope.ServiceProvider.GetRequiredService<IRealtimeCursorProtector>();
            cursor = protector.Protect(new(organization,
                Guid.Parse(session.Ticket.Principal.FindFirstValue(ClaimTypes.NameIdentifier)!), session.Hash,
                state.GetProperty("generation").GetGuid()), 1);
        }
        await AssertResetAsync(browser, cursor);
    }

    [TestMethod]
    public async Task ContinuationsCannotMoveAcrossDevicesOrWorkspaceAbaTransitions()
    {
        await using SecurityHost host = await SecurityHost.StartAsync();
        using HttpClient first = host.CreateClient();
        await AccountSessionLifecycleTests.SignInAsync(host, first);
        Guid organizationA = await CreateOrganizationForCurrentUserAsync(first, "realtime-cursor-workspace-a");
        JsonElement firstA = await StateAsync(first);
        string firstCursor = firstA.GetProperty("cursor").GetString()!;
        using HttpClient otherDevice = await host.SignInAsync();
        using (HttpResponseMessage select = await PostAsync(otherDevice, "/api/v1/workspace/select", new { organizationId = organizationA }))
            select.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await AssertResetAsync(otherDevice, firstCursor);

        await CreateOrganizationForCurrentUserAsync(first, "realtime-cursor-workspace-b");
        JsonElement stateB = await StateAsync(first);
        using (HttpResponseMessage select = await PostAsync(first, "/api/v1/workspace/select", new { organizationId = organizationA }))
            select.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        JsonElement secondA = await StateAsync(first);
        secondA.GetProperty("generation").GetGuid().ShouldNotBe(firstA.GetProperty("generation").GetGuid());
        await AssertResetAsync(first, firstCursor);
        await AssertResetAsync(first, stateB.GetProperty("cursor").GetString()!);
        (await first.GetAsync("/api/v1/realtime/history?cursor=" + secondA.GetProperty("cursor").GetString())).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [TestMethod]
    public async Task RevokedCookieCannotUsePreviouslyIssuedStateOrHistory()
    {
        await using SecurityHost host = await SecurityHost.StartAsync();
        using HttpClient browser = host.CreateClient();
        var session = await AccountSessionLifecycleTests.SignInAsync(host, browser);
        await CreateOrganizationForCurrentUserAsync(browser, "realtime-revoked-cookie");
        JsonElement state = await StateAsync(browser);
        await using NpgsqlConnection connection = new(host.OwnerConnection);
        await connection.OpenAsync();
        await using (NpgsqlCommand revoke = new("UPDATE identity.account_sessions SET \"RevokedAt\" = CURRENT_TIMESTAMP WHERE \"SessionHash\" = @hash", connection))
        {
            revoke.Parameters.AddWithValue("hash", session.Hash);
            (await revoke.ExecuteNonQueryAsync()).ShouldBe(1);
        }
        (await browser.GetAsync("/api/v1/realtime/history?cursor=" + state.GetProperty("cursor").GetString())).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await browser.GetAsync("/api/v1/realtime/state")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private static async Task<JsonElement> StateAsync(HttpClient browser)
    {
        using HttpResponseMessage response = await browser.GetAsync("/api/v1/realtime/state");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task AssertResetAsync(HttpClient browser, string cursor)
    {
        using HttpResponseMessage response = await browser.GetAsync("/api/v1/realtime/history?cursor=" + Uri.EscapeDataString(cursor));
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync());
        JsonElement problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("title").GetString().ShouldBe("realtime_reset_required");
        problem.TryGetProperty("items", out _).ShouldBeFalse();
    }
}

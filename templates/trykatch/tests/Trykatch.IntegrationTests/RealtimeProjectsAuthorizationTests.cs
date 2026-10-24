using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Trykatch.Api.Realtime;
using Trykatch.Modules;
using static Trykatch.IntegrationTests.AccountSecurityStepUpTests;
using static Trykatch.IntegrationTests.WorkspaceSessionLifecycleTests;

namespace Trykatch.IntegrationTests;

// Actual installed Projects policy, PostgreSQL and HTTP/WebSocket protocol; TestServer, not OS replicas.
[TestClass, TestCategory("Integration")]
public sealed class RealtimeProjectsAuthorizationTests
{
    private static readonly ModuleRealtimePolicyRef Policy = new("projects", "projects.read", 1);

    [TestMethod]
    public async Task AtomicReplacementAcceptsCurrentSelectorsAndRejectsForeignOrMalformedSelectors()
    {
        await using SecurityHost host = await StartAsync();
        using HttpClient browser = host.CreateClient();
        var session = await AccountSessionLifecycleTests.SignInAsync(host, browser);
        Guid actor = Guid.Parse(session.Ticket.Principal.FindFirstValue(ClaimTypes.NameIdentifier)!);
        Guid organization = await CreateOrganizationForCurrentUserAsync(browser, "realtime-selectors-a");
        Guid project = await CreateProjectAsync(browser);
        await CreateOrganizationForCurrentUserAsync(browser, "realtime-selectors-b");
        Guid foreignProject = await CreateProjectAsync(browser);
        string workspace = await RealtimeSocketAuthorityTests.SelectCookieAsync(browser, organization);
        await using HubConnection socket = RealtimeSocketAuthorityTests.Connection(host, session.Cookie + "; " + workspace);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
        await socket.StartAsync(deadline.Token);
        ModuleRealtimeAudience[] accepted =
        [
            new(Policy, ModuleRealtimeAudienceKind.Organization),
            new(Policy, ModuleRealtimeAudienceKind.User, actor),
            new(Policy, ModuleRealtimeAudienceKind.Resource, Resource: new("projects", project))
        ];
        await socket.InvokeAsync("ReplaceSubscriptions", accepted, deadline.Token);
        RealtimeConnectionEntry entry = host.Services.GetRequiredService<RealtimeConnectionRegistry>().Snapshot().ShouldHaveSingleItem();
        entry.CaptureSubscriptions().Items.ShouldBe(accepted);
        ModuleRealtimeAudience[] denied =
        [
            new(Policy, ModuleRealtimeAudienceKind.User, Guid.NewGuid()),
            new(Policy, ModuleRealtimeAudienceKind.Resource, Resource: new("projects", foreignProject)),
            new(Policy, ModuleRealtimeAudienceKind.Resource, Resource: new("projects", Guid.NewGuid())),
            new(Policy, ModuleRealtimeAudienceKind.Organization, Resource: new("projects", foreignProject)),
            new(Policy, ModuleRealtimeAudienceKind.Resource),
            new(Policy, ModuleRealtimeAudienceKind.Organization, actor),
            new(Policy with { PolicyVersion = 2 }, ModuleRealtimeAudienceKind.Organization)
        ];
        foreach (ModuleRealtimeAudience invalid in denied)
        {
            await Should.ThrowAsync<HubException>(() => socket.InvokeAsync("ReplaceSubscriptions",
                new[] { accepted[0], invalid }, deadline.Token));
            entry.CaptureSubscriptions().Items.ShouldBe(accepted, "a denied replacement must leave the entire previous set intact");
        }
        await Should.ThrowAsync<HubException>(() => socket.InvokeAsync("ReplaceSubscriptions",
            new[] { accepted[0], accepted[0] }, deadline.Token));
        entry.CaptureSubscriptions().Items.ShouldBe(accepted);
    }

    [TestMethod]
    public async Task RealCommittedProjectsEventWakesTheSocketAndRemainsRecoverableThroughHttp()
    {
        await using SecurityHost host = await StartAsync();
        using HttpClient browser = host.CreateClient();
        var session = await AccountSessionLifecycleTests.SignInAsync(host, browser);
        Guid organization = await CreateOrganizationForCurrentUserAsync(browser, "realtime-live-project");
        string workspace = await RealtimeSocketAuthorityTests.SelectCookieAsync(browser, organization);
        await using HubConnection socket = RealtimeSocketAuthorityTests.Connection(host, session.Cookie + "; " + workspace);
        TaskCompletionSource<ModuleRealtimeNotification> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using IDisposable handler = socket.On<ModuleRealtimeNotification>("invalidate", item => received.TrySetResult(item));
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));
        await socket.StartAsync(deadline.Token);
        await socket.InvokeAsync("ReplaceSubscriptions", new[] { new ModuleRealtimeAudience(Policy, ModuleRealtimeAudienceKind.Organization) }, deadline.Token);
        Guid project = await CreateProjectAsync(browser);
        JsonElement page = await AwaitHistoryAsync(browser, deadline.Token);
        await host.Services.GetRequiredService<RealtimeConnectionOperations>().RunOnceAsync(deadline.Token);
        ModuleRealtimeNotification notification = await received.Task.WaitAsync(deadline.Token);
        notification.ModuleId.ShouldBe("projects");
        notification.EventKey.ShouldBe("projects.created");
        notification.SchemaVersion.ShouldBe(1);
        notification.Audience.Policy.ShouldBe(Policy);
        notification.Audience.Resource.ShouldBe(new("projects", project));
        page.GetProperty("items")[0].GetProperty("eventId").GetGuid().ShouldBe(notification.EventId);
        page.GetProperty("items")[0].TryGetProperty("body", out _).ShouldBeFalse();
    }

    [TestMethod]
    public async Task ConnectedSubscriptionsLoseAuthorityWhenCurrentProjectsReadPermissionIsRemoved()
    {
        await using SecurityHost host = await StartAsync();
        using HttpClient browser = host.CreateClient();
        var session = await AccountSessionLifecycleTests.SignInAsync(host, browser);
        Guid organization = await CreateOrganizationForCurrentUserAsync(browser, "realtime-live-permission");
        string workspace = await RealtimeSocketAuthorityTests.SelectCookieAsync(browser, organization);
        await using HubConnection socket = RealtimeSocketAuthorityTests.Connection(host, session.Cookie + "; " + workspace);
        TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        socket.Closed += _ => { closed.TrySetResult(); return Task.CompletedTask; };
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
        await socket.StartAsync(deadline.Token);
        await socket.InvokeAsync("ReplaceSubscriptions", new[] { new ModuleRealtimeAudience(Policy, ModuleRealtimeAudienceKind.Organization) }, deadline.Token);
        await ExecuteOwnerAsync(host, """
            DELETE FROM platform.role_permissions AS permission
            USING platform.roles AS role
            WHERE permission."RoleId" = role."Id" AND role."OrganizationId" = @id
              AND permission."Permission" = 'projects.read'
            """, organization);
        await host.Services.GetRequiredService<RealtimeConnectionOperations>().RunOnceAsync(deadline.Token);
        await closed.Task.WaitAsync(deadline.Token);
        host.Services.GetRequiredService<RealtimeConnectionRegistry>().Count.ShouldBe(0);
        using HttpResponseMessage history = await browser.GetAsync("/api/v1/realtime/history", deadline.Token);
        history.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await history.Content.ReadFromJsonAsync<JsonElement>(deadline.Token)).GetProperty("items").GetArrayLength().ShouldBe(0);
    }

    [TestMethod]
    public async Task RetainedHistoryRechecksActualProjectsResourceAndAdvancesItsFilteredCursor()
    {
        await using SecurityHost host = await StartAsync();
        using HttpClient browser = await host.SignInAsync();
        await CreateOrganizationForCurrentUserAsync(browser, "realtime-retained-resource");
        Guid project = await CreateProjectAsync(browser);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));
        JsonElement visible = await AwaitHistoryAsync(browser, deadline.Token);
        string visibleCursor = visible.GetProperty("cursor").GetString()!;
        await ExecuteOwnerAsync(host, "DELETE FROM app.projects WHERE \"Id\" = @id", project);
        using HttpResponseMessage response = await browser.GetAsync("/api/v1/realtime/history", deadline.Token);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement filtered = await response.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
        filtered.GetProperty("items").GetArrayLength().ShouldBe(0);
        filtered.GetProperty("hasMore").GetBoolean().ShouldBeFalse();
        // Protected cursors have randomized ciphertext; both must nevertheless resume after the scanned row.
        foreach (string cursor in new[] { visibleCursor, filtered.GetProperty("cursor").GetString()! })
        {
            using HttpResponseMessage next = await browser.GetAsync("/api/v1/realtime/history?cursor=" + Uri.EscapeDataString(cursor), deadline.Token);
            next.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await next.Content.ReadFromJsonAsync<JsonElement>(deadline.Token)).GetProperty("items").GetArrayLength().ShouldBe(0);
        }
    }

    private static async Task<SecurityHost> StartAsync()
    {
        SecurityHost host = await SecurityHost.StartAsync();
        await host.RestartAsync(new Dictionary<string, string?>
        {
            ["Realtime:Mode"] = "LocalOnly", ["Realtime:PollInterval"] = "00:00:30"
        }, "Development");
        return host;
    }

    private static async Task<Guid> CreateProjectAsync(HttpClient browser)
    {
        using HttpResponseMessage response = await PostAsync(browser, "/api/v1/projects", new { name = "Realtime project", description = "Reference only" });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> AwaitHistoryAsync(HttpClient browser, CancellationToken cancellationToken)
    {
        while (true)
        {
            using HttpResponseMessage response = await browser.GetAsync("/api/v1/realtime/history", cancellationToken);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(cancellationToken));
            JsonElement page = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            if (page.GetProperty("items").GetArrayLength() > 0) return page;
            await Task.Delay(50, cancellationToken);
        }
    }

    private static async Task ExecuteOwnerAsync(SecurityHost host, string sql, Guid id)
    {
        await using NpgsqlConnection connection = new(host.OwnerConnection);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(sql, connection);
        command.Parameters.AddWithValue("id", id);
        (await command.ExecuteNonQueryAsync()).ShouldBeGreaterThan(0);
    }
}

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Trykatch.Api.Realtime;
using Trykatch.Modules;
using static Trykatch.IntegrationTests.AccountSecurityStepUpTests;
using static Trykatch.IntegrationTests.WorkspaceSessionLifecycleTests;

namespace Trykatch.IntegrationTests;

public sealed partial class RealtimeProjectsAuthorizationTests
{
    [TestMethod]
    public async Task ProjectsReadFeatureFiltersHistoryAndRevokesLiveSubscriptionsWithoutStoppingPublication()
    {
        await using SecurityHost host = await StartAsync();
        using HttpClient browser = host.CreateClient();
        var session = await AccountSessionLifecycleTests.SignInAsync(host, browser);
        Guid organization = await CreateOrganizationForCurrentUserAsync(browser, "realtime-project-feature");
        await SetRealtimeProjectsReadFeatureAsync(host.OwnerConnection, enabled: true);
        string workspace = await RealtimeSocketAuthorityTests.SelectCookieAsync(browser, organization);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));
        await using HubConnection socket = RealtimeSocketAuthorityTests.Connection(host, session.Cookie + "; " + workspace);
        TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ConcurrentQueue<ModuleRealtimeNotification> notifications = new();
        socket.Closed += _ => { closed.TrySetResult(); return Task.CompletedTask; };
        using IDisposable notificationHandler = socket.On<ModuleRealtimeNotification>("invalidate", item => notifications.Enqueue(item));
        await socket.StartAsync(deadline.Token);
        await socket.InvokeAsync("ReplaceSubscriptions",
            new[] { new ModuleRealtimeAudience(Policy, ModuleRealtimeAudienceKind.Organization) }, deadline.Token);
        host.Services.GetRequiredService<RealtimeConnectionRegistry>().Count.ShouldBe(1);

        Guid firstProject = await CreateProjectAsync(browser);
        JsonElement initialHistory = await AwaitHistoryAsync(browser, deadline.Token);
        initialHistory.GetProperty("items").EnumerateArray().ShouldContain(item =>
            item.GetProperty("audience").GetProperty("resource").GetProperty("resourceId").GetGuid() == firstProject);
        await host.Services.GetRequiredService<RealtimeConnectionOperations>().RunOnceAsync(deadline.Token);
        ModuleRealtimeNotification firstNotification = await WaitForRealtimeProjectNotificationAsync(notifications, deadline.Token);
        firstNotification.EventKey.ShouldBe("projects.created");
        firstNotification.Audience.Policy.ShouldBe(Policy);
        (await CountRealtimeProjectsFeatureHistoryAsync(host.OwnerConnection, organization)).ShouldBe(1);

        await SetRealtimeProjectsReadFeatureAsync(host.OwnerConnection, enabled: false);
        Guid secondProject = await CreateProjectAsync(browser);
        secondProject.ShouldNotBe(firstProject);
        await WaitForRealtimeProjectsFeatureHistoryCountAsync(host.OwnerConnection, organization, expected: 2, deadline.Token);
        await host.Services.GetRequiredService<RealtimeConnectionOperations>().RunOnceAsync(deadline.Token);
        await closed.Task.WaitAsync(deadline.Token);
        host.Services.GetRequiredService<RealtimeConnectionRegistry>().Count.ShouldBe(0);
        notifications.Count.ShouldBe(1, "no invalidation may be delivered after the read feature is disabled");

        using (HttpResponseMessage disabledHistoryResponse = await browser.GetAsync("/api/v1/realtime/history", deadline.Token))
        {
            disabledHistoryResponse.StatusCode.ShouldBe(HttpStatusCode.OK, await disabledHistoryResponse.Content.ReadAsStringAsync(deadline.Token));
            JsonElement disabledHistory = (await disabledHistoryResponse.Content.ReadFromJsonAsync<JsonElement>(deadline.Token));
            disabledHistory.GetProperty("items").GetArrayLength().ShouldBe(0);
        }
        (await CountRealtimeProjectsFeatureHistoryAsync(host.OwnerConnection, organization)).ShouldBe(2,
            "disabling reads must filter the retained history without deleting committed events");

        await SetRealtimeProjectsReadFeatureAsync(host.OwnerConnection, enabled: true);
        using (HttpResponseMessage enabledHistoryResponse = await browser.GetAsync("/api/v1/realtime/history", deadline.Token))
        {
            enabledHistoryResponse.StatusCode.ShouldBe(HttpStatusCode.OK, await enabledHistoryResponse.Content.ReadAsStringAsync(deadline.Token));
            JsonElement enabledHistory = (await enabledHistoryResponse.Content.ReadFromJsonAsync<JsonElement>(deadline.Token));
            enabledHistory.GetProperty("items").GetArrayLength().ShouldBe(2);
            enabledHistory.GetProperty("items").EnumerateArray().ShouldContain(item =>
                item.GetProperty("audience").GetProperty("resource").GetProperty("resourceId").GetGuid() == secondProject);
        }

        socket.State.ShouldBe(HubConnectionState.Disconnected);
        using HttpClient renewedBrowser = host.CreateClient();
        var renewedSession = await AccountSessionLifecycleTests.SignInAsync(host, renewedBrowser);
        string renewedWorkspace = await RealtimeSocketAuthorityTests.SelectCookieAsync(renewedBrowser, organization);
        await using HubConnection renewedSocket = RealtimeSocketAuthorityTests.Connection(host, renewedSession.Cookie + "; " + renewedWorkspace);
        await renewedSocket.StartAsync(deadline.Token);
        await renewedSocket.InvokeAsync("ReplaceSubscriptions",
            new[] { new ModuleRealtimeAudience(Policy, ModuleRealtimeAudienceKind.Organization) }, deadline.Token);
        host.Services.GetRequiredService<RealtimeConnectionRegistry>().Count.ShouldBe(1);
        notifications.Count.ShouldBe(1, "the original disconnected socket must stay disconnected after the feature is re-enabled");
    }

    private static async Task SetRealtimeProjectsReadFeatureAsync(string ownerConnection, bool enabled)
    {
        await using NpgsqlConnection connection = new(ownerConnection);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new("""
            INSERT INTO platform.feature_environment_controls
                ("Environment", "FeatureKey", "DefaultEnabled", "ForceDisabled", "Version", "UpdatedAtUtc")
            VALUES ('development', 'projects.reads', @enabled, false, @version, CURRENT_TIMESTAMP)
            ON CONFLICT ("Environment", "FeatureKey") DO UPDATE
            SET "DefaultEnabled" = EXCLUDED."DefaultEnabled", "ForceDisabled" = false,
                "Version" = EXCLUDED."Version", "UpdatedAtUtc" = CURRENT_TIMESTAMP
            """, connection);
        command.Parameters.AddWithValue("enabled", enabled);
        command.Parameters.AddWithValue("version", Guid.NewGuid());
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountRealtimeProjectsFeatureHistoryAsync(string ownerConnection, Guid organization)
    {
        await using NpgsqlConnection connection = new(ownerConnection);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new("""
            SELECT count(*) FROM app.realtime_history
            WHERE "OrganizationId" = @organization AND "ModuleId" = 'projects' AND "EventKey" = 'projects.created'
            """, connection);
        command.Parameters.AddWithValue("organization", organization);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task WaitForRealtimeProjectsFeatureHistoryCountAsync(
        string ownerConnection, Guid organization, long expected, CancellationToken cancellationToken)
    {
        while (await CountRealtimeProjectsFeatureHistoryAsync(ownerConnection, organization) < expected)
            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
        (await CountRealtimeProjectsFeatureHistoryAsync(ownerConnection, organization)).ShouldBe(expected);
    }

    private static async Task<ModuleRealtimeNotification> WaitForRealtimeProjectNotificationAsync(
        ConcurrentQueue<ModuleRealtimeNotification> notifications, CancellationToken cancellationToken)
    {
        ModuleRealtimeNotification? notification;
        while (!notifications.TryPeek(out notification))
            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
        return notification!;
    }
}

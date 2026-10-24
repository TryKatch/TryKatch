using System.Net;
using System.Net.WebSockets;
using System.Security.Claims;
using Microsoft.AspNetCore.Http.Connections;
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

// Real application WebSocket protocol and PostgreSQL authority, hosted in TestServer; not OS-replica evidence.
[TestClass, TestCategory("Integration")]
public sealed class RealtimeSocketAuthorityTests
{
    [TestMethod]
    public async Task SocketCapturesOnlyCurrentBindingAndRejectsUnknownMethodsAndPolicies()
    {
        await using SecurityHost host = await StartAsync();
        using HttpClient browser = host.CreateClient();
        var session = await AccountSessionLifecycleTests.SignInAsync(host, browser);
        Guid organization = await CreateOrganizationForCurrentUserAsync(browser, "realtime-socket-valid");
        string workspace = await SelectCookieAsync(browser, organization);
        await using HubConnection socket = Connection(host, session.Cookie + "; " + workspace);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        await socket.StartAsync(deadline.Token);
        await socket.InvokeAsync("ReplaceSubscriptions", Array.Empty<ModuleRealtimeAudience>(), deadline.Token);
        RealtimeConnectionRegistry registry = host.Services.GetRequiredService<RealtimeConnectionRegistry>();
        RealtimeConnectionEntry entry = registry.Snapshot().ShouldHaveSingleItem();
        entry.HasTransport.ShouldBeTrue();
        entry.Binding.OrganizationId.ShouldBe(organization);
        entry.Binding.Proof.SessionHash.ShouldBe(session.Hash);
        await Should.ThrowAsync<HubException>(() => socket.InvokeAsync("AddToGroup", "arbitrary", deadline.Token));
        await Should.ThrowAsync<HubException>(() => socket.InvokeAsync("ReplaceSubscriptions", new[]
        {
            new ModuleRealtimeAudience(new("uninstalled", "unknown", 1), ModuleRealtimeAudienceKind.Organization)
        }, deadline.Token));
        entry.CaptureSubscriptions().Items.ShouldBeEmpty();
    }

    [TestMethod]
    [DataRow("revocation")]
    [DataRow("stamp")]
    [DataRow("ticket-expiry")]
    [DataRow("membership")]
    [DataRow("organization")]
    public async Task ConnectedSocketIsRemovedAndAbortedAfterAuthorityChanges(string change)
    {
        await using SecurityHost host = await StartAsync();
        using HttpClient browser = host.CreateClient();
        var session = await AccountSessionLifecycleTests.SignInAsync(host, browser);
        Guid organization = await CreateOrganizationForCurrentUserAsync(browser, "realtime-socket-revoke");
        string workspace = await SelectCookieAsync(browser, organization);
        await using HubConnection socket = Connection(host, session.Cookie + "; " + workspace);
        TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        socket.Closed += _ => { closed.TrySetResult(); return Task.CompletedTask; };
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        await socket.StartAsync(deadline.Token);
        await socket.InvokeAsync("ReplaceSubscriptions", Array.Empty<ModuleRealtimeAudience>(), deadline.Token);
        host.Services.GetRequiredService<RealtimeConnectionRegistry>().Count.ShouldBe(1);
        if (change == "ticket-expiry")
            host.Clock.Advance(session.Ticket.Properties.ExpiresUtc!.Value - host.Clock.GetUtcNow() + TimeSpan.FromSeconds(1));
        else
        {
            string sql = change switch
            {
                "revocation" => "UPDATE identity.account_sessions SET \"RevokedAt\" = CURRENT_TIMESTAMP WHERE \"SessionHash\" = @hash",
                "stamp" => "UPDATE identity.\"AspNetUsers\" SET \"SecurityStamp\" = 'changed-live-stamp' WHERE \"Id\" = @actor",
                "membership" => "UPDATE platform.memberships SET \"Status\" = 2 WHERE \"OrganizationId\" = @organization AND \"UserId\" = @actor",
                _ => "UPDATE platform.organizations SET \"IsActive\" = false WHERE \"Id\" = @organization"
            };
            await using NpgsqlConnection connection = new(host.OwnerConnection);
            await connection.OpenAsync(deadline.Token);
            await using NpgsqlCommand command = new(sql, connection);
            command.Parameters.AddWithValue("hash", session.Hash);
            command.Parameters.AddWithValue("actor", Guid.Parse(session.Ticket.Principal.FindFirstValue(ClaimTypes.NameIdentifier)!));
            command.Parameters.AddWithValue("organization", organization);
            (await command.ExecuteNonQueryAsync(deadline.Token)).ShouldBe(1);
        }
        await closed.Task.WaitAsync(deadline.Token);
        host.Services.GetRequiredService<RealtimeConnectionRegistry>().Count.ShouldBe(0);
    }

    [TestMethod]
    public async Task WorkspaceAbaCannotReviveAnAlreadyConnectedSocket()
    {
        await using SecurityHost host = await StartAsync();
        using HttpClient browser = host.CreateClient();
        var session = await AccountSessionLifecycleTests.SignInAsync(host, browser);
        Guid organization = await CreateOrganizationForCurrentUserAsync(browser, "realtime-socket-aba-a");
        string workspace = await SelectCookieAsync(browser, organization);
        await using HubConnection socket = Connection(host, session.Cookie + "; " + workspace);
        TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        socket.Closed += _ => { closed.TrySetResult(); return Task.CompletedTask; };
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        await socket.StartAsync(deadline.Token);
        await socket.InvokeAsync("ReplaceSubscriptions", Array.Empty<ModuleRealtimeAudience>(), deadline.Token);
        await CreateOrganizationForCurrentUserAsync(browser, "realtime-socket-aba-b");
        await SelectCookieAsync(browser, organization);
        await closed.Task.WaitAsync(deadline.Token);
        host.Services.GetRequiredService<RealtimeConnectionRegistry>().Count.ShouldBe(0);
    }

    private static async Task<SecurityHost> StartAsync()
    {
        SecurityHost host = await SecurityHost.StartAsync();
        await host.RestartAsync(new Dictionary<string, string?>
        {
            ["Realtime:Mode"] = "LocalOnly", ["Realtime:PollInterval"] = "00:00:00.1"
        }, "Development");
        return host;
    }

    internal static HubConnection Connection(SecurityHost host, string cookies) => new HubConnectionBuilder()
        .WithUrl("https://localhost/api/v1/realtime/hub", options =>
        {
            options.Transports = HttpTransportType.WebSockets;
            options.SkipNegotiation = true;
            options.WebSocketFactory = async (context, cancellationToken) =>
            {
                var client = host.CreateWebSocketClient();
                client.ConfigureRequest = request =>
                {
                    request.Headers.Cookie = cookies;
                    request.Headers.Origin = "https://localhost";
                };
                return await client.ConnectAsync(context.Uri, cancellationToken);
            };
        }).Build();

    internal static async Task<string> SelectCookieAsync(HttpClient browser, Guid organization)
    {
        using HttpResponseMessage select = await PostAsync(browser, "/api/v1/workspace/select", new { organizationId = organization });
        select.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        return select.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith("__Host-trykatch-workspace=", StringComparison.Ordinal)).Split(';')[0];
    }
}

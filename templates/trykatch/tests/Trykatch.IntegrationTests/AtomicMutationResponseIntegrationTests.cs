using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Trykatch.Modules.AspNetCore;

namespace Trykatch.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
public sealed class AtomicMutationResponseIntegrationTests
{
    [TestMethod]
    public async Task ExecutedCreatedResultIsCapturedBeforeCommitAndNetworkPublication()
    {
        TaskCompletionSource finalizeReached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource allowCommit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        StagedMutationResponse? captured = null;
        using IHost host = await Host.CreateDefaultBuilder()
            .ConfigureWebHost(webHost => webHost.UseTestServer().Configure(app => app.Run(context =>
                AtomicMutationResponse.ExecuteAsync(context, 1024, async http =>
                {
                    http.Features.Set<IModuleMutationResponseCaptureFeature>(new Capture(response => captured = response));
                    await Results.Created("/api/v1/projects/server-id", new { id = "server-id" }).ExecuteAsync(http);
                }, async _ =>
                {
                    finalizeReached.SetResult();
                    await allowCommit.Task;
                }))))
            .StartAsync();
        using HttpClient client = host.GetTestClient();
        Task<HttpResponseMessage> request = client.GetAsync("/", HttpCompletionOption.ResponseHeadersRead);
        try
        {
            await finalizeReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
            request.IsCompleted.ShouldBeFalse();
            captured.ShouldNotBeNull();
            captured.StatusCode.ShouldBe(201);
            captured.Location.ShouldBe("/api/v1/projects/server-id");
            System.Text.Encoding.UTF8.GetString(captured.Body).ShouldBe("{\"id\":\"server-id\"}");
        }
        finally
        {
            allowCommit.TrySetResult();
        }
        using HttpResponseMessage response = await request;
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.Created);
        response.Headers.Location!.OriginalString.ShouldBe(captured!.Location);
        (await response.Content.ReadAsByteArrayAsync()).ShouldBe(captured.Body);
    }

    [TestMethod]
    public async Task TestServerCannotObserveStartedResponseBeforeCommit()
    {
        TaskCompletionSource commitReached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource allowCommit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using IHost host = await Host.CreateDefaultBuilder()
            .ConfigureWebHost(webHost => webHost.UseTestServer().Configure(app => app.Run(context =>
                AtomicMutationResponse.ExecuteAsync(
                    context,
                    1024,
                    async responseContext =>
                    {
                        await responseContext.Response.StartAsync();
                        await responseContext.Response.WriteAsync("committed");
                        await responseContext.Response.CompleteAsync();
                    },
                    async _ =>
                    {
                        context.Response.HasStarted.ShouldBeFalse();
                        commitReached.SetResult();
                        await allowCommit.Task;
                    }))))
            .StartAsync();
        using HttpClient client = host.GetTestClient();

        Task<HttpResponseMessage> request = client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "/"),
            HttpCompletionOption.ResponseHeadersRead);
        await commitReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        request.IsCompleted.ShouldBeFalse();

        allowCommit.SetResult();
        using HttpResponseMessage response = await request;
        (await response.Content.ReadAsStringAsync()).ShouldBe("committed");
    }

    private sealed class Capture(Action<StagedMutationResponse> capture) : IModuleMutationResponseCaptureFeature
    {
        public int MaximumResponseBytes => 1024;
        public bool RequiresRollback => false;
        public Task CaptureAsync(StagedMutationResponse response, CancellationToken cancellationToken)
        {
            capture(response);
            return Task.CompletedTask;
        }
    }
}

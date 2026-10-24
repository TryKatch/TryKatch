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
    public async Task NativePublicationCannotChangeCommittedReceiptMetadata()
    {
        bool committed = false;
        StagedMutationResponse? captured = null;
        using IHost host = await Host.CreateDefaultBuilder()
            .ConfigureWebHost(webHost => webHost.UseTestServer().Configure(app =>
            {
                app.Use((context, next) =>
                {
                    // Deliberately outside application callback staging, like a hosting wrapper.
                    context.Response.OnStarting(() =>
                    {
                        committed.ShouldBeTrue();
                        context.Response.Headers.Location = "/api/v1/projects/too-late";
                        return Task.CompletedTask;
                    });
                    return next();
                });
                app.Use((context, next) => AtomicMutationResponse.StageStartingCallbacksAsync(context, _ => next()));
                app.Run(context => AtomicMutationResponse.ExecuteAsync(context, 1024, async http =>
                {
                    http.Features.Set<IModuleMutationResponseCaptureFeature>(new Capture(response => captured = response));
                    await Results.Created("/api/v1/projects/committed", new { id = "server-id" }).ExecuteAsync(http);
                }, _ =>
                {
                    committed = true;
                    return Task.CompletedTask;
                }));
            }))
            .StartAsync();
        using HttpClient client = host.GetTestClient();
        await Should.ThrowAsync<InvalidOperationException>(() => client.GetAsync("/"));
        committed.ShouldBeTrue();
        captured!.Location.ShouldBe("/api/v1/projects/committed");
    }

    [TestMethod]
    public async Task EarlyApplicationCallbacksRunInReverseOrderBeforeReceiptCapture()
    {
        List<string> calls = [];
        StagedMutationResponse? captured = null;
        using IHost host = await Host.CreateDefaultBuilder()
            .ConfigureWebHost(webHost => webHost.UseTestServer().Configure(app =>
            {
                app.Use((context, next) => AtomicMutationResponse.StageStartingCallbacksAsync(context, _ => next()));
                app.Use((context, next) =>
                {
                    context.Response.OnStarting(() =>
                    {
                        calls.Add("outer");
                        context.Response.Headers.Location = "/api/v1/projects/outer-final";
                        return Task.CompletedTask;
                    });
                    return next();
                });
                app.Run(context => AtomicMutationResponse.ExecuteAsync(context, 1024, async http =>
                {
                    http.Features.Set<IModuleMutationResponseCaptureFeature>(new Capture(response => captured = response));
                    http.Response.OnStarting(async () =>
                    {
                        await Task.Yield();
                        calls.Add("inner");
                        http.Response.Headers.Location = "/api/v1/projects/inner";
                    });
                    await Results.Created("/api/v1/projects/original", new { id = "server-id" }).ExecuteAsync(http);
                }, _ =>
                {
                    calls.ShouldBe(["inner", "outer"]);
                    context.Response.HasStarted.ShouldBeFalse();
                    captured!.Location.ShouldBe("/api/v1/projects/outer-final");
                    return Task.CompletedTask;
                }));
            }))
            .StartAsync();
        using HttpClient client = host.GetTestClient();
        using HttpResponseMessage response = await client.GetAsync("/");
        calls.ShouldBe(["inner", "outer"]);
        response.Headers.Location!.OriginalString.ShouldBe(captured!.Location);
    }

    [TestMethod]
    public async Task StartingCallbacksFinalizeReceiptMetadataBeforeCommitAndRunOnlyOnce()
    {
        StagedMutationResponse? captured = null;
        int callbacks = 0;
        bool committed = false;
        using IHost host = await Host.CreateDefaultBuilder()
            .ConfigureWebHost(webHost => webHost.UseTestServer().Configure(app => app.Run(context =>
                AtomicMutationResponse.ExecuteAsync(context, 1024, async http =>
                {
                    http.Features.Set<IModuleMutationResponseCaptureFeature>(new Capture(response => captured = response));
                    http.Response.OnStarting(async () =>
                    {
                        await Task.Yield();
                        callbacks++;
                        http.Response.StatusCode = StatusCodes.Status202Accepted;
                        http.Response.ContentType = "application/vnd.trykatch+json";
                        http.Response.Headers.Location = "/api/v1/projects/final-id";
                    });
                    await Results.Created("/api/v1/projects/original-id", new { id = "server-id" }).ExecuteAsync(http);
                }, _ =>
                {
                    callbacks.ShouldBe(1, "response-start callbacks must finish before the owning transaction commits");
                    captured!.StatusCode.ShouldBe(StatusCodes.Status202Accepted);
                    committed = true;
                    return Task.CompletedTask;
                }))))
            .StartAsync();
        using HttpClient client = host.GetTestClient();
        using HttpResponseMessage response = await client.GetAsync("/");
        committed.ShouldBeTrue();
        callbacks.ShouldBe(1);
        ((int)response.StatusCode).ShouldBe(captured!.StatusCode);
        response.Content.Headers.ContentType!.ToString().ShouldBe(captured.ContentType);
        response.Headers.Location!.OriginalString.ShouldBe(captured.Location);
        (await response.Content.ReadAsByteArrayAsync()).ShouldBe(captured.Body);
    }

    [TestMethod]
    public async Task StartingCallbackFailureCannotCommitTheMutation()
    {
        bool committed = false;
        using IHost host = await Host.CreateDefaultBuilder()
            .ConfigureWebHost(webHost => webHost.UseTestServer().Configure(app => app.Run(context =>
                AtomicMutationResponse.ExecuteAsync(context, 1024, async http =>
                {
                    http.Features.Set<IModuleMutationResponseCaptureFeature>(new Capture(_ => { }));
                    http.Response.OnStarting(() => throw new InvalidOperationException("synthetic response-start failure"));
                    await Results.Created("/api/v1/projects/server-id", new { id = "server-id" }).ExecuteAsync(http);
                }, _ =>
                {
                    committed = true;
                    return Task.CompletedTask;
                }))))
            .StartAsync();
        using HttpClient client = host.GetTestClient();
        await Should.ThrowAsync<InvalidOperationException>(() => client.GetAsync("/"));
        committed.ShouldBeFalse("a failed response-start callback must run before transaction finalization");
    }

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

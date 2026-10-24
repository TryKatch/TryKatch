using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Shouldly;
using Trykatch.Modules.AspNetCore;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class AtomicMutationCaptureTests
{
    [TestMethod]
    public async Task LateEnrollmentCapturesFlushedResultBytesBeforeTransactionFinalization()
    {
        DefaultHttpContext context = new();
        await using MemoryStream client = new();
        context.Response.Body = client;
        List<string> order = [];
        StagedMutationResponse? captured = null;
        Capture capture = new(1024, response =>
        {
            captured = response;
            order.Add("capture");
            client.Length.ShouldBe(0);
            return Task.CompletedTask;
        });
        await AtomicMutationResponse.ExecuteAsync(context, 1024, async http =>
        {
            http.Features.Set<IModuleMutationResponseCaptureFeature>(capture);
            http.Response.StatusCode = 201;
            http.Response.ContentType = "application/json";
            http.Response.Headers.Location = "/api/v1/projects/server-created";
            http.Response.Headers.SetCookie = "never-persist-this";
            await http.Response.BodyWriter.WriteAsync("{\"id\":1}"u8.ToArray());
            await http.Response.CompleteAsync();
        }, _ =>
        {
            order.Add("finalize");
            client.Length.ShouldBe(0);
            captured.ShouldNotBeNull();
            return Task.CompletedTask;
        });

        order.ShouldBe(["capture", "finalize"]);
        captured.ShouldNotBeNull();
        captured.StatusCode.ShouldBe(201);
        captured.ContentType.ShouldBe("application/json");
        captured.Location.ShouldBe("/api/v1/projects/server-created");
        captured.Body.ShouldBe("{\"id\":1}"u8.ToArray());
        client.ToArray().ShouldBe(captured.Body);
    }

    [TestMethod]
    public async Task CaptureFailurePreventsFinalizationAndClientPublication()
    {
        DefaultHttpContext context = new();
        await using MemoryStream client = new();
        context.Response.Body = client;
        bool finalized = false;
        context.Features.Set<IModuleMutationResponseCaptureFeature>(new Capture(1024,
            _ => throw new InvalidOperationException("injected receipt failure")));

        await Should.ThrowAsync<InvalidOperationException>(() => AtomicMutationResponse.ExecuteAsync(context, 1024,
            http => http.Response.WriteAsync("created"), _ => { finalized = true; return Task.CompletedTask; }));

        finalized.ShouldBeFalse();
        client.Length.ShouldBe(0);
    }

    [TestMethod]
    public async Task ReceiptSpecificLimitFailsBeforeSnapshotAndFinalization()
    {
        DefaultHttpContext context = new();
        await using MemoryStream client = new();
        context.Response.Body = client;
        bool captured = false;
        bool finalized = false;
        context.Features.Set<IModuleMutationResponseCaptureFeature>(new Capture(4,
            _ => { captured = true; return Task.CompletedTask; }));

        await Should.ThrowAsync<InvalidOperationException>(() => AtomicMutationResponse.ExecuteAsync(context, 1024,
            http => http.Response.WriteAsync("created"), _ => { finalized = true; return Task.CompletedTask; }));

        captured.ShouldBeFalse();
        finalized.ShouldBeFalse();
        client.Length.ShouldBe(0);
    }

    [TestMethod]
    public async Task ExplicitStreamingCannotBeEnrolledInMutationCapture()
    {
        DefaultHttpContext context = new();
        await using MemoryStream client = new();
        context.Response.Body = client;
        bool finalized = false;
        context.Features.Set<IModuleMutationResponseCaptureFeature>(new Capture(1024, _ => Task.CompletedTask));

        await Should.ThrowAsync<NotSupportedException>(() => AtomicMutationResponse.ExecuteAsync(context, 1024,
            async http =>
            {
                http.Features.Get<IHttpResponseBodyFeature>()!.DisableBuffering();
                await http.Response.WriteAsync("streamed");
            }, _ => { finalized = true; return Task.CompletedTask; }));

        finalized.ShouldBeFalse();
        client.Length.ShouldBe(0);
    }

    [TestMethod]
    public async Task FileResponseCannotCommitOrPublishThroughMutationCapture()
    {
        string path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "file-content");
            DefaultHttpContext context = new();
            await using MemoryStream client = new();
            context.Response.Body = client;
            bool finalized = false;
            context.Features.Set<IModuleMutationResponseCaptureFeature>(new Capture(1024, _ => Task.CompletedTask));

            await Should.ThrowAsync<NotSupportedException>(() => AtomicMutationResponse.ExecuteAsync(context, 1024,
                http => http.Features.Get<IHttpResponseBodyFeature>()!.SendFileAsync(path, 0, null),
                _ => { finalized = true; return Task.CompletedTask; }));

            finalized.ShouldBeFalse();
            client.Length.ShouldBe(0);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task CapturedBytesCannotBeChangedDuringCaptureOrFinalization()
    {
        DefaultHttpContext context = new();
        await using MemoryStream client = new();
        context.Response.Body = client;
        bool finalized = false;
        context.Features.Set<IModuleMutationResponseCaptureFeature>(new Capture(1024,
            _ => context.Response.WriteAsync("late")));

        await Should.ThrowAsync<InvalidOperationException>(() => AtomicMutationResponse.ExecuteAsync(context, 1024,
            http => http.Response.WriteAsync("created"), _ => { finalized = true; return Task.CompletedTask; }));

        finalized.ShouldBeFalse();
        client.Length.ShouldBe(0);
    }

    [TestMethod]
    public async Task CancellationDuringCapturePreventsTransactionFinalization()
    {
        DefaultHttpContext context = new();
        await using MemoryStream client = new();
        using CancellationTokenSource cancellation = new();
        context.Response.Body = client;
        context.RequestAborted = cancellation.Token;
        bool finalized = false;
        context.Features.Set<IModuleMutationResponseCaptureFeature>(new Capture(1024, _ =>
        {
            cancellation.Cancel();
            return Task.CompletedTask;
        }));

        await Should.ThrowAsync<OperationCanceledException>(() => AtomicMutationResponse.ExecuteAsync(context, 1024,
            http => http.Response.WriteAsync("created"), _ => { finalized = true; return Task.CompletedTask; }));

        finalized.ShouldBeFalse();
        client.Length.ShouldBe(0);
    }

    private sealed class Capture(int maximumResponseBytes, Func<StagedMutationResponse, Task> capture)
        : IModuleMutationResponseCaptureFeature
    {
        public int MaximumResponseBytes => maximumResponseBytes;
        public bool RequiresRollback => false;
        public Task CaptureAsync(StagedMutationResponse response, CancellationToken cancellationToken) => capture(response);
    }
}

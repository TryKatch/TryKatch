using System.Text;
using Microsoft.AspNetCore.Http;
using Shouldly;
using Trykatch.Modules.AspNetCore;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class AtomicMutationResponseTests
{
    [TestMethod]
    public async Task SuccessIsNotPublishedUntilCommitCompletes()
    {
        DefaultHttpContext context = new();
        await using MemoryStream client = new();
        context.Response.Body = client;
        bool committed = false;

        await AtomicMutationResponse.ExecuteAsync(
            context,
            1024,
            async httpContext =>
            {
                await httpContext.Response.WriteAsync("success");
                client.Length.ShouldBe(0);
            },
            _ =>
            {
                context.Response.HasStarted.ShouldBeFalse();
                client.Length.ShouldBe(0);
                committed = true;
                return Task.CompletedTask;
            });

        committed.ShouldBeTrue();
        Encoding.UTF8.GetString(client.ToArray()).ShouldBe("success");
    }

    [TestMethod]
    public async Task CommitFailurePublishesNoSuccessBody()
    {
        DefaultHttpContext context = new();
        await using MemoryStream client = new();
        context.Response.Body = client;

        await Should.ThrowAsync<InvalidOperationException>(() => AtomicMutationResponse.ExecuteAsync(
            context,
            1024,
            httpContext => httpContext.Response.WriteAsync("success"),
            _ => throw new InvalidOperationException("injected ambiguous commit outcome")));

        client.Length.ShouldBe(0);
    }

    [TestMethod]
    public async Task ExplicitStartAndCompleteRemainStagedUntilCommit()
    {
        DefaultHttpContext context = new();
        await using MemoryStream client = new();
        context.Response.Body = client;

        await AtomicMutationResponse.ExecuteAsync(
            context,
            1024,
            async httpContext =>
            {
                await httpContext.Response.StartAsync();
                await httpContext.Response.WriteAsync("success");
                await httpContext.Response.CompleteAsync();
                httpContext.Response.HasStarted.ShouldBeFalse();
                client.Length.ShouldBe(0);
            },
            _ =>
            {
                context.Response.HasStarted.ShouldBeFalse();
                client.Length.ShouldBe(0);
                return Task.CompletedTask;
            });

        Encoding.UTF8.GetString(client.ToArray()).ShouldBe("success");
    }

    [TestMethod]
    public async Task OversizedMutationResponseFailsClosedBeforeCommit()
    {
        DefaultHttpContext context = new();
        await using MemoryStream client = new();
        context.Response.Body = client;
        bool committed = false;

        await Should.ThrowAsync<InvalidOperationException>(() => AtomicMutationResponse.ExecuteAsync(
            context,
            4,
            httpContext => httpContext.Response.WriteAsync("success"),
            _ =>
            {
                committed = true;
                return Task.CompletedTask;
            }));

        committed.ShouldBeFalse();
        client.Length.ShouldBe(0);
    }

    [TestMethod]
    public async Task ByteAtATimeCannotBypassTheResponseLimit()
    {
        DefaultHttpContext context = new();
        await using MemoryStream client = new();
        context.Response.Body = client;
        bool committed = false;

        await Should.ThrowAsync<InvalidOperationException>(() => AtomicMutationResponse.ExecuteAsync(
            context,
            4,
            httpContext =>
            {
                for (int index = 0; index < 5; index++) httpContext.Response.Body.WriteByte((byte)'x');
                return Task.CompletedTask;
            },
            _ =>
            {
                committed = true;
                return Task.CompletedTask;
            }));

        committed.ShouldBeFalse();
        client.Length.ShouldBe(0);
    }

    [TestMethod]
    public async Task DisconnectBeforeCommitPublishesNoSuccess()
    {
        DefaultHttpContext context = new();
        await using MemoryStream client = new();
        context.Response.Body = client;
        using CancellationTokenSource disconnected = new();
        context.RequestAborted = disconnected.Token;

        await Should.ThrowAsync<OperationCanceledException>(() => AtomicMutationResponse.ExecuteAsync(
            context,
            1024,
            async httpContext =>
            {
                await httpContext.Response.Body.WriteAsync("success"u8.ToArray(), CancellationToken.None);
                disconnected.Cancel();
            },
            cancellationToken => Task.FromCanceled(cancellationToken)));

        client.Length.ShouldBe(0);
    }
}

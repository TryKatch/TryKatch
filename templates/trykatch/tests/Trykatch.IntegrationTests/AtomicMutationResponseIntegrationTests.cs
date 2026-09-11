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
}

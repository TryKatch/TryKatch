using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Trykatch.Api.Security;

namespace Trykatch.IntegrationTests;

[TestClass]
public sealed class RequestBindingExceptionHandlerTests
{
    [TestMethod]
    [DataRow(400)]
    [DataRow(413)]
    [DataRow(415)]
    public async Task ClientBindingErrorsPreserveStatusWithoutLeakingTheirMessage(int status)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddOptions();
        await using ServiceProvider provider = services.BuildServiceProvider();
        DefaultHttpContext context = new() { RequestServices = provider };
        using MemoryStream response = new();
        context.Response.Body = response;
        RequestBindingExceptionHandler handler = new();

        (await handler.TryHandleAsync(context, new BadHttpRequestException("Sensitive JSON and CLR names", status), CancellationToken.None)).ShouldBeTrue();
        context.Response.StatusCode.ShouldBe(status);
        string payload = System.Text.Encoding.UTF8.GetString(response.ToArray());
        payload.ShouldContain("invalid_request");
        payload.ShouldNotContain("Sensitive");
        payload.ShouldNotContain("CLR");
    }

    [TestMethod]
    public async Task UnexpectedServerExceptionsAreNotDisguisedAsClientErrors()
    {
        RequestBindingExceptionHandler handler = new();
        (await handler.TryHandleAsync(new DefaultHttpContext(), new InvalidOperationException("Server failure"), CancellationToken.None)).ShouldBeFalse();
        (await handler.TryHandleAsync(new DefaultHttpContext(), new BadHttpRequestException("Server failure", 500), CancellationToken.None)).ShouldBeFalse();
    }
}

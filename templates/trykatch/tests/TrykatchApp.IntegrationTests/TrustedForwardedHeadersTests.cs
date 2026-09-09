using System.Net;
using TrykatchApp.Api.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;

namespace TrykatchApp.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
public sealed class TrustedForwardedHeadersTests
{
    [TestMethod]
    public void ConfiguresOnlyExplicitTrustedProxies()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ReverseProxy:KnownProxies:0"] = "172.30.250.10"
            })
            .Build();
        ServiceCollection services = new();

        services.AddTrustedForwardedHeaders(configuration);

        using ServiceProvider provider = services.BuildServiceProvider();
        ForwardedHeadersOptions options = provider
            .GetRequiredService<IOptions<ForwardedHeadersOptions>>()
            .Value;

        options.ForwardedHeaders.ShouldBe(ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);
        options.ForwardLimit.ShouldBe(1);
        options.RequireHeaderSymmetry.ShouldBeTrue();
        options.KnownIPNetworks.ShouldBeEmpty();
        options.KnownProxies.ShouldBe([IPAddress.Parse("172.30.250.10")]);
    }

    [TestMethod]
    public void RejectsInvalidTrustedProxyConfiguration()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ReverseProxy:KnownProxies:0"] = "docker-network"
            })
            .Build();
        ServiceCollection services = new();

        InvalidOperationException exception = Should.Throw<InvalidOperationException>(
            () => services.AddTrustedForwardedHeaders(configuration));

        exception.Message.ShouldContain("invalid IP address");
    }

    [TestMethod]
    public async Task UnconfiguredOrUntrustedPeersCannotForwardSchemeOrClientAddress()
    {
        await AssertForwardingIgnoredAsync(new ConfigurationBuilder().Build(), IPAddress.Parse("172.30.250.10"));

        IConfiguration configured = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ReverseProxy:KnownProxies:0"] = "172.30.250.10"
            })
            .Build();
        await AssertForwardingIgnoredAsync(configured, IPAddress.Parse("172.30.250.11"));
    }

    [TestMethod]
    public async Task ConfiguredProxyCanForwardOneSymmetricHeaderPair()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ReverseProxy:KnownProxies:0"] = "172.30.250.10"
            })
            .Build();
        ServiceCollection services = new();
        services.AddLogging();
        services.AddTrustedForwardedHeaders(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();
        ApplicationBuilder application = new(provider);
        application.UseForwardedHeaders();
        application.Run(context =>
        {
            context.Request.Scheme.ShouldBe("https");
            context.Connection.RemoteIpAddress.ShouldBe(IPAddress.Parse("203.0.113.8"));
            return Task.CompletedTask;
        });

        DefaultHttpContext context = CreateForwardedContext(provider, IPAddress.Parse("172.30.250.10"));

        await application.Build()(context);
    }

    private static async Task AssertForwardingIgnoredAsync(IConfiguration configuration, IPAddress peerAddress)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddTrustedForwardedHeaders(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();
        ApplicationBuilder application = new(provider);
        application.UseForwardedHeaders();
        application.Run(context =>
        {
            context.Request.Scheme.ShouldBe("http");
            context.Connection.RemoteIpAddress.ShouldBe(peerAddress);
            return Task.CompletedTask;
        });

        DefaultHttpContext context = CreateForwardedContext(provider, peerAddress);

        await application.Build()(context);
    }

    private static DefaultHttpContext CreateForwardedContext(IServiceProvider services, IPAddress peerAddress)
    {
        DefaultHttpContext context = new() { RequestServices = services };
        context.Request.Scheme = "http";
        context.Connection.RemoteIpAddress = peerAddress;
        context.Request.Headers["X-Forwarded-For"] = "203.0.113.8";
        context.Request.Headers["X-Forwarded-Proto"] = "https";
        return context;
    }
}

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Shouldly;
using StackExchange.Redis;
using Trykatch.Api.Realtime;

namespace Trykatch.IntegrationTests;

[TestClass]
public sealed class RealtimeBackplaneSettingsTests
{
    [TestMethod]
    public void DistributedConnectionRequiresOneAuthenticatedEndpointWithoutSentinelDiscovery()
    {
        foreach (string? invalid in new string?[]
        {
            null, "", "cache.invalid:6379", "cache.invalid:6379,password=", "bad-option=credential-sentinel",
            "first.invalid:6379,second.invalid:6379,password=credential-sentinel",
            "cache.invalid:6379,password=credential-sentinel,serviceName=discovered-primary"
        })
        {
            Exception error = Should.Throw<InvalidOperationException>(() => RealtimeBackplaneSettings.Parse(invalid));
            error.Message.ShouldBe("Distributed realtime requires a valid authenticated realtime connection.");
            error.InnerException.ShouldBeNull();
            error.ToString().ShouldNotContain("credential-sentinel");
        }
    }

    [TestMethod]
    public void ConnectionOverridesStayWithinFiniteHostBoundsAndSuppressExceptionDetails()
    {
        ConfigurationOptions settings = RealtimeBackplaneSettings.Parse(
            "cache.invalid:6380,user=operator,password=credential-sentinel,ssl=true,allowAdmin=true,connectRetry=99,connectTimeout=60000,asyncTimeout=60000,syncTimeout=60000,abortConnect=true");
        settings.EndPoints.Count.ShouldBe(1);
        settings.User.ShouldBe("operator");
        settings.Password.ShouldBe("credential-sentinel");
        settings.Ssl.ShouldBeTrue();
        settings.AllowAdmin.ShouldBeFalse();
        settings.AbortOnConnectFail.ShouldBeFalse();
        settings.ConnectRetry.ShouldBe(1);
        settings.ConnectTimeout.ShouldBe(1000);
        settings.AsyncTimeout.ShouldBe(1000);
        settings.SyncTimeout.ShouldBe(1000);
        settings.IncludeDetailInExceptions.ShouldBeFalse();
        settings.IncludePerformanceCountersInExceptions.ShouldBeFalse();
        settings.ClientName.ShouldBe("realtime");
    }

    [TestMethod]
    public void LiteralChannelPrefixSeparatesApplicationAndEnvironmentWithoutEchoingEnvironment()
    {
        RedisChannel one = RealtimeBackplaneSettings.ChannelPrefix("one", "credential-like-environment");
        one.IsPattern.ShouldBeFalse();
        one.ShouldBe(RealtimeBackplaneSettings.ChannelPrefix("one", "credential-like-environment"));
        one.ShouldNotBe(RealtimeBackplaneSettings.ChannelPrefix("two", "credential-like-environment"));
        one.ShouldNotBe(RealtimeBackplaneSettings.ChannelPrefix("one", "other-environment"));
        one.ToString().ShouldNotContain("credential-like-environment");
        one.ToString().Length.ShouldBeLessThan(180);
    }

    [TestMethod]
    public void DefaultAndGenerationCompositionNeverRequireARealtimeBackend()
    {
        foreach (var (mode, generation) in new[] { ("Disabled", false), ("LocalOnly", false), ("Distributed", true) })
        {
            ServiceCollection services = new();
            services.AddSingleton<IHostEnvironment>(new TestEnvironment());
            IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Realtime:Mode"] = mode, ["Realtime:ApplicationNamespace"] = "composition",
                ["ConnectionStrings:realtime"] = "invalid-and-must-not-be-parsed"
            }).Build();
            services.AddRealtime(configuration, generation);
            services.Any(service => service.ImplementationType?.Name.Contains("RedisHubLifetimeManager", StringComparison.Ordinal) == true)
                .ShouldBeFalse();
            using ServiceProvider provider = services.BuildServiceProvider();
            provider.GetRequiredService<IOptions<RealtimeOptions>>().Value.Mode.ToString().ShouldBe(mode);
            services.Any(service => service.ServiceType == typeof(IHostedService) && service.ImplementationType == typeof(RealtimeRelay))
                .ShouldBe(!generation && mode != "Disabled");
        }
    }

    [TestMethod]
    public void NormalDistributedCompositionRejectsAnAbsentConnectionBeforeStartingAnyHost()
    {
        ServiceCollection services = new();
        services.AddSingleton<IHostEnvironment>(new TestEnvironment());
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Realtime:Mode"] = "Distributed", ["Realtime:ApplicationNamespace"] = "composition"
        }).Build();
        Should.Throw<InvalidOperationException>(() => services.AddRealtime(configuration, false));
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "RealtimeSettingsTests";
        public string ContentRootPath { get; set; } = "/";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}

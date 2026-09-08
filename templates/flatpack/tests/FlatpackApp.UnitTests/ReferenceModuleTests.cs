using System.Text.Json;
using FlatpackApp.Infrastructure.Modules;
using FlatpackApp.Modules;
using FlatpackApp.Modules.AspNetCore;
using FlatpackApp.Modules.GettingStarted;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace FlatpackApp.UnitTests;

[TestClass]
public sealed class ReferenceModuleTests
{
    [TestMethod]
    public void PackageManifestMatchesRuntimeDescriptor()
    {
        string json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "getting-started.module.json"));
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        FlatpackModuleDescriptor descriptor = new GettingStartedModule().Descriptor;

        root.GetProperty("id").GetString().ShouldBe(descriptor.Id);
        root.GetProperty("version").GetString().ShouldBe(descriptor.Version);
        root.GetProperty("requires").EnumerateArray().Select(value => value.GetString()).ShouldBe(descriptor.Requires);
        root.GetProperty("assistantTools")[0].GetProperty("name").GetString()
            .ShouldBe(descriptor.AssistantTools[0].Name);
    }

    [TestMethod]
    public void HostMapsEnabledModuleInsideOrganizationSecurityBoundary()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddFlatpackModules(new ConfigurationBuilder().Build(),
            [new ProjectsModule(), new GettingStartedModule()]);
        WebApplication app = builder.Build();

        app.MapFlatpackOrganizationModuleEndpoints();

        RouteEndpoint endpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(value => value.RoutePattern.RawText == "/api/v1/getting-started");
        endpoint.Metadata.GetMetadata<FlatpackOrganizationScopedMetadata>().ShouldNotBeNull();
        endpoint.Metadata.GetMetadata<FlatpackModuleEndpointMetadata>()?.ModuleId.ShouldBe("getting-started");
        endpoint.Metadata.GetOrderedMetadata<Microsoft.AspNetCore.Authorization.IAuthorizeData>()
            .ShouldContain(value => value.Policy == $"permission:{GettingStartedPermissions.Read}");
    }

    [TestMethod]
    public void HostDoesNotExposeEndpointWhenModuleIsNotEnabled()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddFlatpackModules(new ConfigurationBuilder().Build(), [new ProjectsModule()]);
        WebApplication app = builder.Build();

        app.MapFlatpackOrganizationModuleEndpoints();

        ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ShouldNotContain(value => value.RoutePattern.RawText == "/api/v1/getting-started");
    }
}

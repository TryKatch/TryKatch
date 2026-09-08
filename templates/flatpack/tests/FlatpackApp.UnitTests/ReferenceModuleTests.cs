using System.Text.Json;
using FlatpackApp.Infrastructure.Modules;
using FlatpackApp.Modules;
using FlatpackApp.Modules.AspNetCore;
using FlatpackApp.Modules.GettingStarted;
using FlatpackApp.Modules.Federation;
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
    public void GettingStartedManifestMatchesRuntimeDescriptor()
    {
        AssertManifestMatches("getting-started.module.json", new GettingStartedModule().Descriptor);
    }

    [TestMethod]
    public void ProjectsManifestMatchesRuntimeDescriptor()
    {
        AssertManifestMatches("projects.module.json", new ProjectsModule().Descriptor);
    }

    [TestMethod]
    public void FederationManifestAndMigrationMatchRuntimeDescriptor()
    {
        FederationModule module = new();
        AssertManifestMatches("federation.module.json", module.Descriptor);

        IReadOnlyList<PendingFlatpackModuleMigration> plan = FlatpackModuleMigrationPlan.Build([module], []);

        plan.Count.ShouldBe(1);
        plan[0].ModuleId.ShouldBe("federation");
        plan[0].Sql.ShouldContain("identity.federation_connections");
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

    [TestMethod]
    public void HostMapsFederationInsidePlatformPermissionBoundary()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Configuration["ConnectionStrings:flatpackdb"] = "Host=localhost;Database=test;Username=test;Password=test";
        builder.Services.AddAuthorization();
        builder.Services.AddDataProtection();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddFlatpackModules(builder.Configuration, [new FederationModule()]);
        WebApplication app = builder.Build();

        app.MapFlatpackPlatformModuleEndpoints();

        RouteEndpoint endpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(value => value.RoutePattern.RawText == "/api/v1/platform/federation/connections/"
                && value.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods.Contains("GET") == true);
        endpoint.Metadata.GetMetadata<FlatpackModuleEndpointMetadata>()?.ModuleId.ShouldBe("federation");
        endpoint.Metadata.GetOrderedMetadata<Microsoft.AspNetCore.Authorization.IAuthorizeData>()
            .ShouldContain(value => value.Policy == "platform-permission:platform.authentication.read");
    }

    private static void AssertManifestMatches(string fixture, FlatpackModuleDescriptor descriptor)
    {
        string json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", fixture));
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        root.GetProperty("id").GetString().ShouldBe(descriptor.Id);
        root.GetProperty("name").GetString().ShouldBe(descriptor.Name);
        root.GetProperty("version").GetString().ShouldBe(descriptor.Version);
        root.GetProperty("description").GetString().ShouldBe(descriptor.Description);
        root.GetProperty("requires").EnumerateArray().Select(value => value.GetString()).ShouldBe(descriptor.Requires);
        root.GetProperty("optionalDependencies").EnumerateArray().Select(value => value.GetString())
            .ShouldBe(descriptor.OptionalDependencies);
        ParseCapabilities(root.GetProperty("capabilities")).ShouldBe(descriptor.Capabilities);
        root.GetProperty("contributions").GetProperty("extensionPoints").EnumerateArray()
            .Select(value => value.GetProperty("id").GetString())
            .ShouldBe(descriptor.ExtensionPoints.Select(point => point.Id));

        JsonElement.ArrayEnumerator tools = root.GetProperty("contributions").GetProperty("assistantTools").EnumerateArray();
        tools.Select(value => value.GetProperty("name").GetString()).ShouldBe(descriptor.AssistantTools.Select(tool => tool.Name));
        tools.Select(value => value.GetProperty("operationId").GetString()).ShouldBe(descriptor.AssistantTools.Select(tool => tool.OperationId));
        tools.Select(value => value.GetProperty("requiresHumanConfirmation").GetBoolean())
            .ShouldBe(descriptor.AssistantTools.Select(tool => tool.RequiresHumanConfirmation));
    }

    private static FlatpackModuleCapabilities ParseCapabilities(JsonElement capabilities)
    {
        FlatpackModuleCapabilities result = FlatpackModuleCapabilities.None;
        foreach (JsonElement value in capabilities.EnumerateArray())
        {
            result |= value.GetString() switch
            {
                "api" => FlatpackModuleCapabilities.Api,
                "web" => FlatpackModuleCapabilities.Web,
                "data" => FlatpackModuleCapabilities.Data,
                "background-work" => FlatpackModuleCapabilities.BackgroundWork,
                "assistant" => FlatpackModuleCapabilities.Assistant,
                string unknown => throw new InvalidOperationException($"Unknown manifest capability '{unknown}'."),
                null => throw new InvalidOperationException("Manifest capability cannot be null.")
            };
        }

        return result;
    }
}

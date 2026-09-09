using System.Text.Json;
using TrykatchApp.Infrastructure.Modules;
using TrykatchApp.Modules;
using TrykatchApp.Modules.AspNetCore;
using TrykatchApp.Modules.Federation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace TrykatchApp.UnitTests;

[TestClass]
public sealed class ReferenceModuleTests
{
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

        IReadOnlyList<PendingTrykatchModuleMigration> plan = TrykatchModuleMigrationPlan.Build([module], []);

        plan.Count.ShouldBe(1);
        plan[0].ModuleId.ShouldBe("federation");
        plan[0].Sql.ShouldContain("identity.federation_connections");
    }

    [TestMethod]
    public void HostMapsFederationInsidePlatformPermissionBoundary()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Configuration["ConnectionStrings:trykatchdb"] = "Host=localhost;Database=test;Username=test;Password=test";
        builder.Services.AddAuthorization();
        builder.Services.AddDataProtection();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddTrykatchModules(builder.Configuration, [new FederationModule()]);
        WebApplication app = builder.Build();

        app.MapTrykatchPlatformModuleEndpoints();

        RouteEndpoint endpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(value => value.RoutePattern.RawText == "/api/v1/platform/federation/connections/"
                && value.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods.Contains("GET") == true);
        endpoint.Metadata.GetMetadata<TrykatchModuleEndpointMetadata>()?.ModuleId.ShouldBe("federation");
        endpoint.Metadata.GetOrderedMetadata<Microsoft.AspNetCore.Authorization.IAuthorizeData>()
            .ShouldContain(value => value.Policy == "platform-permission:platform.authentication.read");
    }

    private static void AssertManifestMatches(string fixture, TrykatchModuleDescriptor descriptor)
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

    private static TrykatchModuleCapabilities ParseCapabilities(JsonElement capabilities)
    {
        TrykatchModuleCapabilities result = TrykatchModuleCapabilities.None;
        foreach (JsonElement value in capabilities.EnumerateArray())
        {
            result |= value.GetString() switch
            {
                "api" => TrykatchModuleCapabilities.Api,
                "web" => TrykatchModuleCapabilities.Web,
                "data" => TrykatchModuleCapabilities.Data,
                "background-work" => TrykatchModuleCapabilities.BackgroundWork,
                "assistant" => TrykatchModuleCapabilities.Assistant,
                string unknown => throw new InvalidOperationException($"Unknown manifest capability '{unknown}'."),
                null => throw new InvalidOperationException("Manifest capability cannot be null.")
            };
        }

        return result;
    }
}

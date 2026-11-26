using System.Text.Json;
using Trykatch.Infrastructure.Modules;
using Trykatch.Modules;
using Trykatch.Modules.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Trykatch.UnitTests;

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

        IReadOnlyList<PendingModuleMigration> plan = ModuleMigrationPlan.Build([module], []);

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
        builder.Services.AddModules(builder.Configuration, [new FederationModule()]);
        WebApplication app = builder.Build();

        app.MapPlatformModuleEndpoints();

        RouteEndpoint endpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(value => value.RoutePattern.RawText == "/api/v1/platform/federation/connections/"
                && value.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods.Contains("GET") == true);
        endpoint.Metadata.GetMetadata<ModuleEndpointMetadata>()?.ModuleId.ShouldBe("federation");
        endpoint.Metadata.GetOrderedMetadata<Microsoft.AspNetCore.Authorization.IAuthorizeData>()
            .ShouldContain(value => value.Policy == "platform-permission:platform.authentication.read");
    }

    private static void AssertManifestMatches(string fixture, ModuleDescriptor descriptor)
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

        if (!root.TryGetProperty("execution", out JsonElement execution))
        {
            descriptor.Operations.ShouldBeEmpty();
            descriptor.SystemGrants.ShouldBeEmpty();
            descriptor.IntegrationEvents.ShouldBeEmpty();
            descriptor.EventConsumers.ShouldBeEmpty();
            descriptor.Jobs.ShouldBeEmpty();
            descriptor.RecurringSchedules.ShouldBeEmpty();
            return;
        }

        JsonElement.ArrayEnumerator operations = execution.GetProperty("operations").EnumerateArray();
        operations.Select(item => item.GetProperty("operationKey").GetString()).ShouldBe(descriptor.Operations.Select(item => item.OperationKey));
        operations.Select(item => item.GetProperty("operationVersion").GetInt32()).ShouldBe(descriptor.Operations.Select(item => item.OperationVersion));
        operations.Select(item => item.GetProperty("executionKind").GetString()).ShouldBe(descriptor.Operations.Select(item => item.ExecutionKind switch
        {
            ModuleExecutionKind.UserCommand => "user-command",
            ModuleExecutionKind.CommittedEvent => "committed-event",
            ModuleExecutionKind.ScheduledSystem => "scheduled-system",
            _ => throw new InvalidOperationException($"Unknown execution kind '{item.ExecutionKind}'.")
        }));
        operations.Select(item => item.GetProperty("payloadSchemaKey").GetString()).ShouldBe(descriptor.Operations.Select(item => item.PayloadSchemaKey));
        operations.Select(item => item.GetProperty("payloadSchemaVersion").GetInt32()).ShouldBe(descriptor.Operations.Select(item => item.PayloadSchemaVersion));
        operations.Select(item => item.GetProperty("requiredPermissionKeys").EnumerateArray().Select(permission => permission.GetString()).ToArray())
            .ShouldBe(descriptor.Operations.Select(item => item.RequiredPermissionKeys.ToArray()));
        operations.Select(item => item.GetProperty("resourcePolicyKey").GetString()).ShouldBe(descriptor.Operations.Select(item => item.ResourcePolicyKey));
        operations.Select(item => item.GetProperty("systemGrantKey").ValueKind == JsonValueKind.Null ? null : item.GetProperty("systemGrantKey").GetString())
            .ShouldBe(descriptor.Operations.Select(item => item.SystemGrantKey));
        operations.Select(item => item.GetProperty("maximumPayloadBytes").GetInt32()).ShouldBe(descriptor.Operations.Select(item => item.MaximumPayloadBytes));

        JsonElement.ArrayEnumerator grants = execution.GetProperty("systemGrants").EnumerateArray();
        grants.Select(item => item.GetProperty("grantKey").GetString()).ShouldBe(descriptor.SystemGrants.Select(item => item.GrantKey));
        grants.Select(item => item.GetProperty("grantVersion").GetInt32()).ShouldBe(descriptor.SystemGrants.Select(item => item.GrantVersion));
        grants.Select(item => item.GetProperty("auditActorId").GetGuid()).ShouldBe(descriptor.SystemGrants.Select(item => item.AuditActorId));
        grants.Select(item => item.GetProperty("allowedOperationKeys").EnumerateArray().Select(value => value.GetString()).ToArray())
            .ShouldBe(descriptor.SystemGrants.Select(item => item.AllowedOperationKeys.ToArray()));
        grants.Select(item => item.GetProperty("allowedResourceKeys").EnumerateArray().Select(value => value.GetString()).ToArray())
            .ShouldBe(descriptor.SystemGrants.Select(item => item.AllowedResourceKeys.ToArray()));

        JsonElement.ArrayEnumerator integrationEvents = execution.GetProperty("integrationEvents").EnumerateArray();
        integrationEvents.Select(item => item.GetProperty("eventKey").GetString()).ShouldBe(descriptor.IntegrationEvents.Select(item => item.EventKey));
        integrationEvents.Select(item => item.GetProperty("schemaVersion").GetInt32()).ShouldBe(descriptor.IntegrationEvents.Select(item => item.SchemaVersion));
        integrationEvents.Select(item => item.GetProperty("maximumPayloadBytes").GetInt32()).ShouldBe(descriptor.IntegrationEvents.Select(item => item.MaximumPayloadBytes));
        integrationEvents.Any(item => item.TryGetProperty("payloadType", out _) || item.TryGetProperty("readPayload", out _)).ShouldBeFalse();

        JsonElement.ArrayEnumerator eventConsumers = execution.GetProperty("eventConsumers").EnumerateArray();
        eventConsumers.Select(item => item.GetProperty("consumerKey").GetString()).ShouldBe(descriptor.EventConsumers.Select(item => item.ConsumerKey));
        eventConsumers.Select(item => item.GetProperty("acceptedEvents").EnumerateArray().Select(value => (value.GetProperty("eventKey").GetString()!, value.GetProperty("schemaVersion").GetInt32())).ToArray())
            .ShouldBe(descriptor.EventConsumers.Select(item => item.AcceptedEvents.Select(value => (value.EventKey, value.SchemaVersion)).ToArray()));
        eventConsumers.Select(item => item.GetProperty("operationKey").GetString()).ShouldBe(descriptor.EventConsumers.Select(item => item.OperationKey));
        eventConsumers.Select(item => item.GetProperty("operationVersion").GetInt32()).ShouldBe(descriptor.EventConsumers.Select(item => item.OperationVersion));
        eventConsumers.Select(item => item.GetProperty("systemGrantKey").GetString()).ShouldBe(descriptor.EventConsumers.Select(item => item.SystemGrantKey));

        JsonElement.ArrayEnumerator jobs = execution.GetProperty("jobs").EnumerateArray();
        jobs.Select(item => item.GetProperty("jobKey").GetString()).ShouldBe(descriptor.Jobs.Select(item => item.JobKey));
        jobs.Select(item => item.GetProperty("jobVersion").GetInt32()).ShouldBe(descriptor.Jobs.Select(item => item.JobVersion));
        jobs.Select(item => item.GetProperty("operationKey").GetString()).ShouldBe(descriptor.Jobs.Select(item => item.OperationKey));
        jobs.Select(item => item.GetProperty("operationVersion").GetInt32()).ShouldBe(descriptor.Jobs.Select(item => item.OperationVersion));
        jobs.Select(item => item.GetProperty("executionKind").GetString()).ShouldBe(descriptor.Jobs.Select(item => item.ExecutionKind switch
        {
            ModuleExecutionKind.UserCommand => "user-command",
            ModuleExecutionKind.CommittedEvent => "committed-event",
            ModuleExecutionKind.ScheduledSystem => "scheduled-system",
            _ => throw new InvalidOperationException($"Unknown execution kind '{item.ExecutionKind}'.")
        }));
        jobs.Select(item => item.GetProperty("payloadSchemaKey").GetString()).ShouldBe(descriptor.Jobs.Select(item => item.PayloadSchemaKey));
        jobs.Select(item => item.GetProperty("payloadSchemaVersion").GetInt32()).ShouldBe(descriptor.Jobs.Select(item => item.PayloadSchemaVersion));
        jobs.Select(item => item.GetProperty("maximumPayloadBytes").GetInt32()).ShouldBe(descriptor.Jobs.Select(item => item.MaximumPayloadBytes));

        JsonElement.ArrayEnumerator schedules = execution.GetProperty("recurringSchedules").EnumerateArray();
        schedules.Select(item => item.GetProperty("scheduleKey").GetString()).ShouldBe(descriptor.RecurringSchedules.Select(item => item.ScheduleKey));
        schedules.Select(item => item.GetProperty("jobKey").GetString()).ShouldBe(descriptor.RecurringSchedules.Select(item => item.JobKey));
        schedules.Select(item => item.GetProperty("jobVersion").GetInt32()).ShouldBe(descriptor.RecurringSchedules.Select(item => item.JobVersion));
        schedules.Select(item => item.GetProperty("utcSchedule").GetString()).ShouldBe(descriptor.RecurringSchedules.Select(item => item.UtcSchedule));
        schedules.Select(item => item.GetProperty("misfirePolicy").GetString()).ShouldBe(descriptor.RecurringSchedules.Select(item => item.MisfirePolicy switch
        {
            ModuleMisfirePolicy.Coalesce => "coalesce",
            ModuleMisfirePolicy.CatchUp => "catch-up",
            _ => throw new InvalidOperationException($"Unknown misfire policy '{item.MisfirePolicy}'.")
        }));
        schedules.Select(item => item.GetProperty("systemGrantKey").GetString()).ShouldBe(descriptor.RecurringSchedules.Select(item => item.SystemGrantKey));
    }

    private static ModuleCapabilities ParseCapabilities(JsonElement capabilities)
    {
        ModuleCapabilities result = ModuleCapabilities.None;
        foreach (JsonElement value in capabilities.EnumerateArray())
        {
            result |= value.GetString() switch
            {
                "api" => ModuleCapabilities.Api,
                "web" => ModuleCapabilities.Web,
                "data" => ModuleCapabilities.Data,
                "background-work" => ModuleCapabilities.BackgroundWork,
                "assistant" => ModuleCapabilities.Assistant,
                string unknown => throw new InvalidOperationException($"Unknown manifest capability '{unknown}'."),
                null => throw new InvalidOperationException("Manifest capability cannot be null.")
            };
        }

        return result;
    }
}

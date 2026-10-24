using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Shouldly;
using Trykatch.Modules;
using Trykatch.Modules.AspNetCore.Assistant;
using Trykatch.Infrastructure.Persistence;

namespace Trykatch.IntegrationTests;

public sealed partial class AccessManagementBoundaryTests
{
    [TestMethod]
    public async Task ProjectsAssistantReadMapsFeatureUnavailableAndPolicyFailureThenRecovers()
    {
        ScriptedProjectsAssistantModel model = new();
        FeatureReadFailureProbe featureReadFailure = new();
        await using AccessHost host = await AccessHost.StartAsync(
            configureServices: services =>
            {
                services.AddDbContext<OrganizationControlPlaneDbContext>((_, options) => options.AddInterceptors(featureReadFailure));
                services.RemoveAll<AssistantRuntime>();
                services.AddScoped<AssistantRuntime>(provider => new(
                    provider.GetRequiredService<ModuleCatalog>(),
                    provider.GetServices<IReadOnlyAssistantTool>(),
                    provider.GetRequiredService<IModulePermissionAuthorizer>(),
                    model,
                    Options.Create(new AssistantOptions { Enabled = true }),
                    provider.GetRequiredService<AssistantKnowledge>(),
                    new EnabledAssistantActivation()));
            });

        using HttpClient administrator = await host.SignInAsync(AccessHost.AdministratorEmail);
        using HttpClient owner = await host.CreateOrganizationOwnerAsync(administrator);
        await SaveProjectsEnvironmentAsync(administrator, ProjectReads, enabled: true);

        using (HttpResponseMessage enabled = await AccessHost.SendAsync(owner, HttpMethod.Post, "/api/v1/assistant/ask", new { message = "List projects" }))
        {
            enabled.StatusCode.ShouldBe(HttpStatusCode.OK, await enabled.Content.ReadAsStringAsync());
            (await enabled.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("answer").GetString().ShouldNotBeNullOrWhiteSpace();
        }
        model.ToolResult.ShouldContain("pageSize");

        await SaveProjectsEnvironmentAsync(administrator, ProjectReads, enabled: false);
        using (HttpResponseMessage disabled = await AccessHost.SendAsync(owner, HttpMethod.Post, "/api/v1/assistant/ask", new { message = "List projects" }))
        {
            JsonElement problem = await AccessHost.AssertProblemAsync(disabled, HttpStatusCode.Conflict, "feature_unavailable");
            problem.GetRawText().ShouldNotContain("private feature failure");
            problem.GetRawText().ShouldNotContain("feature_environment_controls");
        }

        await SaveProjectsEnvironmentAsync(administrator, ProjectReads, enabled: true);
        featureReadFailure.Fail = true;
        using (HttpResponseMessage unavailable = await AccessHost.SendAsync(owner, HttpMethod.Post, "/api/v1/assistant/ask", new { message = "List projects" }))
        {
            JsonElement problem = await AccessHost.AssertProblemAsync(unavailable, HttpStatusCode.ServiceUnavailable, "feature_policy_unavailable");
            problem.GetRawText().ShouldNotContain("private feature failure");
            problem.GetRawText().ShouldNotContain("feature_environment_controls");
        }

        featureReadFailure.Fail = false;
        using HttpResponseMessage recovered = await AccessHost.SendAsync(owner, HttpMethod.Post, "/api/v1/assistant/ask", new { message = "List projects" });
        recovered.StatusCode.ShouldBe(HttpStatusCode.OK, await recovered.Content.ReadAsStringAsync());
        model.ToolResult.ShouldContain("pageSize");
    }

    private sealed class EnabledAssistantActivation : IOrganizationAssistantActivation
    {
        public Task<bool> IsEnabledAsync(CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<Guid> VersionAsync(CancellationToken cancellationToken) => Task.FromResult(Guid.Parse("11111111-1111-4111-8111-111111111111"));
    }

    private sealed class ScriptedProjectsAssistantModel : IChatClient
    {
        public string ToolResult { get; private set; } = string.Empty;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            ChatMessage[] input = messages.ToArray();
            if (input[^1].Role == ChatRole.Tool)
            {
                FunctionResultContent result = input[^1].Contents.OfType<FunctionResultContent>().Single();
                ToolResult = AssistantProtocol.ResultJson(result);
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "I checked the current project list.")));
            }

            FunctionCallContent call = AssistantProtocol.Call("projects-read", "trykatch_list_projects", "{\"page\":1,\"search\":null}");
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [call])));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}

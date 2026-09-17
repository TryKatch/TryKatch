using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Npgsql;
using Shouldly;
using Testcontainers.PostgreSql;
using Trykatch.Identity;
using Trykatch.Api.Controllers;
using Trykatch.Infrastructure.Organizations;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Modules;
using Trykatch.Modules.AspNetCore.Assistant;
using Trykatch.Domain.Organizations;
using Trykatch.Api.Security;

namespace Trykatch.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class AssistantIntegrationTests
{
    private const string Email = "assistant-member@trykatch.test";
    private const string Password = "Local-only!Assistant-Password-42";

    [TestMethod]
    public async Task ProductionHttpBoundaryEnforcesScopeAntiforgeryRlsReadOnlyAndRateLimits()
    {
        await using PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:18.6-alpine3.23@sha256:697c180dbf244d3ce4a8f4cbc0156cde840af055c1bf8b76aebe422a4822086f").Build();
        await postgres.StartAsync();
        string owner = postgres.GetConnectionString();
        (string organization, string platform, string identity, string outbox) = await PostgresRuntimeRoleFixture.CreateConnectionStringsAsync(owner);
        IModule[] modules = global::Trykatch.Api.Modules.EnabledModules.All.ToArray();
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:trykatchdb"] = identity }).Build();
        await MigrateAsync(owner, configuration, modules);
        await RuntimeRoleProvisioner.ProvisionAsync(owner, new RuntimeDatabaseRoles(
            PostgresRuntimeRoleFixture.OrganizationRole, PostgresRuntimeRoleFixture.PlatformRole,
            PostgresRuntimeRoleFixture.IdentityRole, PostgresRuntimeRoleFixture.OutboxRole));
        ScriptedModel model = new();
        await using WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseEnvironment("Development");
            // Early settings are also needed by AddInfrastructure before the deferred configuration callback.
            host.UseSetting("ConnectionStrings:trykatchdb", organization);
            host.UseSetting("ConnectionStrings:trykatch-organization", organization);
            host.UseSetting("ConnectionStrings:trykatch-platform", platform);
            host.UseSetting("ConnectionStrings:trykatch-identity", identity);
            host.UseSetting("ConnectionStrings:trykatch-outbox", outbox);
            host.ConfigureAppConfiguration((_, settings) => settings.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:trykatchdb"] = organization,
                ["ConnectionStrings:trykatch-organization"] = organization,
                ["ConnectionStrings:trykatch-platform"] = platform,
                ["ConnectionStrings:trykatch-identity"] = identity,
                ["ConnectionStrings:trykatch-outbox"] = outbox,
                ["Bootstrap:PlatformAdminEmail"] = "assistant-platform@trykatch.test",
                ["Bootstrap:PlatformAdminPassword"] = Password,
                ["DevelopmentDemo:Enabled"] = "true",
                ["DevelopmentDemo:PlatformAdminEmail"] = "assistant-platform@trykatch.test",
                ["DevelopmentDemo:TenantAdminEmail"] = Email,
                ["DevelopmentDemo:Password"] = Password,
                ["DevelopmentDemo:OrganizationName"] = "Assistant test workspace",
                ["DevelopmentDemo:OrganizationSlug"] = "assistant-test",
                ["Assistant:Enabled"] = "true",
                ["Assistant:Provider"] = "openai",
                ["Assistant:ApiKey"] = "test-only-not-a-real-key",
                ["Assistant:Model"] = "test-only-model"
            }));
            host.ConfigureTestServices(services =>
            {
                services.RemoveAll<IChatClient>();
                services.AddSingleton<IChatClient>(model);
            });
        });
        using HttpClient anonymous = Client(factory);
        (await anonymous.GetAsync("/api/v1/assistant/status")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.GetAsync("/api/v1/assistant/guides/architecture")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.PostAsJsonAsync("/api/v1/assistant/ask", new { message = "List projects" })).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        model.Requests.ShouldBe(0);
        using HttpClient member = Client(factory);
        string token = await TokenAsync(member);
        using (HttpResponseMessage login = await SendAsync(member, "/api/v1/auth/login", new { email = Email, password = Password, rememberMe = false }, token))
            login.StatusCode.ShouldBe(HttpStatusCode.OK);
        token = await TokenAsync(member);
        (await member.GetAsync("/api/v1/assistant/status")).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        Guid organizationId;
        using (IServiceScope scope = factory.Services.CreateScope())
            organizationId = await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().Organizations
                .Where(item => item.Slug == "assistant-test").Select(item => item.Id).SingleAsync();
        using (HttpResponseMessage select = await SendAsync(member, "/api/v1/workspace/select", new { organizationId, remember = false }, token))
            select.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        Guid foreignId = Guid.CreateVersion7();
        Guid foreignOrganization = Guid.CreateVersion7();
        await using (NpgsqlConnection connection = new(owner))
        {
            await connection.OpenAsync();
            for (int index = 0; index < 23; index++)
            {
                await using NpgsqlCommand insert = new("""
                    INSERT INTO app.projects ("Id", "OrganizationId", "Name", "Description", "CreatedBy", "CreatedAt")
                    VALUES (@id, @org, @name, @description, @actor, now())
                    """, connection);
                insert.Parameters.AddWithValue("id", index == 22 ? foreignId : Guid.CreateVersion7());
                insert.Parameters.AddWithValue("org", index == 22 ? foreignOrganization : organizationId);
                insert.Parameters.AddWithValue("name", index == 22 ? "Foreign secret project" : $"Visible project {index}");
                insert.Parameters.AddWithValue("description", "Private description");
                insert.Parameters.AddWithValue("actor", Guid.CreateVersion7());
                await insert.ExecuteNonQueryAsync();
            }
            await using NpgsqlCommand document = new("""
                INSERT INTO app.documents ("Id", "OrganizationId", "Title", "Content", "CreatedBy", "CreatedAt", "ObjectKey")
                VALUES (@id, @org, 'Allowed metadata', 'Private content never sent', @actor, now(), 'private-object-key-never-sent')
                """, connection);
            document.Parameters.AddWithValue("id", Guid.CreateVersion7());
            document.Parameters.AddWithValue("org", organizationId);
            document.Parameters.AddWithValue("actor", Guid.CreateVersion7());
            await document.ExecuteNonQueryAsync();
        }
        AssistantStatus status = (await member.GetFromJsonAsync<AssistantStatus>("/api/v1/assistant/status"))!;
        status.HelpAvailable.ShouldBeTrue();
        status.Tools.Length.ShouldBe(3);
        status.Tools.ShouldNotContain("try" + "katch_update_document");
        (await member.PostAsJsonAsync("/api/v1/assistant/ask", new { message = "List projects" })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        model.Requests.ShouldBe(0);
        using (HttpResponseMessage forged = await SendAsync(member, "/api/v1/assistant/ask", new { message = "test", organizationId = foreignOrganization }, token))
            forged.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        model.Requests.ShouldBe(0);
        model.Tool = "try" + "katch_get_project";
        model.Arguments = JsonSerializer.Serialize(new { id = foreignId });
        using (HttpResponseMessage ask = await SendAsync(member, "/api/v1/assistant/ask", new { message = "Get project" }, token))
            ask.StatusCode.ShouldBe(HttpStatusCode.OK, await ask.Content.ReadAsStringAsync());
        using (JsonDocument result = JsonDocument.Parse(model.LastResult))
            result.RootElement.GetProperty("found").GetBoolean().ShouldBeFalse();
        model.LastResult.ShouldNotContain("Foreign secret project");
        model.Tool = "try" + "katch_list_projects";
        model.Arguments = "{\"page\":1,\"search\":null}";
        string continuation;
        using (HttpResponseMessage ask = await SendAsync(member, "/api/v1/assistant/ask", new { message = "List projects" }, token))
        {
            ask.StatusCode.ShouldBe(HttpStatusCode.OK, await ask.Content.ReadAsStringAsync());
            continuation = (await ask.Content.ReadFromJsonAsync<AssistantAnswer>())!.ConversationToken!;
            continuation.ShouldNotBeNullOrWhiteSpace();
        }
        using (JsonDocument result = JsonDocument.Parse(model.LastResult))
        {
            result.RootElement.GetProperty("items").GetArrayLength().ShouldBe(20);
            result.RootElement.GetProperty("hasMore").GetBoolean().ShouldBeTrue();
        }
        model.Tool = "try" + "katch_list_documents";
        model.Batch = ["try" + "katch_list_projects", model.Tool];
        using (HttpResponseMessage ask = await SendAsync(member, "/api/v1/assistant/ask", new { message = "List documents" }, token))
            ask.StatusCode.ShouldBe(HttpStatusCode.OK, await ask.Content.ReadAsStringAsync());
        model.LastResults.Length.ShouldBe(2);
        using (JsonDocument projects = JsonDocument.Parse(model.LastResults[0]))
            projects.RootElement.GetProperty("items").GetArrayLength().ShouldBe(20);
        model.LastResults[0].ShouldNotContain("Foreign secret project");
        model.LastResult.ShouldContain("Allowed metadata");
        model.LastResult.ShouldNotContain("Private content never sent");
        model.LastResult.ShouldNotContain("private-object-key-never-sent");
        model.Batch = [];
        model.Tool = "try" + "katch_update_document";
        using (HttpResponseMessage ask = await SendAsync(member, "/api/v1/assistant/ask", new { message = "Update everything" }, token))
            ask.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        model.Tool = "try" + "katch_list_projects";
        using (HttpResponseMessage followUp = await SendAsync(member, "/api/v1/assistant/ask", new { message = "Explain those projects", conversationToken = continuation }, token))
            followUp.StatusCode.ShouldBe(HttpStatusCode.OK, await followUp.Content.ReadAsStringAsync());
        model.LastInitialInput.Where(message => !message.Text.StartsWith("Server-supplied help reference data.", StringComparison.Ordinal))
            .Select(message => message.Text).ShouldBe(["List projects", "Read completed; no changes made.", "Explain those projects"]);
        int beforeLimit = model.Requests;
        model.FinishReason = ChatFinishReason.Length;
        using (HttpResponseMessage exhausted = await SendAsync(member, "/api/v1/assistant/ask", new { message = "Explain further" }, token))
        {
            exhausted.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
            using JsonDocument problem = JsonDocument.Parse(await exhausted.Content.ReadAsStringAsync());
            problem.RootElement.GetProperty("title").GetString().ShouldBe("response_limit");
            problem.RootElement.TryGetProperty("answer", out _).ShouldBeFalse();
            problem.RootElement.TryGetProperty("conversationToken", out _).ShouldBeFalse();
        }
        model.Requests.ShouldBe(beforeLimit + 1);
        model.FinishReason = null;
        int beforeInvalid = model.Requests;
        using (HttpResponseMessage tampered = await SendAsync(member, "/api/v1/assistant/ask", new { message = "Follow up", conversationToken = "forged-not-base64" }, token))
            tampered.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        AssistantConversationTokens conversationTokens = new(factory.Services.GetRequiredService<IDataProtectionProvider>(), TimeProvider.System);
        string foreignContinuation = conversationTokens.Continue(conversationTokens.Read(null,
            new(Guid.NewGuid(), foreignOrganization, Guid.NewGuid(), "different-access")), "foreign question", "foreign answer");
        using (HttpResponseMessage foreignContext = await SendAsync(member, "/api/v1/assistant/ask", new { message = "Follow up", conversationToken = foreignContinuation }, token))
            foreignContext.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        model.Requests.ShouldBe(beforeInvalid);
        bool limited = false;
        for (int index = 0; index < 11; index++)
        {
            using HttpResponseMessage ask = await SendAsync(member, "/api/v1/assistant/ask", new { message = "Try again" }, token);
            if (ask.StatusCode == HttpStatusCode.TooManyRequests) { limited = true; break; }
        }
        limited.ShouldBeTrue();
        // A distinct authenticated membership with no role grants may read approved help, never organization records.
        const string helpEmail = "assistant-help-only@trykatch.test";
        ApplicationUser helpUser = new() { Id = Guid.CreateVersion7(), Email = helpEmail, UserName = helpEmail, EmailConfirmed = true, DisplayName = "Help-only member" };
        using (IServiceScope scope = factory.Services.CreateScope())
            (await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().CreateAsync(helpUser, Password)).Succeeded.ShouldBeTrue();
        await using (PlatformDbContext seed = new(new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(owner).Options))
        {
            seed.Memberships.Add(Membership.Create(organizationId, helpUser.Id));
            await seed.SaveChangesAsync();
        }
        using HttpClient helpMember = Client(factory);
        string helpCsrf = await TokenAsync(helpMember);
        using (HttpResponseMessage login = await SendAsync(helpMember, "/api/v1/auth/login", new { email = helpEmail, password = Password, rememberMe = false }, helpCsrf))
            login.StatusCode.ShouldBe(HttpStatusCode.OK);
        helpCsrf = await TokenAsync(helpMember);
        using (HttpResponseMessage select = await SendAsync(helpMember, "/api/v1/workspace/select", new { organizationId, remember = false }, helpCsrf))
            select.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        AssistantStatus helpStatus = (await helpMember.GetFromJsonAsync<AssistantStatus>("/api/v1/assistant/status"))!;
        helpStatus.HelpAvailable.ShouldBeTrue();
        helpStatus.Tools.ShouldBeEmpty();
        AssistantGuide guide = (await helpMember.GetFromJsonAsync<AssistantGuide>("/api/v1/assistant/guides/isolation"))!;
        guide.Sections.Any(section => section.Text.Contains("transaction-local", StringComparison.Ordinal)).ShouldBeTrue();
        (await helpMember.GetAsync("/api/v1/assistant/guides/appsettings.json")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await helpMember.GetAsync("/api/v1/assistant/guides/federation")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await helpMember.GetAsync("/api/v1/projects")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        model.Tool = "";
        using (HttpResponseMessage help = await SendAsync(helpMember, "/api/v1/assistant/ask", new { message = "Explain the architecture and layers" }, helpCsrf))
        {
            help.StatusCode.ShouldBe(HttpStatusCode.OK, await help.Content.ReadAsStringAsync());
            AssistantAnswer guidance = (await help.Content.ReadFromJsonAsync<AssistantAnswer>())!;
            guidance.ToolsUsed.ShouldBeEmpty();
            guidance.Guides!.Select(source => source.Id).ShouldContain("architecture");
        }
        model.Tool = "try" + "katch_list_projects";
        using (HttpResponseMessage forbiddenRead = await SendAsync(helpMember, "/api/v1/assistant/ask", new { message = "List projects" }, helpCsrf))
            forbiddenRead.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        model.Tool = "";
        TaskCompletionSource<ChatResponse> hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        model.Hold = hold;
        Task<HttpResponseMessage>[] concurrent = Enumerable.Range(0, AssistantRequestLimits.ConcurrentRequests)
            .Select(_ => SendAsync(helpMember, "/api/v1/assistant/ask", new { message = "Explain architecture" }, helpCsrf)).ToArray();
        try
        {
            using CancellationTokenSource wait = new(TimeSpan.FromSeconds(10));
            while (model.Waiting < AssistantRequestLimits.ConcurrentRequests) await Task.Delay(20, wait.Token);
            // This caller has not consumed its actor quota; rejection must come from the shared concurrency cap.
            using HttpResponseMessage busy = await anonymous.PostAsJsonAsync("/api/v1/assistant/ask", new { message = "Explain architecture" });
            busy.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
            (await anonymous.GetAsync("/health/ready")).StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        finally
        {
            hold.TrySetResult(new(new ChatMessage(ChatRole.Assistant, "Documented architecture answer.")));
            foreach (HttpResponseMessage result in await Task.WhenAll(concurrent))
            {
                using (result) result.StatusCode.ShouldBe(HttpStatusCode.OK, await result.Content.ReadAsStringAsync());
            }
            model.Hold = null;
        }
        // Completed calls release their slots; the normal authentication response resumes.
        using (HttpResponseMessage released = await anonymous.PostAsJsonAsync("/api/v1/assistant/ask", new { message = "Explain architecture" }))
            released.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        await using (NpgsqlConnection connection = new(owner))
        {
            await connection.OpenAsync();
            await using NpgsqlCommand check = new("SELECT count(*) FROM app.projects WHERE \"Name\" = 'Foreign secret project' AND \"Description\" = 'Private description'", connection);
            Convert.ToInt64(await check.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture).ShouldBe(1);
        }
    }

    private static HttpClient Client(WebApplicationFactory<Program> factory) => factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = true });
    private static async Task<string> TokenAsync(HttpClient client) => (await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/antiforgery")).GetProperty("token").GetString()!;
    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string path, object body, string token)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-CSRF-TOKEN", token);
        return await client.SendAsync(request);
    }
    private static async Task MigrateAsync(string owner, IConfiguration configuration, IModule[] modules)
    {
        await PostgresRuntimeRoleFixture.EnsureRuntimeRolesAsync(owner);
        await using IdentityDbContext identity = new(new DbContextOptionsBuilder<IdentityDbContext>().UseNpgsql(owner).Options);
        await identity.Database.MigrateAsync();
        await using PlatformDbContext platform = new(new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(owner).Options);
        await platform.Database.MigrateAsync();
        ServiceCollection services = new();
        ModuleCatalog catalog = services.AddModules(configuration, modules);
        await using ServiceProvider provider = services.BuildServiceProvider();
        await using ApplicationDbContext application = new(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(owner).Options,
            provider.GetServices<IApplicationModelContributor>(), moduleCatalog: catalog);
        await application.Database.MigrateAsync();
        await using NpgsqlConnection connection = new(owner);
        await connection.OpenAsync();
        foreach (IModuleMigrationContributor contributor in modules.OfType<IModuleMigrationContributor>())
        foreach (ModuleMigration migration in contributor.Migrations)
        {
            await using NpgsqlCommand command = new(migration.Sql, connection);
            await command.ExecuteNonQueryAsync();
        }
        await InstalledSchemaCatalog.SynchronizeAsync(owner, modules.SelectMany(module => module.Descriptor.DataResources.Select(resource => new InstalledDataResource(module.Descriptor.Id, resource))));
    }
    private sealed class ScriptedModel : IChatClient
    {
        private int _waiting;
        public int Waiting => Volatile.Read(ref _waiting);
        public TaskCompletionSource<ChatResponse>? Hold { get; set; }
        public ChatFinishReason? FinishReason { get; set; }
        public string Tool { get; set; } = "";
        public string[] Batch { get; set; } = [];
        public string Arguments { get; set; } = "{}";
        public string LastResult { get; private set; } = "";
        public string[] LastResults { get; private set; } = [];
        public int Requests { get; private set; }
        public ChatMessage[] LastInitialInput { get; private set; } = [];
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            ChatMessage[] input = messages.ToArray();
            Requests++;
            if (FinishReason is { } finishReason)
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Partial answer must not be returned.")) { FinishReason = finishReason });
            if (Hold is { } held)
            {
                Interlocked.Increment(ref _waiting);
                return WaitAsync(held, cancellationToken);
            }
            if (input[^1].Contents.Single() is FunctionResultContent result)
            {
                LastResults = input.Where(message => message.Role == ChatRole.Tool).SelectMany(message => message.Contents)
                    .OfType<FunctionResultContent>().Select(AssistantProtocol.ResultJson).ToArray();
                LastResult = AssistantProtocol.ResultJson(result);
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Read completed; no changes made.")));
            }
            LastInitialInput = input;
            if (Batch.Length > 0) return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                Batch.Select((name, index) => (AIContent)AssistantProtocol.Call($"batch-{index}", name, Arguments)).ToList())));
            if (Tool.Length == 0) return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "The documented architecture uses focused module layers.")));
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [AssistantProtocol.Call("test-call", Tool, Arguments)])));
        }
        private async Task<ChatResponse> WaitAsync(TaskCompletionSource<ChatResponse> held, CancellationToken cancellationToken)
        {
            try { return await held.Task.WaitAsync(cancellationToken); }
            finally { Interlocked.Decrement(ref _waiting); }
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}

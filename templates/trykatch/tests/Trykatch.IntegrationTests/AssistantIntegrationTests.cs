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
using Trykatch.Application.Organizations;

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
        ProviderWire wire = new();
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
                services.AddHttpClient("assistant-provider").ConfigurePrimaryHttpMessageHandler(() => wire);
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
        (await member.GetFromJsonAsync<AssistantStatus>("/api/v1/assistant/status"))!.Enabled.ShouldBeFalse();
        model.Requests.ShouldBe(0);
        OrganizationAiConfiguration ai = (await member.GetFromJsonAsync<OrganizationAiConfiguration>("/api/v1/organization-settings/ai"))!;
        ai.Enabled.ShouldBeFalse();
        ai.Version.ShouldBe(Guid.Empty);
        using (HttpResponseMessage missingProof = await member.PutAsJsonAsync("/api/v1/organization-settings/ai", new { enabled = true, expectedVersion = ai.Version }))
            missingProof.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using (HttpRequestMessage unknownFields = new(HttpMethod.Put, "/api/v1/organization-settings/ai"))
        {
            unknownFields.Headers.Add("X-CSRF-TOKEN", token);
            unknownFields.Content = JsonContent.Create(new { enabled = true, expectedVersion = ai.Version, organizationId = Guid.NewGuid(), apiKey = "not-a-secret" });
            using HttpResponseMessage rejected = await member.SendAsync(unknownFields);
            rejected.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }
        using (HttpRequestMessage activation = new(HttpMethod.Put, "/api/v1/organization-settings/ai"))
        {
            activation.Headers.Add("X-CSRF-TOKEN", token);
            activation.Content = JsonContent.Create(new { enabled = true, expectedVersion = ai.Version });
            using HttpResponseMessage activated = await member.SendAsync(activation);
            activated.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        ai = (await member.GetFromJsonAsync<OrganizationAiConfiguration>("/api/v1/organization-settings/ai"))!;
        ai.Enabled.ShouldBeTrue();
        ai.Version.ShouldNotBe(Guid.Empty);
        Organization otherAiOrganization = Organization.Create("Other AI workspace", "other-ai-workspace");
        await using (PlatformDbContext seedAi = new(new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(owner).Options))
        {
            seedAi.Organizations.Add(otherAiOrganization);
            seedAi.Set<OrganizationAssistantSetting>().Add(OrganizationAssistantSetting.Create(otherAiOrganization.Id, false));
            await seedAi.SaveChangesAsync();
        }
        await using (NpgsqlConnection isolatedAi = new(organization))
        {
            await isolatedAi.OpenAsync();
            await using (NpgsqlCommand missingScope = new("SELECT count(*) FROM platform.organization_assistant_settings", isolatedAi))
                Convert.ToInt64(await missingScope.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture).ShouldBe(0);
            await using var transaction = await isolatedAi.BeginTransactionAsync();
            await using (NpgsqlCommand setScope = new("SELECT set_config('app.organization_id', @organization, true)", isolatedAi, transaction))
            {
                setScope.Parameters.AddWithValue("organization", organizationId.ToString());
                await setScope.ExecuteNonQueryAsync();
            }
            await using (NpgsqlCommand visible = new("SELECT count(*) FROM platform.organization_assistant_settings", isolatedAi, transaction))
                Convert.ToInt64(await visible.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture).ShouldBe(1);
            await using (NpgsqlCommand foreignWrite = new("UPDATE platform.organization_assistant_settings SET \"Enabled\" = true WHERE \"OrganizationId\" = @foreign", isolatedAi, transaction))
            {
                foreignWrite.Parameters.AddWithValue("foreign", otherAiOrganization.Id);
                (await foreignWrite.ExecuteNonQueryAsync()).ShouldBe(0);
            }
            await using (NpgsqlCommand moveAcrossBoundary = new("UPDATE platform.organization_assistant_settings SET \"OrganizationId\" = @foreign", isolatedAi, transaction))
            {
                moveAcrossBoundary.Parameters.AddWithValue("foreign", Guid.CreateVersion7());
                (await Should.ThrowAsync<PostgresException>(() => moveAcrossBoundary.ExecuteNonQueryAsync())).SqlState.ShouldBe("42501");
            }
            await transaction.RollbackAsync();
        }
        using (HttpRequestMessage stale = new(HttpMethod.Put, "/api/v1/organization-settings/ai"))
        {
            stale.Headers.Add("X-CSRF-TOKEN", token);
            stale.Content = JsonContent.Create(new { enabled = false, expectedVersion = Guid.Empty });
            using HttpResponseMessage rejected = await member.SendAsync(stale);
            rejected.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }
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
        (await helpMember.GetAsync("/api/v1/organization-settings/ai")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        using (HttpRequestMessage forbiddenActivation = new(HttpMethod.Put, "/api/v1/organization-settings/ai"))
        {
            forbiddenActivation.Headers.Add("X-CSRF-TOKEN", helpCsrf);
            forbiddenActivation.Content = JsonContent.Create(new { enabled = false, expectedVersion = ai.Version });
            using HttpResponseMessage denied = await helpMember.SendAsync(forbiddenActivation);
            denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }
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
        // Enabling this organization must not enable a distinct organization's assistant.
        const string disabledEmail = "assistant-disabled-tenant@trykatch.test";
        ApplicationUser disabledUser = new() { Id = Guid.CreateVersion7(), Email = disabledEmail, UserName = disabledEmail, EmailConfirmed = true, DisplayName = "Disabled tenant member" };
        using (IServiceScope scope = factory.Services.CreateScope())
            (await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().CreateAsync(disabledUser, Password)).Succeeded.ShouldBeTrue();
        await using (PlatformDbContext seed = new(new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(owner).Options))
        {
            seed.Memberships.Add(Membership.Create(otherAiOrganization.Id, disabledUser.Id));
            await seed.SaveChangesAsync();
        }
        using HttpClient disabledMember = Client(factory);
        string disabledCsrf = await TokenAsync(disabledMember);
        using (HttpResponseMessage login = await SendAsync(disabledMember, "/api/v1/auth/login", new { email = disabledEmail, password = Password, rememberMe = false }, disabledCsrf))
            login.StatusCode.ShouldBe(HttpStatusCode.OK);
        disabledCsrf = await TokenAsync(disabledMember);
        using (HttpResponseMessage select = await SendAsync(disabledMember, "/api/v1/workspace/select", new { organizationId = otherAiOrganization.Id, remember = false }, disabledCsrf))
            select.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await disabledMember.GetFromJsonAsync<AssistantStatus>("/api/v1/assistant/status"))!.Enabled.ShouldBeFalse();
        int beforeDisabled = model.Requests;
        using (HttpResponseMessage disabledAsk = await SendAsync(disabledMember, "/api/v1/assistant/ask", new { message = "Explain architecture" }, disabledCsrf))
            disabledAsk.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        model.Requests.ShouldBe(beforeDisabled);
        await VerifyOrganizationProviderAsync(factory, owner, member, token, disabledMember, disabledCsrf,
            disabledUser.Id, organizationId, otherAiOrganization.Id, wire, model);
        await using (NpgsqlConnection connection = new(owner))
        {
            await connection.OpenAsync();
            await using NpgsqlCommand check = new("SELECT count(*) FROM app.projects WHERE \"Name\" = 'Foreign secret project' AND \"Description\" = 'Private description'", connection);
            Convert.ToInt64(await check.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture).ShouldBe(1);
        }
    }

    private static HttpClient Client(WebApplicationFactory<Program> factory) => factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = true });
    private static async Task VerifyOrganizationProviderAsync(WebApplicationFactory<Program> factory, string owner,
        HttpClient administrator, string csrf, HttpClient freshActor, string freshCsrf, Guid userId, Guid organizationId,
        Guid otherOrganizationId, ProviderWire wire, ScriptedModel global)
    {
        OrganizationAiConfiguration current = (await administrator.GetFromJsonAsync<OrganizationAiConfiguration>("/api/v1/organization-settings/ai"))!;
        const string firstKey = "test-only-organization-key-one";
        const string secondKey = "test-only-organization-key-two";
        async Task<OrganizationAiConfiguration> SaveAsync(object body, HttpStatusCode expected)
        {
            using HttpRequestMessage request = new(HttpMethod.Put, "/api/v1/organization-settings/ai") { Content = JsonContent.Create(body) };
            request.Headers.Add("X-CSRF-TOKEN", csrf);
            using HttpResponseMessage response = await administrator.SendAsync(request);
            string json = await response.Content.ReadAsStringAsync();
            response.StatusCode.ShouldBe(expected, json);
            json.ShouldNotContain(firstKey);
            json.ShouldNotContain(secondKey);
            json.ShouldNotContain("protectedApiKey");
            return expected == HttpStatusCode.OK ? (await response.Content.ReadFromJsonAsync<OrganizationAiConfiguration>())! : current;
        }
        current = await SaveAsync(new { enabled = true, expectedVersion = current.Version, provider = "deepseek", model = "organization-model",
            endpoint = "https://api.deepseek.com", apiKey = firstKey, timeoutMs = 30_000 }, HttpStatusCode.OK);
        current.UsesTenantProvider.ShouldBeTrue();
        current.HasApiKey.ShouldBeTrue();
        string protectedKey;
        await using (PlatformDbContext database = new(new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(owner).Options))
            protectedKey = (await database.Set<OrganizationAssistantSetting>().SingleAsync(item => item.OrganizationId == organizationId)).ProtectedApiKey;
        protectedKey.ShouldNotContain(firstKey);
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            var protector = scope.ServiceProvider.GetRequiredService<IOrganizationAssistantKeyProtector>();
            protector.Unprotect(organizationId, protectedKey).ShouldBe(firstKey);
            Should.Throw<System.Security.Cryptography.CryptographicException>(() => protector.Unprotect(otherOrganizationId, protectedKey));
        }
        foreach (string endpoint in new[] { "https://localhost", "http://127.0.0.1", "https://api.deepseek.com/other", "https://api.deepseek.com?secret=x", "https://user@api.deepseek.com" })
            await SaveAsync(new { enabled = true, expectedVersion = current.Version, endpoint, apiKey = secondKey }, HttpStatusCode.BadRequest);
        await SaveAsync(new { enabled = true, expectedVersion = Guid.Empty, apiKey = secondKey }, HttpStatusCode.Conflict);
        (await administrator.GetFromJsonAsync<OrganizationAiConfiguration>("/api/v1/organization-settings/ai"))!.Version.ShouldBe(current.Version);
        current = await SaveAsync(new { enabled = true, expectedVersion = current.Version, model = "organization-model-two" }, HttpStatusCode.OK);
        await using (PlatformDbContext database = new(new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(owner).Options))
            (await database.Set<OrganizationAssistantSetting>().SingleAsync(item => item.OrganizationId == organizationId)).ProtectedApiKey.ShouldBe(protectedKey);
        await SaveAsync(new { enabled = true, expectedVersion = current.Version, provider = "openai", endpoint = "" }, HttpStatusCode.BadRequest);
        await SaveAsync(new { enabled = true, expectedVersion = current.Version, removeApiKey = true }, HttpStatusCode.BadRequest);
        current = await SaveAsync(new { enabled = false, expectedVersion = current.Version, removeApiKey = true }, HttpStatusCode.OK);
        current.HasApiKey.ShouldBeFalse();
        await SaveAsync(new { enabled = true, expectedVersion = current.Version }, HttpStatusCode.BadRequest);
        current = await SaveAsync(new { enabled = true, expectedVersion = current.Version, apiKey = secondKey }, HttpStatusCode.OK);
        wire.Requests.ShouldBe(0);
        using (HttpResponseMessage forbidden = await SendAsync(freshActor, "/api/v1/organization-settings/ai/test", new { expectedVersion = current.Version }, freshCsrf))
            forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        Membership membership = Membership.Create(organizationId, userId);
        await using (PlatformDbContext database = new(new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(owner).Options))
        {
            membership.AssignRole(await database.Roles.Where(role => role.OrganizationId == organizationId && role.Name == "Owner").Select(role => role.Id).SingleAsync());
            database.Memberships.Add(membership);
            await database.SaveChangesAsync();
        }
        using (HttpResponseMessage select = await SendAsync(freshActor, "/api/v1/workspace/select", new { organizationId, remember = false }, freshCsrf))
            select.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        using (HttpResponseMessage stale = await SendAsync(freshActor, "/api/v1/organization-settings/ai/test", new { expectedVersion = Guid.Empty }, freshCsrf))
            stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        wire.Requests.ShouldBe(0);
        using (HttpResponseMessage test = await SendAsync(freshActor, "/api/v1/organization-settings/ai/test", new { expectedVersion = current.Version }, freshCsrf))
        {
            test.StatusCode.ShouldBe(HttpStatusCode.OK, await test.Content.ReadAsStringAsync());
            (await test.Content.ReadFromJsonAsync<OrganizationAiConnectionResult>())!.Connected.ShouldBeTrue();
        }
        wire.Requests.ShouldBe(1);
        wire.Authorization.ShouldBe("Bearer " + secondKey);
        wire.Uri.ShouldBe("https://api.deepseek.com/chat/completions");
        using (JsonDocument request = JsonDocument.Parse(wire.Body))
        {
            request.RootElement.GetProperty("model").GetString().ShouldBe("organization-model-two");
            request.RootElement.TryGetProperty("tools", out _).ShouldBeFalse();
            request.RootElement.GetProperty("messages").GetArrayLength().ShouldBe(2);
            request.RootElement.GetProperty("messages")[1].GetProperty("content").GetString().ShouldBe("Reply with OK.");
            request.RootElement.GetProperty("max_tokens").GetInt32().ShouldBe(8);
        }
        int globalBefore = global.Requests;
        using (HttpResponseMessage answer = await SendAsync(freshActor, "/api/v1/assistant/ask", new { message = "Explain architecture" }, freshCsrf))
            answer.StatusCode.ShouldBe(HttpStatusCode.OK, await answer.Content.ReadAsStringAsync());
        wire.Requests.ShouldBe(2);
        global.Requests.ShouldBe(globalBefore);
        using (HttpResponseMessage select = await SendAsync(freshActor, "/api/v1/workspace/select", new { organizationId = otherOrganizationId, remember = false }, freshCsrf))
            select.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        using (HttpResponseMessage answer = await SendAsync(freshActor, "/api/v1/assistant/ask", new { message = "Explain architecture" }, freshCsrf))
            answer.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        wire.Requests.ShouldBe(2);
        await using (PlatformDbContext database = new(new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(owner).Options))
        {
            (await database.Memberships.SingleAsync(item => item.Id == membership.Id)).Suspend();
            await database.SaveChangesAsync();
        }
        using (HttpResponseMessage select = await SendAsync(freshActor, "/api/v1/workspace/select", new { organizationId, remember = false }, freshCsrf))
            select.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        wire.Requests.ShouldBe(2);
    }

    private sealed class ProviderWire : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public string Body { get; private set; } = "";
        public string? Authorization { get; private set; }
        public string Uri { get; private set; } = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Authorization = request.Headers.Authorization?.ToString();
            Uri = request.RequestUri!.AbsoluteUri;
            return new(HttpStatusCode.OK) { Content = new StringContent("""{"choices":[{"finish_reason":"stop","message":{"role":"assistant","content":"OK"}}]}""") };
        }
    }
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

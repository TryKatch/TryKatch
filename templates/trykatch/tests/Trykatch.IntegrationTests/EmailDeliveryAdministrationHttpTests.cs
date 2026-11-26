#if TRYKATCH_EMAIL
using System.Collections.Concurrent;
using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Trykatch.Application.Identity;
using Trykatch.Application.Mailing;
using Trykatch.Identity;
using Trykatch.Infrastructure.Mailing;

namespace Trykatch.IntegrationTests;

public sealed partial class AccessManagementBoundaryTests
{
    [TestMethod]
    public async Task EmailInspectionRequiresExplicitGrantAndProjectsOnlySafeOwnedMetadata()
    {
        EmailMetadataCapture capture = new();
        await using AccessHost host = await AccessHost.StartAsync(queuedEmail: true, configureServices: services =>
        {
            services.AddSingleton(provider => new DbContextOptionsBuilder<IdentityEmailDeliveryDbContext>()
                .UseNpgsql(provider.GetRequiredService<IConfiguration>().GetConnectionString("trykatch-identity"))
                .AddInterceptors(capture).Options);
            services.AddSingleton(provider => new DbContextOptionsBuilder<OrganizationEmailDeliveryDbContext>()
                .UseNpgsql(provider.GetRequiredService<IConfiguration>().GetConnectionString("trykatch-work"))
                .AddInterceptors(capture).Options);
        });
        using HttpClient anonymous = host.CreateClient();
        (await anonymous.GetAsync("/api/v1/platform/email-deliveries")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        using HttpClient administrator = await host.SignInAsync(AccessHost.AdministratorEmail);
        foreach (string path in new[] { "?boundary=organization", "?boundary=identity", "/templates", "/templates/preview?templateKey=core.password-reset&templateVersion=1" })
            (await administrator.GetAsync("/api/v1/platform/email-deliveries" + path)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        capture.Commands.ShouldBeEmpty();

        JsonElement role = await AccessHost.SuccessAsync(administrator, HttpMethod.Post, "/api/v1/platform-users/roles",
            new { name = "Email reader", description = "Explicit email metadata", permissions = new[] { PlatformPermissions.EmailRead } });
        string roleKey = role.GetProperty("key").GetString()!;
        using HttpClient reader = await host.GrantAndSignInAsync(administrator, "email-reader@trykatch.test", roleKey);
        using (IServiceScope scope = host.Services.CreateScope())
            scope.ServiceProvider.GetServices<IEmailDeliveryMetadataReader>().Select(value => value.Boundary)
                .Order().ShouldBe(new[] { EmailDeliveryBoundary.Organization, EmailDeliveryBoundary.Identity }.Order());
        Guid organizationId = Guid.CreateVersion7(), actorId = Guid.CreateVersion7(), userId = Guid.CreateVersion7();
        await using (OrganizationEmailDeliveryDbContext database = new(new DbContextOptionsBuilder<OrganizationEmailDeliveryDbContext>().UseNpgsql(host.OwnerConnection).Options))
        {
            database.EmailDeliveries.AddRange(
                new() { Id = Guid.CreateVersion7(), OrganizationId = organizationId, ActorId = actorId, MessageId = Guid.CreateVersion7(),
                    AdmissionIdentityKey = "fixture", TemplateKey = "core.organization-invitation", TemplateVersion = 1,
                    ProtectedIntent = "private-mail-marker", State = "queued", CreatedAtUtc = DateTimeOffset.UtcNow, LastFailureCode = "private-mail-marker" },
                new() { Id = Guid.CreateVersion7(), OrganizationId = organizationId, ActorId = actorId, MessageId = Guid.CreateVersion7(),
                    AdmissionIdentityKey = "fixture", TemplateKey = "private-template-marker", TemplateVersion = 7,
                    ProtectedIntent = "private-mail-marker", State = "suppressed", CreatedAtUtc = DateTimeOffset.UtcNow });
            await database.SaveChangesAsync();
        }
        await using (IdentityEmailDeliveryDbContext database = new(new DbContextOptionsBuilder<IdentityEmailDeliveryDbContext>().UseNpgsql(host.OwnerConnection).Options))
        {
            database.EmailDeliveries.Add(new() { Id = Guid.CreateVersion7(), UserId = userId,
                TemplateKey = "core.password-reset", TemplateVersion = 1, ProtectedIntent = "private-mail-marker",
                State = "queued", CreatedAtUtc = DateTimeOffset.UtcNow, LastFailureCode = "private-mail-marker" });
            await database.SaveChangesAsync();
        }
        foreach (string boundary in new[] { "organization", "identity" })
        {
            using HttpResponseMessage response = await reader.GetAsync($"/api/v1/platform/email-deliveries?boundary={boundary}&page=-1&pageSize=1000");
            response.StatusCode.ShouldBe(HttpStatusCode.OK, boundary);
            string json = await response.Content.ReadAsStringAsync();
            foreach (string privateValue in new[] { "private-mail-marker", "private-template-marker", "protectedIntent", "userId", "actorId", "messageId", "leaseId", actorId.ToString(), userId.ToString() })
                json.ShouldNotContain(privateValue);
            json.ShouldContain("unclassified");
            JsonElement body = JsonSerializer.Deserialize<JsonElement>(json);
            body.GetProperty("page").GetInt32().ShouldBe(1);
            body.GetProperty("pageSize").GetInt32().ShouldBe(100);
            body.GetProperty("items").GetArrayLength().ShouldBe(boundary == "organization" ? 2 : 1);
        }
        capture.Commands.Count.ShouldBeGreaterThanOrEqualTo(4);
        foreach (string command in capture.Commands)
            foreach (string privateColumn in new[] { "ProtectedIntent", "UserId", "ActorId", "MessageId", "AdmissionIdentityKey", "LeaseId" })
                command.ShouldNotContain(privateColumn);
        (await reader.GetAsync("/api/v1/platform/email-deliveries?state=invalid")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await reader.GetAsync("/api/v1/platform/email-deliveries?boundary=invalid")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await reader.GetAsync($"/api/v1/platform/email-deliveries?boundary=identity&organizationId={organizationId}")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using HttpResponseMessage preview = await reader.GetAsync("/api/v1/platform/email-deliveries/templates/preview?templateKey=core.password-reset&templateVersion=1");
        preview.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await preview.Content.ReadAsStringAsync()).ShouldNotContain("private-mail-marker");
        (await reader.GetAsync("/api/v1/platform/email-deliveries/templates/preview?templateKey=missing&templateVersion=1")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        await AccessHost.SuccessAsync(administrator, HttpMethod.Put, $"/api/v1/platform-users/roles/{roleKey}",
            new { name = "Email reader", description = "Read access removed", permissions = Array.Empty<string>() });
        string[] deniedPaths = ["?boundary=organization", "?boundary=identity", "/templates", "/templates/preview?templateKey=core.password-reset"];
        int beforeDenied = capture.Commands.Count;
        // The role update rotates the stamp: reject the old session before permission evaluation.
        foreach (string path in deniedPaths)
            (await reader.GetAsync("/api/v1/platform/email-deliveries" + path)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        using (HttpClient deniedReader = await host.SignInAsync("email-reader@trykatch.test"))
            foreach (string path in deniedPaths)
                (await deniedReader.GetAsync("/api/v1/platform/email-deliveries" + path)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        capture.Commands.Count.ShouldBe(beforeDenied);

        await AccessHost.SuccessAsync(administrator, HttpMethod.Put, $"/api/v1/platform-users/roles/{roleKey}",
            new { name = "Email reader", description = "Read access restored explicitly", permissions = new[] { PlatformPermissions.EmailRead } });
        using HttpClient currentReader = await host.SignInAsync("email-reader@trykatch.test");
        (await currentReader.GetAsync("/api/v1/platform/email-deliveries/templates")).StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement users = await administrator.GetFromJsonAsync<JsonElement>("/api/v1/platform-users?search=email-reader%40trykatch.test");
        Guid readerId = users.GetProperty("items")[0].GetProperty("id").GetGuid();
        await AccessHost.SuccessAsync(administrator, HttpMethod.Post, $"/api/v1/platform-users/{readerId}/suspend", new { });
        beforeDenied = capture.Commands.Count;
        foreach (string path in deniedPaths)
            (await currentReader.GetAsync("/api/v1/platform/email-deliveries" + path)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        using (HttpClient suspendedReader = await host.SignInAsync("email-reader@trykatch.test"))
            foreach (string path in deniedPaths)
                (await suspendedReader.GetAsync("/api/v1/platform/email-deliveries" + path)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        capture.Commands.Count.ShouldBe(beforeDenied);
    }

    private sealed class EmailMetadataCapture : DbCommandInterceptor
    {
        public ConcurrentQueue<string> Commands { get; } = new();
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("email_deliveries", StringComparison.Ordinal)) Commands.Enqueue(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
#endif

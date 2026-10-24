using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Caching.Distributed;
using Shouldly;
using DotNet.Testcontainers.Containers;
using Trykatch.ExecutionAcceptance;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Infrastructure.Caching;

namespace Trykatch.IntegrationTests;

public sealed partial class AccessManagementBoundaryTests
{
    private static readonly JsonSerializerOptions CacheProcessJson = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public async Task DistributedProjectDetailIsSharedAcrossProcessesAndIdlePeerReadsCommittedGeneration()
    {
        await using IContainer valkey = await OrganizationCacheScopeIntegrationTests.StartCacheBackendAsync();
        Dictionary<string, string?> cacheSettings = OrganizationCacheScopeIntegrationTests.DistributedSettings(valkey);
        cacheSettings["Cache:ApplicationNamespace"] = "project-cache-process-acceptance";
        await using AccessHost host = await AccessHost.StartAsync(additionalSettings: cacheSettings);
        using HttpClient administrator = await host.SignInAsync(AccessHost.AdministratorEmail);
        using HttpClient owner = await host.CreateOrganizationOwnerAsync(administrator);
        JsonElement ownerAccess = await owner.GetFromJsonAsync<JsonElement>("/api/v1/access");
        Guid organizationId = ownerAccess.GetProperty("organizationId").GetGuid();
        Guid ownerActorId = await ReadActorIdAsync(host.OwnerConnection, ownerAccess.GetProperty("membershipId").GetGuid());
        JsonElement created = await AccessHost.SuccessAsync(owner, HttpMethod.Post, "/api/v1/projects", new
        {
            name = "Process cached project", description = "Shared between isolated processes"
        });
        Guid projectId = created.GetProperty("id").GetGuid();

        JsonElement roles = await owner.GetFromJsonAsync<JsonElement>("/api/v1/roles");
        Guid viewerRoleId = roles.EnumerateArray().Single(role => role.GetProperty("name").GetString() == "Viewer")
            .GetProperty("id").GetGuid();
        const string memberEmail = "project-cache-reader@trykatch.test";
        using HttpClient member = await host.InviteAndActivateAsync(owner, memberEmail, viewerRoleId);
        JsonElement memberAccess = await member.GetFromJsonAsync<JsonElement>("/api/v1/access");
        memberAccess.GetProperty("organizationId").GetGuid().ShouldBe(organizationId);
        memberAccess.GetProperty("permissions").EnumerateArray().Select(item => item.GetString())
            .ShouldContain("projects.read");
        Guid memberMembershipId = memberAccess.GetProperty("membershipId").GetGuid();
        Guid memberActorId = await ReadActorIdAsync(host.OwnerConnection, memberMembershipId);

        IConfiguration configuration = host.Services.GetRequiredService<IConfiguration>();
        await using CacheReaderProcess first = await CacheReaderProcess.StartAsync(configuration, cacheSettings);
        await using CacheReaderProcess idlePeer = await CacheReaderProcess.StartAsync(configuration, cacheSettings);
        first.ProcessId.ShouldNotBe(idlePeer.ProcessId);

        CacheAcceptanceReadResponse warmed = await first.ReadAsync(organizationId, ownerActorId, projectId);
        warmed.Kind.ShouldBe("project");
        warmed.Name.ShouldBe("Process cached project");
        warmed.SourceQueryCount.ShouldBe(1);
        await AwaitProjectL2Async(host, organizationId, ownerActorId, projectId);

        CacheAcceptanceReadResponse peerHit = await idlePeer.ReadAsync(organizationId, memberActorId, projectId);
        peerHit.Kind.ShouldBe("project");
        peerHit.Name.ShouldBe("Process cached project");
        peerHit.SourceQueryCount.ShouldBe(0, "the second OS process must deserialize the shared Valkey L2 DTO without querying app.projects");

        await using (CacheReaderProcess fresh = await CacheReaderProcess.StartAsync(configuration, cacheSettings))
        {
            fresh.ProcessId.ShouldNotBe(first.ProcessId);
            fresh.ProcessId.ShouldNotBe(idlePeer.ProcessId);
            CacheAcceptanceReadResponse freshHit = await fresh.ReadAsync(organizationId, memberActorId, projectId);
            freshHit.Kind.ShouldBe("project");
            freshHit.Name.ShouldBe("Process cached project");
            freshHit.SourceQueryCount.ShouldBe(0, "a newly started process must read the already populated shared Valkey L2 entry");
        }

        await AccessHost.SuccessAsync(owner, HttpMethod.Put, $"/api/v1/projects/{projectId}", new
        {
            id = projectId, name = "Committed while the peer was idle", description = "Generation changes with the project transaction"
        });

        CacheAcceptanceReadResponse refreshedPeer = await idlePeer.ReadAsync(organizationId, memberActorId, projectId);
        refreshedPeer.Kind.ShouldBe("project");
        refreshedPeer.Name.ShouldBe("Committed while the peer was idle");
        refreshedPeer.SourceQueryCount.ShouldBe(1, "the idle replica must reject its old generation and load the committed detail once");
        await AwaitProjectL2Async(host, organizationId, ownerActorId, projectId);
        CacheAcceptanceReadResponse refreshedFirst = await first.ReadAsync(organizationId, ownerActorId, projectId);
        refreshedFirst.Kind.ShouldBe("project");
        refreshedFirst.Name.ShouldBe("Committed while the peer was idle");
        refreshedFirst.SourceQueryCount.ShouldBe(0, "the other replica must reject its own old L1 entry and read the new committed generation from L2");

        using HttpResponseMessage revoked = await AccessHost.SendAsync(owner, HttpMethod.Post,
            $"/api/v1/members/{memberMembershipId}/archive", new { });
        revoked.IsSuccessStatusCode.ShouldBeTrue(await revoked.Content.ReadAsStringAsync());
        CacheAcceptanceReadResponse afterRevocation = await idlePeer.ReadAsync(organizationId, memberActorId, projectId);
        afterRevocation.Kind.ShouldBe("denied");
        afterRevocation.Code.ShouldBe("membership_unavailable");
        afterRevocation.SourceQueryCount.ShouldBe(0, "a warm organization cache entry must not bypass current membership resolution");
    }

    private static async Task AwaitProjectL2Async(AccessHost host, Guid organization, Guid actor, Guid project)
    {
        string hash = DetailHash(host, organization, actor, project);
        Guid generation = await ReadGenerationAsync(host.OwnerConnection, organization, hash);
        string physical = CacheKeyPolicy.PhysicalKey(hash, generation);
        IDistributedCache backend = host.Services.GetRequiredKeyedService<IDistributedCache>(OrganizationCacheRegistration.DistributedServiceKey);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        while (await backend.GetAsync(physical, deadline.Token) is null)
            await Task.Delay(50, deadline.Token);
    }

    private sealed record CacheAcceptanceReadResponse(
        string Kind, string? Code, int ProcessId, string? Name, Guid? ProjectId, long SourceQueryCount);

    private sealed class CacheReaderProcess : IAsyncDisposable
    {
        private readonly Process process;
        private bool disposed;
        private bool readFailed;

        private CacheReaderProcess(Process process) => this.process = process;

        public int ProcessId => process.Id;

        public static Task<CacheReaderProcess> StartAsync(IConfiguration configuration,
            IReadOnlyDictionary<string, string?> cacheSettings)
        {
            string testAssembly = typeof(AccessManagementBoundaryTests).Assembly.Location;
            string helperAssembly = typeof(ExecutionAcceptanceProcessMarker).Assembly.Location;
            ProcessStartInfo start = new(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            start.ArgumentList.Add("exec");
            start.ArgumentList.Add("--runtimeconfig");
            start.ArgumentList.Add(Path.ChangeExtension(testAssembly, ".runtimeconfig.json"));
            start.ArgumentList.Add("--depsfile");
            start.ArgumentList.Add(Path.ChangeExtension(testAssembly, ".deps.json"));
            start.ArgumentList.Add(helperAssembly);
            start.ArgumentList.Add("cache-reader");

            Dictionary<string, string?> inherited = Environment.GetEnvironmentVariables()
                .Cast<System.Collections.DictionaryEntry>()
                .Where(entry => entry.Key is string key
                    && (key == "PATH" || key == "HOME" || key.StartsWith("DOTNET_", StringComparison.Ordinal)
                        || key is "TMPDIR" or "TMP" or "TEMP"))
                .ToDictionary(entry => (string)entry.Key!, entry => (string?)entry.Value, StringComparer.Ordinal);
            start.Environment.Clear();
            foreach ((string name, string? value) in inherited)
                if (value is not null) start.Environment[name] = value;
            start.Environment["DOTNET_ENVIRONMENT"] = "Development";
            start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
            foreach (string profile in new[]
            {
                RuntimeDatabaseConnectionContract.Organization,
                RuntimeDatabaseConnectionContract.Platform,
                RuntimeDatabaseConnectionContract.Identity,
                RuntimeDatabaseConnectionContract.Outbox,
                RuntimeDatabaseConnectionContract.WorkCoordinator
            })
            {
                string connection = configuration[$"ConnectionStrings:{profile}"]
                    ?? throw new InvalidOperationException($"Runtime connection profile '{profile}' is missing from the test host.");
                start.Environment["ConnectionStrings__" + profile] = connection;
            }
            foreach ((string key, string? value) in cacheSettings)
                if (value is not null) start.Environment[key.Replace(":", "__", StringComparison.Ordinal)] = value;

            Process process = Process.Start(start) ?? throw new InvalidOperationException("Cache reader process did not start.");
            return Task.FromResult(new CacheReaderProcess(process));
        }

        public async Task<CacheAcceptanceReadResponse> ReadAsync(Guid organizationId, Guid actorId, Guid projectId)
        {
            try { return await ReadCoreAsync(organizationId, actorId, projectId); }
            catch { readFailed = true; throw; }
        }

        private async Task<CacheAcceptanceReadResponse> ReadCoreAsync(Guid organizationId, Guid actorId, Guid projectId)
        {
            process.StandardInput.WriteLine(JsonSerializer.Serialize(new { organizationId, actorId, projectId }, CacheProcessJson));
            await process.StandardInput.FlushAsync();
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
            string? responseLine;
            try { responseLine = await process.StandardOutput.ReadLineAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                if (process.HasExited) throw new InvalidOperationException("Cache reader process exited before returning a result.");
                throw new TimeoutException("Cache reader process did not return a result within 30 seconds.");
            }
            if (responseLine is null)
                throw new InvalidOperationException("Cache reader process closed stdout before returning a result. "
                    + await process.StandardError.ReadToEndAsync(timeout.Token));
            CacheAcceptanceReadResponse response = JsonSerializer.Deserialize<CacheAcceptanceReadResponse>(responseLine, CacheProcessJson)
                ?? throw new InvalidOperationException("Cache reader process returned invalid response metadata.");
            response.Kind.ShouldNotBe("error", response.Code);
            response.ProcessId.ShouldBe(process.Id);
            return response;
        }

        public async ValueTask DisposeAsync()
        {
            if (disposed) return;
            disposed = true;
            process.StandardInput.Close();
            try
            {
                (int exit, string output, string error) = await ProcessExecution.WaitAsync(process, TimeSpan.FromSeconds(10));
                if (!readFailed) exit.ShouldBe(0, output + error);
            }
            catch
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                process.Dispose();
                throw;
            }
            process.Dispose();
        }
    }
}

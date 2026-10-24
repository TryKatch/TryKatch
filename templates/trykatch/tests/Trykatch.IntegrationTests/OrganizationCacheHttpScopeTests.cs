using System.Collections.Concurrent;
using System.Data.Common;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Trykatch.Infrastructure.Persistence;

namespace Trykatch.IntegrationTests;

public sealed partial class AccessManagementBoundaryTests
{
    [TestMethod]
    public async Task ProjectsDetailOptsIntoReadOnlyScopeWhileListAndMutationsKeepWriteScopes()
    {
        CacheReadScopeRecorder recorder = new();
        await using AccessHost host = await AccessHost.StartAsync(configureServices: services =>
            services.AddDbContext<ApplicationDbContext>((_, options) => options.AddInterceptors(recorder)));
        using HttpClient administrator = await host.SignInAsync(AccessHost.AdministratorEmail);
        using HttpClient owner = await host.CreateOrganizationOwnerAsync(administrator);
        JsonElement created = await AccessHost.SuccessAsync(owner, HttpMethod.Post, "/api/v1/projects", new
        {
            name = "Read-only cache scope", description = "Current authority before cache access"
        });
        Guid projectId = created.GetProperty("id").GetGuid();

        recorder.Reads.Clear();
        JsonElement detail = await owner.GetFromJsonAsync<JsonElement>($"/api/v1/projects/{projectId}");
        detail.GetProperty("id").GetGuid().ShouldBe(projectId);
        AssertCacheScope(recorder, expectedReadOnly: true);

        recorder.Reads.Clear();
        _ = await owner.GetFromJsonAsync<JsonElement>("/api/v1/projects");
        AssertCacheScope(recorder, expectedReadOnly: false);

        recorder.Reads.Clear();
        _ = await AccessHost.SuccessAsync(owner, HttpMethod.Put, $"/api/v1/projects/{projectId}", new
        {
            id = projectId, name = "Changed in a write scope", description = "Committed normally"
        });
        AssertCacheScope(recorder, expectedReadOnly: false);
    }

    private static void AssertCacheScope(CacheReadScopeRecorder recorder, bool expectedReadOnly)
    {
        recorder.Reads.ShouldNotBeEmpty();
        foreach ((bool readOnly, string role) in recorder.Reads)
        {
            readOnly.ShouldBe(expectedReadOnly);
            role.ShouldBe(PostgresRuntimeRoleFixture.OrganizationRole);
        }
    }

    private sealed class CacheReadScopeRecorder : DbCommandInterceptor
    {
        public ConcurrentQueue<(bool ReadOnly, string Role)> Reads { get; } = new();

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (!command.CommandText.Contains("projects", StringComparison.Ordinal)) return result;
            await using DbCommand inspect = command.Connection!.CreateCommand();
            inspect.Transaction = command.Transaction;
            inspect.CommandText = "SELECT current_setting('transaction_read_only'), current_user";
            await using DbDataReader reader = await inspect.ExecuteReaderAsync(cancellationToken);
            (await reader.ReadAsync(cancellationToken)).ShouldBeTrue();
            Reads.Enqueue((reader.GetString(0) == "on", reader.GetString(1)));
            return result;
        }
    }
}

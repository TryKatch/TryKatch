using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Trykatch.ExecutionAcceptance;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Infrastructure.Storage;
using Trykatch.Modules;

namespace Trykatch.IntegrationTests;

public sealed partial class ModuleWorkExecutionIntegrationTests
{
    private static readonly JsonSerializerOptions QuotaProcessJson = new(JsonSerializerDefaults.Web);
    private static readonly byte[] QuotaProcessPayload = Encoding.UTF8.GetBytes("quota process upload");

    [TestMethod]
    [DataRow("before-put")]
    [DataRow("after-put")]
    [DataRow("after-final-commit")]
    public async Task KilledUploadProcessRetainsOnlyItsCommittedEffectsAndFreshWorkerRecovers(string phase)
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync(["documents.manage"]);
        await fixture.SeedQuotaProjectionAsync(user.OrganizationId, limit: 64);
        Guid operation = Guid.CreateVersion7();
        string root = Path.Combine(Path.GetTempPath(), "trykatch-quota-process", operation.ToString("N"));
        try
        {
            await using QuotaChildProcess producer = fixture.StartQuotaChild("quota-producer", root, phase, user, operation, pastClock: true);
            await producer.WaitForAsync("quota-checkpoint", phase);
            producer.IsRunning.ShouldBeTrue();
            QuotaProcessSnapshot before = await fixture.ReadQuotaProcessSnapshotAsync(user.OrganizationId, operation);
            bool committed = phase == "after-final-commit";
            before.UploadState.ShouldBe(committed ? 1 : 0);
            before.HoldState.ShouldBe(committed ? 1 : 0);
            before.ExpiresAt.ShouldBeLessThan(DateTimeOffset.UtcNow);
            before.Effects.ShouldBe(committed ? new QuotaProcessEffects(1, 1, 1, 1, 20) : new(0, 0, 0, 0, 0));
            using ServiceProvider storageProvider = QuotaStorageObserver(root);
            IConditionalObjectStorage storage = storageProvider.GetRequiredService<IConditionalObjectStorage>();
            ConditionalObjectStat storedBeforeKill = await storage.StatAsync(before.ObjectKey);
            storedBeforeKill.State.ShouldBe(phase == "before-put" ? ConditionalObjectState.Missing : ConditionalObjectState.Provisional);
            if (phase != "before-put") storedBeforeKill.Length.ShouldBe(20);

            await producer.KillAsync();
            (await fixture.ReadQuotaProcessSnapshotAsync(user.OrganizationId, operation)).Effects.ShouldBe(before.Effects);
            await using QuotaChildProcess recovery = fixture.StartQuotaChild("quota-worker", root,
                documentsAbsent: phase == "before-put");
            JsonElement ready = await recovery.WaitForAsync("quota-ready");
            AssertQuotaProcessRoles(ready);
            recovery.Id.ShouldNotBe(producer.Id);
            await fixture.WaitForQuotaProcessCompletionAsync(user.OrganizationId, operation);
            recovery.IsRunning.ShouldBeTrue();
            QuotaProcessSnapshot after = await fixture.ReadQuotaProcessSnapshotAsync(user.OrganizationId, operation);
            after.UploadState.ShouldBe(committed ? 1 : 3);
            after.HoldState.ShouldBe(committed ? 1 : 2);
            after.Effects.ShouldBe(before.Effects);
            ConditionalObjectStat recovered = await storage.StatAsync(after.ObjectKey);
            recovered.State.ShouldBe(committed ? ConditionalObjectState.Provisional : ConditionalObjectState.Cancellation);
            recovered.Length.ShouldBe(committed ? 20 : 0);
            if (committed) recovered.Revision.ShouldBe(storedBeforeKill.Revision);
            else
            {
                using MemoryStream late = new(QuotaProcessPayload);
                (await storage.TryCreateAsync(after.ObjectKey, late, 20, "text/plain", IdentityFrom(after)))
                    .Disposition.ShouldBe(ConditionalObjectWriteDisposition.AlreadyExists);
                (await storage.StatAsync(after.ObjectKey)).Revision.ShouldBe(recovered.Revision);
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    [DataRow("before-sentinel")]
    [DataRow("after-sentinel")]
    [DataRow("after-hold-release")]
    public async Task KilledMaintenanceProcessRecoversItsDurableLeaseAndFenceAcrossCompetingOsWorkers(string phase)
    {
        ProjectsSeededUser user = await fixture.SeedProjectsUserAsync(["documents.manage"]);
        await fixture.SeedQuotaProjectionAsync(user.OrganizationId, limit: 64);
        Guid operation = Guid.CreateVersion7();
        string root = Path.Combine(Path.GetTempPath(), "trykatch-quota-process", operation.ToString("N"));
        try
        {
            await using QuotaChildProcess producer = fixture.StartQuotaChild("quota-producer", root, "after-put", user, operation, pastClock: true);
            await producer.WaitForAsync("quota-checkpoint", "after-put");
            await producer.KillAsync();
            (await fixture.RevokeMembershipAsync(user.OrganizationId, user.ActorId)).ShouldBe(1);
            await using QuotaChildProcess crashed = fixture.StartQuotaChild("quota-worker", root, phase);
            await crashed.WaitForAsync("quota-checkpoint", phase);
            QuotaProcessSnapshot checkpoint = await fixture.ReadQuotaProcessSnapshotAsync(user.OrganizationId, operation);
            checkpoint.QueueState.ShouldBe(1);
            checkpoint.LeaseId.ShouldNotBeNull();
            checkpoint.UploadState.ShouldBe(phase == "after-hold-release" ? 3 : 2);
            checkpoint.HoldState.ShouldBe(phase == "after-hold-release" ? 2 : 0);
            checkpoint.Effects.ShouldBe(new(0, 0, 0, 0, 0));
            using ServiceProvider storageProvider = QuotaStorageObserver(root);
            IConditionalObjectStorage storage = storageProvider.GetRequiredService<IConditionalObjectStorage>();
            ConditionalObjectStat fenced = await storage.StatAsync(checkpoint.ObjectKey);
            fenced.State.ShouldBe(phase == "before-sentinel" ? ConditionalObjectState.Provisional : ConditionalObjectState.Cancellation);
            await crashed.KillAsync();
            (await fixture.ReadQuotaProcessSnapshotAsync(user.OrganizationId, operation)).LeaseId.ShouldBe(checkpoint.LeaseId);

            await using QuotaChildProcess first = fixture.StartQuotaChild("quota-worker", root, documentsAbsent: true);
            AssertQuotaProcessRoles(await first.WaitForAsync("quota-ready"));
            first.IsRunning.ShouldBeTrue();
            (await fixture.ReadQuotaProcessSnapshotAsync(user.OrganizationId, operation)).QueueState.ShouldBe(1,
                "a fresh OS worker must not steal a still-live durable lease");
            await fixture.ExpireQuotaMaintenanceLeaseAsync(checkpoint.WorkId);
            await using QuotaChildProcess second = fixture.StartQuotaChild("quota-worker", root, documentsAbsent: true);
            AssertQuotaProcessRoles(await second.WaitForAsync("quota-ready"));
            first.Id.ShouldNotBe(second.Id);
            first.Id.ShouldNotBe(crashed.Id);
            second.Id.ShouldNotBe(crashed.Id);
            await fixture.WaitForQuotaProcessCompletionAsync(user.OrganizationId, operation);
            first.IsRunning.ShouldBeTrue();
            second.IsRunning.ShouldBeTrue();
            QuotaProcessSnapshot recovered = await fixture.ReadQuotaProcessSnapshotAsync(user.OrganizationId, operation);
            recovered.UploadState.ShouldBe(3);
            recovered.HoldState.ShouldBe(2);
            recovered.LeaseId.ShouldBeNull();
            recovered.Effects.ShouldBe(new(0, 0, 0, 0, 0));
            ConditionalObjectStat sentinel = await storage.StatAsync(recovered.ObjectKey);
            sentinel.State.ShouldBe(ConditionalObjectState.Cancellation);
            sentinel.Length.ShouldBe(0);
            sentinel.Identity.ShouldBe(IdentityFrom(recovered));
            if (phase != "before-sentinel") sentinel.Revision.ShouldBe(fenced.Revision);
            using MemoryStream late = new(QuotaProcessPayload);
            (await storage.TryCreateAsync(recovered.ObjectKey, late, 20, "text/plain", IdentityFrom(recovered)))
                .Disposition.ShouldBe(ConditionalObjectWriteDisposition.AlreadyExists);
            (await storage.StatAsync(recovered.ObjectKey)).Revision.ShouldBe(sentinel.Revision);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static ObjectWriteIdentity IdentityFrom(QuotaProcessSnapshot row) => new(row.OperationId, 20,
        Convert.ToHexStringLower(SHA256.HashData(QuotaProcessPayload)), row.MetadataFingerprint);

    private static ServiceProvider QuotaStorageObserver(string storageRoot)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Storage:LocalPath"] = storageRoot }).Build();
        ServiceCollection services = new();
        services.AddSingleton(configuration);
        services.AddStorageModule(configuration);
        return services.BuildServiceProvider();
    }

    private static void AssertQuotaProcessRoles(JsonElement ready)
    {
        JsonElement roles = ready.GetProperty("roles");
        roles.GetProperty("organization").GetString().ShouldBe(PostgresRuntimeRoleFixture.OrganizationRole);
        roles.GetProperty("platform").GetString().ShouldBe(PostgresRuntimeRoleFixture.PlatformRole);
        roles.GetProperty("identity").GetString().ShouldBe(PostgresRuntimeRoleFixture.IdentityRole);
        roles.GetProperty("outbox").GetString().ShouldBe(PostgresRuntimeRoleFixture.OutboxRole);
        roles.GetProperty("coordinator").GetString().ShouldBe(PostgresRuntimeRoleFixture.WorkCoordinatorRole);
    }

    private sealed record QuotaProcessEffects(long Documents, long Audits, long Outbox, long UsageEffects, long Units);
    private sealed record QuotaProcessSnapshot(Guid WorkId, Guid OperationId, string ObjectKey, string MetadataFingerprint,
        int UploadState, int HoldState, int QueueState, Guid? LeaseId, DateTimeOffset ExpiresAt, QuotaProcessEffects Effects);

    private sealed partial class ExecutionFixture
    {
        public QuotaChildProcess StartQuotaChild(string mode, string storageRoot, string? phase = null,
            ProjectsSeededUser? user = null, Guid? operationId = null, bool pastClock = false, bool documentsAbsent = false)
        {
            IConfiguration configuration = services.GetRequiredService<IConfiguration>();
            Dictionary<string, string?> settings = new()
            {
                ["Storage__LocalPath"] = storageRoot, ["Jobs__Enabled"] = "false",
                ["Email__Host"] = "127.0.0.1", ["Email__Port"] = "1",
                ["Email__From"] = "quota-acceptance@example.test", ["Email__Security"] = "StartTls",
                ["TRYKATCH_ACCEPTANCE_QUOTA_PHASE"] = phase,
                ["TRYKATCH_ACCEPTANCE_ORGANIZATION_ID"] = user?.OrganizationId.ToString(),
                ["TRYKATCH_ACCEPTANCE_ACTOR_ID"] = user?.ActorId.ToString(),
                ["TRYKATCH_ACCEPTANCE_UPLOAD_OPERATION_ID"] = operationId?.ToString(),
                ["TRYKATCH_ACCEPTANCE_QUOTA_CLOCK_UTC"] = pastClock ? DateTimeOffset.UtcNow.AddMinutes(-20).ToString("O", CultureInfo.InvariantCulture) : null,
                ["TRYKATCH_ACCEPTANCE_QUOTA_DOCUMENTS_ABSENT"] = documentsAbsent ? "1" : null
            };
            foreach (string profile in new[] { RuntimeDatabaseConnectionContract.Organization, RuntimeDatabaseConnectionContract.Platform,
                RuntimeDatabaseConnectionContract.Identity, RuntimeDatabaseConnectionContract.Outbox, RuntimeDatabaseConnectionContract.WorkCoordinator })
                settings["ConnectionStrings__" + profile] = RuntimeDatabaseConnectionContract.Get(configuration, profile);
            return QuotaChildProcess.Start(mode, settings);
        }

        public async Task<QuotaProcessSnapshot> ReadQuotaProcessSnapshotAsync(Guid organizationId, Guid operationId)
        {
            await using NpgsqlConnection connection = new(owner);
            await connection.OpenAsync();
            await using NpgsqlCommand command = new("""
                SELECT work."Id", upload."ObjectKey", upload."MetadataFingerprint", upload."State", hold."State", work."State",
                    work."LeaseId", upload."ExpiresAtUtc",
                    (SELECT count(*) FROM app.documents WHERE "OrganizationId"=@organization AND "UploadOperationId"=@operation),
                    (SELECT count(*) FROM platform.audit_entries WHERE "OrganizationId"=@organization AND "Action"='document.uploaded'),
                    (SELECT count(*) FROM platform.outbox_messages WHERE "Type"='Trykatch.Modules.Documents.DocumentChanged'
                        AND "Payload"->>'documentId'=upload."SubjectId"::text AND "Payload"->>'operation'='uploaded'),
                    (SELECT count(*) FROM app.organization_usage WHERE "OrganizationId"=@organization AND "EffectId"=@operation),
                    (SELECT COALESCE(sum("Units"),0)::bigint FROM app.organization_usage WHERE "OrganizationId"=@organization AND "EffectId"=@operation)
                FROM app.module_upload_operations upload
                JOIN app.organization_quota_reservations hold ON hold."OrganizationId"=upload."OrganizationId" AND hold."Id"=upload."ReservationId"
                JOIN infrastructure.quota_maintenance_work work ON work."OrganizationId"=upload."OrganizationId"
                    AND work."UploadOperationId"=upload."OperationId" AND work."Kind"=2
                WHERE upload."OrganizationId"=@organization AND upload."OperationId"=@operation
                """, connection);
            command.Parameters.AddWithValue("organization", organizationId);
            command.Parameters.AddWithValue("operation", operationId);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).ShouldBeTrue();
            return new(reader.GetGuid(0), operationId, reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetInt32(4),
                reader.GetInt32(5), reader.IsDBNull(6) ? null : reader.GetGuid(6), reader.GetFieldValue<DateTimeOffset>(7),
                new(reader.GetInt64(8), reader.GetInt64(9), reader.GetInt64(10), reader.GetInt64(11), reader.GetInt64(12)));
        }

        public async Task WaitForQuotaProcessCompletionAsync(Guid organizationId, Guid operationId)
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
            while ((await ReadQuotaProcessSnapshotAsync(organizationId, operationId)).QueueState != 3)
                await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token);
        }
    }

    private sealed class QuotaChildProcess(Process process) : IAsyncDisposable
    {
        private readonly List<JsonElement> pending = [];
        private readonly Task<string> errors = process.StandardError.ReadToEndAsync();
        internal int Id => process.Id;
        internal bool IsRunning => !process.HasExited;

        internal static QuotaChildProcess Start(string mode, IReadOnlyDictionary<string, string?> settings)
        {
            string assembly = typeof(ModuleWorkExecutionIntegrationTests).Assembly.Location;
            ProcessStartInfo start = new(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (string argument in new[] { "exec", "--runtimeconfig", Path.ChangeExtension(assembly, ".runtimeconfig.json"),
                "--depsfile", Path.ChangeExtension(assembly, ".deps.json"), typeof(ExecutionAcceptanceProcessMarker).Assembly.Location, mode })
                start.ArgumentList.Add(argument);
            Dictionary<string, string?> inherited = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
                .Where(entry => entry.Key is string name && (name is "PATH" or "HOME" or "TMPDIR" or "TMP" or "TEMP"
                    || name.StartsWith("DOTNET_", StringComparison.Ordinal)))
                .ToDictionary(entry => (string)entry.Key!, entry => (string?)entry.Value, StringComparer.Ordinal);
            start.Environment.Clear();
            foreach ((string name, string? value) in inherited.Concat(settings)) if (value is not null) start.Environment[name] = value;
            return new(Process.Start(start) ?? throw new InvalidOperationException("Quota acceptance process did not start."));
        }

        internal async Task<JsonElement> WaitForAsync(string kind, string? phase = null)
        {
            int buffered = pending.FindIndex(item => Matches(item, kind, phase));
            if (buffered >= 0) { JsonElement ready = pending[buffered]; pending.RemoveAt(buffered); return ready; }
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
            while (true)
            {
                string? line = await process.StandardOutput.ReadLineAsync(timeout.Token);
                if (line is null) throw new InvalidOperationException("Quota child exited before checkpoint: " + await errors);
                JsonElement message = JsonSerializer.Deserialize<JsonElement>(line, QuotaProcessJson);
                message.GetProperty("processId").GetInt32().ShouldBe(Id);
                if (message.GetProperty("kind").GetString() == "quota-error")
                    throw new InvalidOperationException("Quota child failed: " + message.GetProperty("code").GetString());
                if (Matches(message, kind, phase)) return message;
                pending.Add(message);
            }
        }

        private static bool Matches(JsonElement item, string kind, string? phase) => item.GetProperty("kind").GetString() == kind
            && (phase is null || item.GetProperty("phase").GetString() == phase);

        internal async Task KillAsync()
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            process.HasExited.ShouldBeTrue();
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                // Checkpoint children deliberately never read stdin. Terminate the exact
                // child so failure cleanup cannot hide an earlier assertion behind a timeout.
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                _ = await errors;
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                process.Dispose();
            }
        }
    }
}

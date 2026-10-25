using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Shouldly;
using Trykatch.ExecutionAcceptance;
using Trykatch.Modules;

namespace Trykatch.IntegrationTests;

[TestClass]
public sealed class LocalConditionalStorageProcessTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string root = Path.Combine(Path.GetTempPath(), "trykatch-conditional-process", Guid.NewGuid().ToString("N"));
    private readonly string key = "_module-uploads/v1/organizations/first/documents/payload";

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    [TestMethod]
    public async Task LocalAdapterIndependentProcessesHaveOneWriterAndReadTheSameEnvelope()
    {
        List<StorageProcess> processes = [];
        try
        {
            for (int index = 0; index < 4; index++) processes.Add(StorageProcess.Start(root));
            processes.Select(process => process.Id).Distinct().Count().ShouldBe(4);
            byte[][] payloads = Enumerable.Range(0, 4).Select(index => Encoding.UTF8.GetBytes("writer-" + index)).ToArray();
            ObjectWriteIdentity[] identities = payloads.Select(Identity).ToArray();
            JsonElement[] writes = await Task.WhenAll(processes.Select((process, index) => process.CommandAsync(
                new { kind = "create", key, payload = payloads[index], identity = identities[index] })));
            int winner = Array.FindIndex(writes, response => Disposition(response) == ConditionalObjectWriteDisposition.Written);
            winner.ShouldBeGreaterThanOrEqualTo(0);
            writes.Count(response => Disposition(response) == ConditionalObjectWriteDisposition.Written).ShouldBe(1);
            writes.Where((_, index) => index != winner).All(response => Disposition(response) == ConditionalObjectWriteDisposition.Conflict)
                .ShouldBeTrue();
            JsonElement read = await processes[(winner + 1) % processes.Count].CommandAsync(new { kind = "get", key });
            read.GetProperty("payload").GetBytesFromBase64().ShouldBe(payloads[winner]);
            read.GetProperty("length").GetInt64().ShouldBe(payloads[winner].LongLength);
            JsonElement stat = await processes[(winner + 2) % processes.Count].CommandAsync(new { kind = "stat", key });
            stat.GetProperty("snapshot").Deserialize<ConditionalObjectStat>(Json)!.Identity.ShouldBe(identities[winner]);
        }
        finally { foreach (StorageProcess process in processes) await process.DisposeAsync(); }
    }

    [TestMethod]
    public async Task LocalAdapterCrashReleasesPermanentKeyLockAndTimeoutDoesNotPublish()
    {
        await using StorageProcess owner = StorageProcess.Start(root);
        await using StorageProcess peer = StorageProcess.Start(root);
        (await owner.CommandAsync(new { kind = "hold-key-lock", key })).GetProperty("kind").GetString().ShouldBe("locked");
        byte[] bytes = [1, 2, 3];
        ObjectWriteIdentity identity = Identity(bytes);
        Stopwatch elapsed = Stopwatch.StartNew();
        Disposition(await peer.CommandAsync(new { kind = "create", key, payload = bytes, identity }))
            .ShouldBe(ConditionalObjectWriteDisposition.Unavailable);
        elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
        File.Exists(Path.Combine(root, key)).ShouldBeFalse();
        string lockPath = Path.Combine(root, "_module-uploads", "v1", ".locks",
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".lock");
        File.Exists(lockPath).ShouldBeTrue();
        await owner.KillAsync();
        File.Exists(lockPath).ShouldBeTrue("A crash must not remove the permanent serialization inode.");
        Disposition(await peer.CommandAsync(new { kind = "create", key, payload = bytes, identity }))
            .ShouldBe(ConditionalObjectWriteDisposition.Written);
        File.Exists(lockPath).ShouldBeTrue();
    }

    [TestMethod]
    public async Task LocalAdapterRefusesRuntimeWithFilesystemLockingDisabled()
    {
        await using StorageProcess process = StorageProcess.Start(root, disableLocking: true);
        byte[] bytes = [4, 5, 6];
        Disposition(await process.CommandAsync(new { kind = "create", key, payload = bytes, identity = Identity(bytes) }))
            .ShouldBe(ConditionalObjectWriteDisposition.Unavailable);
        File.Exists(Path.Combine(root, key)).ShouldBeFalse();
    }

    [TestMethod]
    public async Task LocalAdapterPeerReplacesOnlyExactOwnedRevisionAndSentinelSurvivesRestart()
    {
        await using StorageProcess writer = StorageProcess.Start(root);
        await using StorageProcess peer = StorageProcess.Start(root);
        byte[] bytes = [10, 11, 12];
        ObjectWriteIdentity identity = Identity(bytes);
        Disposition(await writer.CommandAsync(new { kind = "create", key, payload = bytes, identity }))
            .ShouldBe(ConditionalObjectWriteDisposition.Written);
        JsonElement before = await writer.CommandAsync(new { kind = "stat", key });
        ObjectStorageRevision revision = before.GetProperty("snapshot").Deserialize<ConditionalObjectStat>(Json)!.Revision!;
        Disposition(await peer.CommandAsync(new { kind = "cancel", key, revision = new ObjectStorageRevision("stale"), identity }))
            .ShouldBe(ConditionalObjectWriteDisposition.Conflict);
        Disposition(await peer.CommandAsync(new { kind = "cancel", key, revision, identity = Identity(bytes) }))
            .ShouldBe(ConditionalObjectWriteDisposition.Conflict);
        (await writer.CommandAsync(new { kind = "get", key })).GetProperty("payload").GetBytesFromBase64().ShouldBe(bytes);
        Disposition(await peer.CommandAsync(new { kind = "cancel", key, revision, identity }))
            .ShouldBe(ConditionalObjectWriteDisposition.Written);
        Disposition(await writer.CommandAsync(new { kind = "cancel", key, revision, identity }))
            .ShouldBe(ConditionalObjectWriteDisposition.AlreadyExists);
        await peer.KillAsync();
        await using StorageProcess restarted = StorageProcess.Start(root);
        ConditionalObjectStat snapshot = (await restarted.CommandAsync(new { kind = "stat", key }))
            .GetProperty("snapshot").Deserialize<ConditionalObjectStat>(Json)!;
        snapshot.State.ShouldBe(ConditionalObjectState.Cancellation);
        snapshot.Length.ShouldBe(0);
        snapshot.Identity.ShouldBe(identity);
        snapshot.Revision.ShouldNotBe(revision);
        Disposition(await restarted.CommandAsync(new { kind = "create", key, payload = bytes, identity }))
            .ShouldBe(ConditionalObjectWriteDisposition.AlreadyExists);
    }

    [TestMethod]
    public async Task LocalAdapterSlowWriterInAnotherProcessCannotPublishAfterMissingObjectWasFenced()
    {
        await using StorageProcess writer = StorageProcess.Start(root);
        await using StorageProcess reconciler = StorageProcess.Start(root);
        byte[] bytes = [7, 8, 9];
        ObjectWriteIdentity identity = Identity(bytes);
        (await writer.CommandAsync(new { kind = "gate-create", key, payload = bytes, identity }))
            .GetProperty("kind").GetString().ShouldBe("spooling");
        Disposition(await reconciler.CommandAsync(new { kind = "cancel", key, identity }))
            .ShouldBe(ConditionalObjectWriteDisposition.Written);
        Disposition(await writer.CommandAsync(new { kind = "release", key }))
            .ShouldBe(ConditionalObjectWriteDisposition.AlreadyExists);
        await writer.KillAsync();
        await using StorageProcess restarted = StorageProcess.Start(root);
        JsonElement stat = await restarted.CommandAsync(new { kind = "stat", key });
        ConditionalObjectStat snapshot = stat.GetProperty("snapshot").Deserialize<ConditionalObjectStat>(Json)!;
        snapshot.State.ShouldBe(ConditionalObjectState.Cancellation);
        snapshot.Length.ShouldBe(0);
        snapshot.Identity.ShouldBe(identity);
        Disposition(await restarted.CommandAsync(new { kind = "create", key, payload = bytes, identity }))
            .ShouldBe(ConditionalObjectWriteDisposition.AlreadyExists);
        (await restarted.CommandAsync(new { kind = "get", key })).GetProperty("payload").GetBytesFromBase64().ShouldBeEmpty();
    }

    private static ObjectWriteIdentity Identity(byte[] payload) =>
        new(Guid.NewGuid(), payload.LongLength, Convert.ToHexStringLower(SHA256.HashData(payload)), new string('a', 64));

    private static ConditionalObjectWriteDisposition Disposition(JsonElement response) =>
        (ConditionalObjectWriteDisposition)response.GetProperty("result").GetProperty("disposition").GetInt32();

    private sealed class StorageProcess(Process process) : IAsyncDisposable
    {
        private bool killed;
        private bool commandFailed;
        internal int Id => process.Id;

        internal static StorageProcess Start(string storageRoot, bool disableLocking = false)
        {
            string assembly = typeof(LocalConditionalStorageProcessTests).Assembly.Location;
            ProcessStartInfo start = new(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
                RedirectStandardError = true, CreateNoWindow = true
            };
            start.ArgumentList.Add("exec");
            start.ArgumentList.Add("--runtimeconfig");
            start.ArgumentList.Add(Path.ChangeExtension(assembly, ".runtimeconfig.json"));
            start.ArgumentList.Add("--depsfile");
            start.ArgumentList.Add(Path.ChangeExtension(assembly, ".deps.json"));
            start.ArgumentList.Add(typeof(ExecutionAcceptanceProcessMarker).Assembly.Location);
            start.ArgumentList.Add("local-storage");
            Dictionary<string, string?> inherited = Environment.GetEnvironmentVariables()
                .Cast<System.Collections.DictionaryEntry>().Where(entry => entry.Key is string name
                    && (name == "PATH" || name == "HOME" || name.StartsWith("DOTNET_", StringComparison.Ordinal)
                        || name is "TMPDIR" or "TMP" or "TEMP"))
                .ToDictionary(entry => (string)entry.Key!, entry => (string?)entry.Value, StringComparer.Ordinal);
            start.Environment.Clear();
            foreach ((string name, string? value) in inherited) if (value is not null) start.Environment[name] = value;
            start.Environment["Storage__LocalPath"] = storageRoot;
            start.Environment["DOTNET_SYSTEM_IO_DISABLEFILELOCKING"] = disableLocking ? "1" : "0";
            return new(Process.Start(start) ?? throw new InvalidOperationException("Local storage process did not start."));
        }

        internal async Task<JsonElement> CommandAsync<T>(T command)
        {
            try { return await CommandCoreAsync(command); }
            catch { commandFailed = true; throw; }
        }

        private async Task<JsonElement> CommandCoreAsync<T>(T command)
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(command, Json));
            await process.StandardInput.FlushAsync(timeout.Token);
            string? line = await process.StandardOutput.ReadLineAsync(timeout.Token);
            if (line is null) throw new InvalidOperationException("Local storage process exited: "
                + await process.StandardError.ReadToEndAsync(timeout.Token));
            JsonElement response = JsonSerializer.Deserialize<JsonElement>(line, Json);
            response.GetProperty("processId").GetInt32().ShouldBe(process.Id);
            response.GetProperty("kind").GetString().ShouldNotBe("error", line);
            return response;
        }

        internal async Task KillAsync()
        {
            killed = true;
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }

        public async ValueTask DisposeAsync()
        {
            try { process.StandardInput.Close(); }
            catch (IOException) when (process.HasExited) { }
            try
            {
                (int exit, string output, string error) = await ProcessExecution.WaitAsync(process, TimeSpan.FromSeconds(10));
                if (!killed && !commandFailed) exit.ShouldBe(0, output + error);
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

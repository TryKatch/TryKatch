using System.Diagnostics;
using System.Globalization;
using Shouldly;

namespace Trykatch.IntegrationTests;

[TestClass]
public sealed class ProcessExecutionTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AnInterruptedChildAndItsDescendantExitBeforeTheCallerRegainsControl(bool cancel)
    {
        if (OperatingSystem.IsWindows()) Assert.Inconclusive("The stalled process-tree fixture uses the Unix shell.");
        string directory = Directory.CreateTempSubdirectory("trykatch-process-fixture-").FullName;
        string pidFile = Path.Combine(directory, "child.pid");
        ProcessStartInfo start = new("/bin/sh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("sleep 60 & child=$!; printf '%s' \"$child\" > \"$1\"; printf 'fixture stdout\\n'; printf 'fixture stderr\\n' >&2; wait \"$child\"");
        start.ArgumentList.Add("stalled-fixture");
        start.ArgumentList.Add(pidFile);
        using Process process = Process.Start(start)!;
        try
        {
            using CancellationTokenSource fixtureDeadline = new(TimeSpan.FromSeconds(5));
            while (!File.Exists(pidFile) || new FileInfo(pidFile).Length == 0) await Task.Delay(10, fixtureDeadline.Token);
            using Process descendant = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(pidFile, fixtureDeadline.Token), CultureInfo.InvariantCulture));
            using CancellationTokenSource cancellation = new();
            if (cancel) cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));
            Task wait = ProcessExecution.WaitAsync(process, cancel ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(100), cancellation.Token);
            if (cancel) await Should.ThrowAsync<OperationCanceledException>(() => wait);
            else await Should.ThrowAsync<TimeoutException>(() => wait);
            process.HasExited.ShouldBeTrue();
            await descendant.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            descendant.HasExited.ShouldBeTrue();
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            Directory.Delete(directory, recursive: true);
        }
    }
}

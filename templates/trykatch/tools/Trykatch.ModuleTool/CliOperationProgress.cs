using System.Diagnostics;

namespace Trykatch.ModuleTool;

/// <summary>Terminal-only presentation; the generator remains independent of console rendering.</summary>
internal sealed class CliOperationProgress : IDisposable
{
    private static readonly string[] Frames = ["|", "/", "-", "\\"];
    private readonly TextWriter _output;
    private readonly bool _interactive;
    private readonly string _operation;
    private readonly object _gate = new();
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private readonly Stopwatch _stepElapsed = new();
    private readonly Timer? _timer;
    private string? _step;
    private int _number;
    private int _frame;
    private int _lineWidth;
    private bool _finished;
    private bool _disposed;
    private bool _rollingBack;

    internal CliOperationProgress(TextWriter output, bool interactive, string operation = "Module generation")
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        _output = output;
        _interactive = interactive;
        _operation = operation;
        if (interactive)
            _timer = new Timer(_ => Refresh(), null, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100));
    }

    internal void Report(ModuleCreationProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        lock (_gate)
        {
            if (_finished || _disposed) return;
            WriteSafely(() =>
            {
                if (_step is not null) FinishStep(progress.IsRollback ? "FAIL" : "OK");
                _rollingBack = progress.IsRollback;
                _step = progress.Step;
                _number++;
                _stepElapsed.Restart();
                if (_interactive) Render();
                else _output.WriteLine($"[{_number}] {_step}...");
                _output.Flush();
            });
        }
    }

    internal void ReportStep(string step) => Report(new ModuleCreationProgress(step));

    internal void Complete() => Finish(succeeded: true);

    internal void Fail() => Finish(succeeded: false);

    private void Finish(bool succeeded)
    {
        lock (_gate)
        {
            if (_finished || _disposed) return;
            WriteSafely(() =>
            {
                if (_step is not null)
                {
                    // A failed rollback must not be presented as successfully restored.
                    if (!succeeded && _rollingBack) ClearLine();
                    else FinishStep(succeeded ? "OK" : "FAIL");
                }
                _finished = true;
                _output.WriteLine(succeeded
                    ? $"{_operation} completed in {_elapsed.Elapsed.TotalSeconds:0.0}s."
                    : $"{_operation} stopped after {_elapsed.Elapsed.TotalSeconds:0.0}s.");
                _output.Flush();
            });
        }
    }

    internal void Refresh()
    {
        lock (_gate)
        {
            if (_finished || _disposed || _step is null) return;
            WriteSafely(() =>
            {
                Render();
                _output.Flush();
            });
        }
    }

    private void Render()
    {
        string line = $"{Frames[_frame++ % Frames.Length]} [{_number}] {_step}... ({_stepElapsed.Elapsed.TotalSeconds:0.0}s)";
        _output.Write('\r');
        _output.Write(line);
        if (line.Length < _lineWidth) _output.Write(new string(' ', _lineWidth - line.Length));
        _lineWidth = line.Length;
    }

    private void FinishStep(string status)
    {
        ClearLine();
        _output.WriteLine($"{status} [{_number}] {_step} ({_stepElapsed.Elapsed.TotalSeconds:0.0}s)");
    }

    private void ClearLine()
    {
        if (!_interactive || _lineWidth == 0) return;
        _output.Write('\r');
        _output.Write(new string(' ', _lineWidth));
        _output.Write('\r');
        _lineWidth = 0;
    }

    private void WriteSafely(Action write)
    {
        try { write(); }
        catch (IOException) { _finished = true; }
        catch (ObjectDisposedException) { _finished = true; }
        // A closed output pipe must not abort workspace restoration or crash the timer thread.
    }

    public void Dispose()
    {
        _timer?.Dispose();
        lock (_gate)
        {
            if (_disposed) return;
            WriteSafely(ClearLine);
            _disposed = true;
        }
    }
}

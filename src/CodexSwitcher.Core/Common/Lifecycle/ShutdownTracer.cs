using System.Diagnostics;
using System.Text;

namespace CodexSwitcher.Core.Common.Lifecycle;

/// <summary>
/// Recorded execution timing for an individual phase of application shutdown.
/// </summary>
public sealed class ShutdownPhaseTiming
{
    public string PhaseName { get; }
    public long StartTimestampMs { get; }
    public long EndTimestampMs { get; }
    public double ElapsedMilliseconds { get; }

    public ShutdownPhaseTiming(string phaseName, long startTimestampMs, long endTimestampMs, double elapsedMs)
    {
        PhaseName = phaseName;
        StartTimestampMs = startTimestampMs;
        EndTimestampMs = endTimestampMs;
        ElapsedMilliseconds = elapsedMs;
    }
}

/// <summary>
/// High-resolution monotonic shutdown tracer measuring bounded phase latencies
/// from close request to final process exit.
/// </summary>
public sealed class ShutdownTracer
{
    private readonly Stopwatch _stopwatch = new();
    private readonly List<ShutdownPhaseTiming> _phases = [];
    private readonly object _lock = new();

    public bool IsStarted => _stopwatch.IsRunning;
    public double TotalElapsedMilliseconds => _stopwatch.Elapsed.TotalMilliseconds;

    public IReadOnlyList<ShutdownPhaseTiming> Phases
    {
        get
        {
            lock (_lock)
            {
                return _phases.ToArray();
            }
        }
    }

    public void Start()
    {
        _stopwatch.Restart();
    }

    public void MeasurePhase(string phaseName, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var start = _stopwatch.ElapsedMilliseconds;
        try
        {
            action();
        }
        finally
        {
            var end = _stopwatch.ElapsedMilliseconds;
            lock (_lock)
            {
                _phases.Add(new ShutdownPhaseTiming(phaseName, start, end, end - start));
            }
        }
    }

    public async Task MeasurePhaseAsync(string phaseName, Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var start = _stopwatch.ElapsedMilliseconds;
        try
        {
            await action().ConfigureAwait(false);
        }
        finally
        {
            var end = _stopwatch.ElapsedMilliseconds;
            lock (_lock)
            {
                _phases.Add(new ShutdownPhaseTiming(phaseName, start, end, end - start));
            }
        }
    }

    public string FormatSummary()
    {
        lock (_lock)
        {
            var sb = new StringBuilder();
            sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"Shutdown completed in {TotalElapsedMilliseconds:F1} ms across {_phases.Count} phases:");
            foreach (var phase in _phases)
            {
                sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"  - {phase.PhaseName}: {phase.ElapsedMilliseconds:F1} ms (offset: {phase.StartTimestampMs} ms -> {phase.EndTimestampMs} ms)");
            }
            return sb.ToString();
        }
    }
}

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

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
/// Recorded execution timing for an individual milestone of application shutdown.
/// </summary>
public sealed class ShutdownMilestoneTiming
{
    public string MilestoneName { get; }
    public long Timestamp { get; }
    public double ElapsedMillisecondsFromStart { get; }

    public ShutdownMilestoneTiming(string milestoneName, long timestamp, double elapsedMillisecondsFromStart)
    {
        MilestoneName = milestoneName;
        Timestamp = timestamp;
        ElapsedMillisecondsFromStart = elapsedMillisecondsFromStart;
    }
}

/// <summary>
/// High-resolution monotonic shutdown tracer measuring bounded phase latencies and milestones (S0-S16)
/// from close request to final process exit.
/// Strictly excludes sensitive credentials, emails, and tokens.
/// </summary>
public sealed class ShutdownTracer
{
    private static readonly ShutdownTracer s_instance = new();
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };
    public static ShutdownTracer Instance => s_instance;

    private readonly Stopwatch _stopwatch = new();
    private readonly List<ShutdownPhaseTiming> _phases = [];
    private readonly List<ShutdownMilestoneTiming> _milestones = [];
    private readonly object _lock = new();
    private long _startTimestamp;

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

    public IReadOnlyList<ShutdownMilestoneTiming> Milestones
    {
        get
        {
            lock (_lock)
            {
                return _milestones.ToArray();
            }
        }
    }

    public void Start()
    {
        _startTimestamp = Stopwatch.GetTimestamp();
        _stopwatch.Restart();
        RecordMilestone("S0:CloseRequested");
    }

    public void RecordMilestone(string name)
    {
        var ts = Stopwatch.GetTimestamp();
        lock (_lock)
        {
            if (_startTimestamp == 0)
            {
                _startTimestamp = ts;
                if (!_stopwatch.IsRunning)
                {
                    _stopwatch.Start();
                }
            }
            var elapsedMs = (ts - _startTimestamp) * 1000.0 / Stopwatch.Frequency;
            _milestones.Add(new ShutdownMilestoneTiming(name, ts, elapsedMs));
        }
    }

    public double? GetMilestoneElapsed(string name)
    {
        lock (_lock)
        {
            return _milestones.FirstOrDefault(m => m.MilestoneName.Equals(name, StringComparison.OrdinalIgnoreCase))?.ElapsedMillisecondsFromStart;
        }
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
            sb.AppendLine(CultureInfo.InvariantCulture, $"Shutdown completed in {TotalElapsedMilliseconds:F1} ms across {_phases.Count} phases and {_milestones.Count} milestones:");
            if (_milestones.Count > 0)
            {
                sb.AppendLine("Milestones:");
                foreach (var m in _milestones)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"  - {m.MilestoneName}: {m.ElapsedMillisecondsFromStart:F1} ms");
                }
            }
            if (_phases.Count > 0)
            {
                sb.AppendLine("Phases:");
                foreach (var phase in _phases)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"  - {phase.PhaseName}: {phase.ElapsedMilliseconds:F1} ms (offset: {phase.StartTimestampMs} ms -> {phase.EndTimestampMs} ms)");
                }
            }
            return sb.ToString();
        }
    }

    public void FlushToFile(string? directoryPath = null)
    {
        try
        {
            var dir = directoryPath
                ?? System.Environment.GetEnvironmentVariable("CODEX_SWITCHBOARD_PERF_DIR")
                ?? Path.Combine(Path.GetTempPath(), "codex-switchboard-perf");

            Directory.CreateDirectory(dir);
            var filePath = Path.Combine(dir, "shutdown-latest.json");
            var summaryPath = Path.Combine(dir, "shutdown-latest.txt");

            var payload = new
            {
                recordedAt = DateTimeOffset.UtcNow,
                totalElapsedMs = TotalElapsedMilliseconds,
                windowHiddenMs = GetMilestoneElapsed("S15:WindowHidden"),
                processExitMs = GetMilestoneElapsed("S16:ProcessExited") ?? TotalElapsedMilliseconds,
                milestones = Milestones.Select(m => new { m.MilestoneName, m.ElapsedMillisecondsFromStart }),
                phases = Phases.Select(p => new { p.PhaseName, p.ElapsedMilliseconds, p.StartTimestampMs, p.EndTimestampMs })
            };

            File.WriteAllText(filePath, JsonSerializer.Serialize(payload, s_jsonOptions));
            File.WriteAllText(summaryPath, FormatSummary());
        }
        catch { }
    }
}

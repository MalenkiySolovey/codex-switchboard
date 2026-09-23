using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CodexSwitcher.Core.Common.Lifecycle;

/// <summary>
/// Recorded execution timing for an individual milestone of application startup.
/// </summary>
public sealed class StartupMilestoneTiming
{
    public string MilestoneName { get; }
    public long Timestamp { get; }
    public double ElapsedMillisecondsFromStart { get; }

    public StartupMilestoneTiming(string milestoneName, long timestamp, double elapsedMillisecondsFromStart)
    {
        MilestoneName = milestoneName;
        Timestamp = timestamp;
        ElapsedMillisecondsFromStart = elapsedMillisecondsFromStart;
    }
}

/// <summary>
/// High-resolution monotonic startup tracer measuring milestones (T0-T15)
/// from process entry point to first interactive frame and deferred completion.
/// Strictly excludes sensitive credentials, emails, and tokens.
/// </summary>
public sealed class StartupTracer
{
    private static readonly long s_processStartTimestamp = Stopwatch.GetTimestamp();
    private static readonly StartupTracer s_instance = new(s_processStartTimestamp);
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    public static StartupTracer Instance => s_instance;

    private readonly long _startTimestamp;
    private readonly List<StartupMilestoneTiming> _milestones = [];
    private readonly object _lock = new();

    public long StartTimestamp => _startTimestamp;

    public StartupTracer(long startTimestamp)
    {
        _startTimestamp = startTimestamp;
        RecordMilestone("T0:ProcessEntry", startTimestamp);
    }

    public void RecordMilestone(string name)
    {
        var ts = Stopwatch.GetTimestamp();
        RecordMilestone(name, ts);
    }

    public void RecordMilestone(string name, long timestamp)
    {
        var elapsedMs = (timestamp - _startTimestamp) * 1000.0 / Stopwatch.Frequency;
        lock (_lock)
        {
            _milestones.Add(new StartupMilestoneTiming(name, timestamp, elapsedMs));
        }
    }

    public IReadOnlyList<StartupMilestoneTiming> Milestones
    {
        get
        {
            lock (_lock)
            {
                return _milestones.ToArray();
            }
        }
    }

    public double? GetMilestoneElapsed(string name)
    {
        lock (_lock)
        {
            return _milestones.FirstOrDefault(m => m.MilestoneName.Equals(name, StringComparison.OrdinalIgnoreCase))?.ElapsedMillisecondsFromStart;
        }
    }

    public string FormatSummary()
    {
        lock (_lock)
        {
            var sb = new StringBuilder();
            sb.AppendLine(CultureInfo.InvariantCulture, $"Startup milestones ({_milestones.Count} recorded):");
            foreach (var m in _milestones)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  - {m.MilestoneName}: {m.ElapsedMillisecondsFromStart:F1} ms");
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
            var filePath = Path.Combine(dir, "startup-latest.json");
            var summaryPath = Path.Combine(dir, "startup-latest.txt");

            var payload = new
            {
                recordedAt = DateTimeOffset.UtcNow,
                totalInteractiveMs = GetMilestoneElapsed("T12:UIInteractive") ?? GetMilestoneElapsed("T11:ShellLoaded"),
                milestones = Milestones.Select(m => new { m.MilestoneName, m.ElapsedMillisecondsFromStart })
            };

            File.WriteAllText(filePath, JsonSerializer.Serialize(payload, s_jsonOptions));
            File.WriteAllText(summaryPath, FormatSummary());
        }
        catch { }
    }
}

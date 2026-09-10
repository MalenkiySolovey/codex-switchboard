using System.Globalization;

namespace CodexSwitcher.Core.Support;

/// <summary>
/// Formats token counts, durations, and streak metrics for display.
/// Preserves exact underlying Int64 values while providing compact human-readable presentation.
/// </summary>
public static class UsageMetricFormatter
{
    /// <summary>
    /// Formats token count with K/M/B abbreviations (e.g. 1.13B, 201.8M, 12.4K, 532).
    /// Returns <paramref name="nullFallback"/> for null inputs.
    /// </summary>
    public static string FormatTokenCount(long? tokens, string nullFallback = "—")
    {
        if (!tokens.HasValue)
            return nullFallback;

        var val = tokens.Value;
        if (val < 0)
            val = 0;

        if (val >= 1_000_000_000)
            return (val / 1_000_000_000.0).ToString("F2", CultureInfo.InvariantCulture) + "B";

        if (val >= 1_000_000)
            return (val / 1_000_000.0).ToString("F1", CultureInfo.InvariantCulture) + "M";

        if (val >= 1_000)
            return (val / 1_000.0).ToString("F1", CultureInfo.InvariantCulture) + "K";

        return val.ToString("N0", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Formats turn/task duration in seconds to compact representation (e.g. 59s, 12m 04s, 1h 41m).
    /// </summary>
    public static string FormatTurnDuration(long? seconds, string nullFallback = "—")
    {
        if (!seconds.HasValue || seconds.Value < 0)
            return nullFallback;

        var sec = seconds.Value;
        if (sec >= 3600)
        {
            var hours = sec / 3600;
            var mins = (sec % 3600) / 60;
            return $"{hours}h {mins:D2}m";
        }

        if (sec >= 60)
        {
            var mins = sec / 60;
            var s = sec % 60;
            return $"{mins}m {s:D2}s";
        }

        return $"{sec}s";
    }

    /// <summary>
    /// Formats streak days (e.g. 2d).
    /// </summary>
    public static string FormatStreak(long? days, string nullFallback = "—")
    {
        if (!days.HasValue || days.Value < 0)
            return nullFallback;

        return $"{days.Value}d";
    }
}

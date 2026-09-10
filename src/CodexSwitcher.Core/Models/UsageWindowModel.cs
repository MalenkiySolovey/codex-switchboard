namespace CodexSwitcher.Core.Models;

/// <summary>
/// Normalized immutable presentation model for an individual quota window.
/// Supports arbitrary durations (e.g. 45m, 120m, 300m, 10080m) without hardcoded window assumptions.
/// </summary>
public sealed record UsageWindowModel(
    string Slot,
    int? DurationMinutes,
    string DisplayLabel,
    double? UsedPercent,
    double? RemainingPercent,
    DateTimeOffset? ResetsAt,
    bool IsExhausted,
    bool IsLowQuota)
{
    /// <summary>
    /// Calculates whether the window has an active reset scheduled in the future.
    /// </summary>
    public bool HasFutureReset(DateTimeOffset now) =>
        ResetsAt is { } reset && reset > now;

    /// <summary>
    /// Calculates remaining time until reset relative to <paramref name="now"/>.
    /// Returns null if resetsAt is null or in the past.
    /// </summary>
    public TimeSpan? TimeUntilReset(DateTimeOffset now) =>
        ResetsAt is { } reset && reset > now ? reset - now : null;
}

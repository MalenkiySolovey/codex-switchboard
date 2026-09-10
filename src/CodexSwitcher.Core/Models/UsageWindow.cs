namespace CodexSwitcher.Core.Models;

/// <summary>
/// A normalized rate-limit window. Window duration in minutes (<see cref="DurationMinutes"/>) is authoritative.
/// Display labels (e.g. "5h", "7d") are derived conveniences.
/// </summary>
public sealed record UsageWindow(
    string Slot,
    int? DurationMinutes,
    string DisplayLabel,
    double? UsedPercent,
    double? RemainingPercent,
    DateTimeOffset? ResetsAt)
{
    public TimeSpan? TimeUntilReset(DateTimeOffset now) =>
        ResetsAt is { } reset && reset > now ? reset - now : null;
}

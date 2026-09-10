namespace CodexSwitcher.Core.Models;

/// <summary>
/// A normalized snapshot of rate limits for an account.
/// </summary>
public sealed record RateLimitsSnapshot(
    Guid ProfileId,
    DateTimeOffset ObservedAt,
    string? PrimaryLimitId,
    IReadOnlyList<LimitBucket> Limits,
    int? ResetCreditsAvailable,
    string? PlanType,
    string? AccountEmail,
    UsageStatus Status,
    ErrorInfo? LastError = null,
    RateLimitResetCredits? ResetCreditsDetail = null)
{
    private LimitBucket? PrimaryBucket =>
        Limits.FirstOrDefault(b => b.LimitId == (PrimaryLimitId ?? "codex"))
        ?? Limits.FirstOrDefault();

    /// <summary>The first window in the primary bucket (or null if none).</summary>
    public UsageWindow? PrimaryWindow =>
        PrimaryBucket?.Windows.FirstOrDefault();

    /// <summary>The secondary window in the primary bucket (or null if none).</summary>
    public UsageWindow? SecondaryWindow =>
        PrimaryBucket?.Windows.Skip(1).FirstOrDefault();

    /// <summary>Finds a window by exact duration in minutes (e.g. 300 for 5h, 10080 for 7d).</summary>
    public UsageWindow? FindWindowByDuration(int durationMinutes) =>
        PrimaryBucket?.Windows.FirstOrDefault(w => w.DurationMinutes == durationMinutes)
        ?? Limits.SelectMany(b => b.Windows).FirstOrDefault(w => w.DurationMinutes == durationMinutes);
}

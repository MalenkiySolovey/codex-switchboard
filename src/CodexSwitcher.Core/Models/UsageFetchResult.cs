namespace CodexSwitcher.Core.Models;

/// <summary>
/// Result of querying rate limits from an isolated account sandbox.
/// </summary>
public sealed record UsageFetchResult(
    bool Success,
    RateLimitsSnapshot? Snapshot,
    UsageStatus Status,
    bool SandboxAuthMutated = false,
    byte[]? RotatedAuthJson = null,
    ErrorInfo? Error = null,
    AccountActivitySnapshot? Activity = null,
    AccountActivityAvailability ActivityAvailability = AccountActivityAvailability.Unknown)
{
    public static UsageFetchResult Ok(
        RateLimitsSnapshot snapshot,
        AccountActivitySnapshot? activity = null,
        bool sandboxAuthMutated = false,
        byte[]? rotatedAuthJson = null,
        AccountActivityAvailability activityAvailability = AccountActivityAvailability.Unknown)
    {
        var effAvailability = activityAvailability != AccountActivityAvailability.Unknown
            ? activityAvailability
            : (activity is not null ? AccountActivityAvailability.Available : AccountActivityAvailability.NotReported);
        return new(true, snapshot, snapshot.Status, sandboxAuthMutated, rotatedAuthJson, null, activity, effAvailability);
    }

    public static UsageFetchResult Partial(
        RateLimitsSnapshot? snapshot,
        AccountActivitySnapshot? activity,
        UsageStatus status,
        bool sandboxAuthMutated = false,
        byte[]? rotatedAuthJson = null,
        ErrorInfo? error = null,
        AccountActivityAvailability activityAvailability = AccountActivityAvailability.Unknown)
    {
        var effAvailability = activityAvailability != AccountActivityAvailability.Unknown
            ? activityAvailability
            : (activity is not null ? AccountActivityAvailability.Available : AccountActivityAvailability.TemporarilyUnavailable);
        return new(snapshot is not null || activity is not null, snapshot, status, sandboxAuthMutated, rotatedAuthJson, error, activity, effAvailability);
    }

    public static UsageFetchResult Fail(
        UsageStatus status,
        ErrorInfo error,
        AccountActivityAvailability activityAvailability = AccountActivityAvailability.TemporarilyUnavailable) =>
        new(false, null, status, false, null, error, null, activityAvailability);
}

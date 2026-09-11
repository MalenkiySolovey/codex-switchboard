using CodexSwitcher.Core.Accounts.Contracts;
using CodexSwitcher.Core.Accounts.Formatting;
using CodexSwitcher.Core.Accounts.Models;
using CodexSwitcher.Core.Accounts.Services;
using CodexSwitcher.Core.Common.Dispatcher;
using CodexSwitcher.Core.Common.Enums;
using CodexSwitcher.Core.Common.Environment;
using CodexSwitcher.Core.Common.Lifecycle;
using CodexSwitcher.Core.Common.Logging;
using CodexSwitcher.Core.Common.Storage;
using CodexSwitcher.Core.Common.Time;
using CodexSwitcher.Core.Providers.Catalog;
using CodexSwitcher.Core.Providers.Contracts;
using CodexSwitcher.Core.Providers.Models;
using CodexSwitcher.Core.Providers.Services;
using CodexSwitcher.Core.Routing.Contracts;
using CodexSwitcher.Core.Routing.Models;
using CodexSwitcher.Core.Routing.Services;
using CodexSwitcher.Core.Security.Secrets;
using CodexSwitcher.Core.Security.Totp;
using CodexSwitcher.Core.Security.Verification;
using CodexSwitcher.Core.Settings.Contracts;
using CodexSwitcher.Core.Settings.Models;
using CodexSwitcher.Core.Threads.Contracts;
using CodexSwitcher.Core.Threads.Models;
using CodexSwitcher.Core.Transfer.Contracts;
using CodexSwitcher.Core.Transfer.Models;
using CodexSwitcher.Core.Transfer.Services;
using CodexSwitcher.Core.Usage.Contracts;
using CodexSwitcher.Core.Usage.Formatting;
using CodexSwitcher.Core.Usage.Models;
using CodexSwitcher.Core.Usage.Services;
using CodexSwitcher.Core.Common.Errors;

namespace CodexSwitcher.Core.Usage.Models;

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

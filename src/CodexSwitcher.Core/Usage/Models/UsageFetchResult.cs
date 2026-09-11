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

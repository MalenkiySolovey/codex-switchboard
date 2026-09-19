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
using CodexSwitcher.Core.Usage.Services;
using CodexSwitcher.Core.Common.Errors;
using CodexSwitcher.Core.Usage.Models;

namespace CodexSwitcher.Core.Accounts.Models;

/// <summary>
/// Normalized immutable presentation state for an account's usage and rate limits.
/// </summary>
public sealed record AccountUsageState(
    Guid ProfileId,
    UsageStatus Status,
    UsageVisualState VisualState,
    bool IsStale,
    bool IsRefreshing,
    string? PlanType,
    int? ResetCreditsAvailable,
    DateTimeOffset? ObservedAt,
    IReadOnlyList<UsageWindowModel> Windows,
    string? NoticeMessage,
    ErrorInfo? LastError = null,
    bool CredentialConflict = false,
    CredentialConflictReason ConflictReason = CredentialConflictReason.None,
    AccountActivitySnapshot? Activity = null,
    AccountActivityAvailability ActivityAvailability = AccountActivityAvailability.Unknown,
    RateLimitResetCredits? ResetCreditsDetail = null,
    bool? OrdinaryUsageAllowed = null)
{
    /// <summary>Returns true if this account has any quota windows defined.</summary>
    public bool HasWindows => Windows.Count > 0;

    /// <summary>Returns true if this account has historical activity data.</summary>
    public bool HasActivity => Activity is not null;

    /// <summary>Returns true if a notice banner should be displayed.</summary>
    public bool HasNotice => !string.IsNullOrWhiteSpace(NoticeMessage);

    /// <summary>
    /// Creates a state representing an account whose usage has never been loaded.
    /// </summary>
    public static AccountUsageState NeverLoadedState(Guid profileId) =>
        new(
            ProfileId: profileId,
            Status: UsageStatus.Unknown,
            VisualState: UsageVisualState.NeverLoaded,
            IsStale: false,
            IsRefreshing: false,
            PlanType: null,
            ResetCreditsAvailable: null,
            ObservedAt: null,
            Windows: Array.Empty<UsageWindowModel>(),
            NoticeMessage: null);

    /// <summary>
    /// Creates a state representing an in-flight refresh while preserving previous window data.
    /// </summary>
    public AccountUsageState AsRefreshing() =>
        this with { IsRefreshing = true, VisualState = UsageVisualState.Refreshing };
}

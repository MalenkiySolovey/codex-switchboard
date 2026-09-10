namespace CodexSwitcher.Core.Models;

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
    RateLimitResetCredits? ResetCreditsDetail = null)
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

namespace CodexSwitcher.Core.Models;

/// <summary>
/// A normalized, secret-free cached entry for a profile's rate limits and status.
/// Never contains access tokens, refresh tokens, auth.json, or sensitive RPC payloads.
/// </summary>
public sealed record UsageCacheEntry(
    Guid ProfileId,
    DateTimeOffset ObservedAt,
    RateLimitsSnapshot? Snapshot,
    UsageStatus Status,
    ErrorInfo? LastError,
    bool IsStale,
    bool CredentialConflict = false,
    CredentialConflictReason ConflictReason = CredentialConflictReason.None,
    AccountActivitySnapshot? Activity = null,
    AccountActivityAvailability ActivityAvailability = AccountActivityAvailability.Unknown)
{
    /// <summary>
    /// Effective conflict reason, guaranteeing that legacy entries with CredentialConflict = true
    /// map to at least CredentialConflictReason.Unknown.
    /// </summary>
    public CredentialConflictReason EffectiveConflictReason =>
        ConflictReason != CredentialConflictReason.None
            ? ConflictReason
            : (CredentialConflict ? CredentialConflictReason.Unknown : CredentialConflictReason.None);
}

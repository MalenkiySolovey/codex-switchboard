using CodexSwitcher.Core.Accounts.Contracts;
using CodexSwitcher.Core.Accounts.Formatting;
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
using CodexSwitcher.Core.Accounts.Models;
using CodexSwitcher.Core.Common.Errors;

namespace CodexSwitcher.Core.Usage.Models;

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

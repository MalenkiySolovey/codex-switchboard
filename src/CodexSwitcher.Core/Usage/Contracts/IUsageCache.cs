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
using CodexSwitcher.Core.Usage.Services;
using CodexSwitcher.Core.Accounts.Models;
using CodexSwitcher.Core.Common.Errors;
using CodexSwitcher.Core.Usage.Models;

namespace CodexSwitcher.Core.Usage.Contracts;

/// <summary>
/// Cache layer for non-secret rate-limit snapshots and statuses.
/// Maintains an in-memory dictionary backed by atomic JSON persistence on disk.
/// </summary>
public interface IUsageCache
{
    /// <summary>
    /// Gets the cached usage entry for a profile, if present.
    /// </summary>
    UsageCacheEntry? Get(Guid profileId);

    /// <summary>
    /// Gets all cached usage entries.
    /// </summary>
    IReadOnlyDictionary<Guid, UsageCacheEntry> GetAll();

    /// <summary>
    /// Updates or inserts a cached usage entry in memory.
    /// </summary>
    void Set(
        Guid profileId,
        RateLimitsSnapshot? snapshot,
        UsageStatus status,
        ErrorInfo? lastError = null,
        bool isStale = false,
        bool credentialConflict = false,
        CredentialConflictReason conflictReason = CredentialConflictReason.None,
        AccountActivitySnapshot? activity = null,
        AccountActivityAvailability activityAvailability = AccountActivityAvailability.Unknown);

    /// <summary>
    /// Invalidates and removes the cached entry for a profile (e.g. upon profile deletion).
    /// </summary>
    void Invalidate(Guid profileId);

    /// <summary>
    /// Loads cached entries from disk. Restored entries are marked as stale/unverified.
    /// Gracefully tolerates missing, empty, or corrupt files without throwing.
    /// </summary>
    Task LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically persists the current non-secret usage cache to disk.
    /// </summary>
    Task SaveAsync(CancellationToken cancellationToken = default);
}

using CodexSwitcher.Core.Models;

namespace CodexSwitcher.Core.Abstractions;

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

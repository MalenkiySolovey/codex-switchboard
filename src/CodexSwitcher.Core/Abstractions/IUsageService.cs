using CodexSwitcher.Core.Models;

namespace CodexSwitcher.Core.Abstractions;

/// <summary>
/// Orchestrates rate-limit querying and usage caching across multiple account profiles.
/// Manages bounded concurrency, duplicate refresh coalescing, retry policy,
/// caller cancellation isolation, and credential rotation CAS safety.
/// </summary>
public interface IUsageService
{
    /// <summary>
    /// Gets the current cached usage entry for a profile, if any.
    /// </summary>
    UsageCacheEntry? GetCached(Guid profileId);

    /// <summary>
    /// Carrega o cache persistido em disco para a memória na inicialização.
    /// </summary>
    Task LoadCacheAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all cached usage entries.
    /// </summary>
    IReadOnlyDictionary<Guid, UsageCacheEntry> GetAllCached();

    /// <summary>
    /// Refreshes rate limits for a single profile.
    /// Coalesces concurrent calls for the same profile onto a single in-flight operation.
    /// </summary>
    Task<UsageFetchResult> RefreshAsync(
        ProfileMetadata profile,
        bool force = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Refreshes rate limits for a list of profiles under configured bounded concurrency.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, UsageFetchResult>> RefreshAllAsync(
        IReadOnlyList<ProfileMetadata> profiles,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Invalidates cached usage for a profile (e.g. when removed).
    /// </summary>
    void Invalidate(Guid profileId);
}

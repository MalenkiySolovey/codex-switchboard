using CodexSwitcher.Core.Accounts.Contracts;
using CodexSwitcher.Core.Accounts.Formatting;
using CodexSwitcher.Core.Accounts.Services;
using CodexSwitcher.Core.Common.Dispatcher;
using CodexSwitcher.Core.Common.Enums;
using CodexSwitcher.Core.Common.Environment;
using CodexSwitcher.Core.Common.Errors;
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
using CodexSwitcher.Core.Usage.Models;

namespace CodexSwitcher.Core.Usage.Contracts;

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
    /// Refreshes rate limits for a list of profiles with progressive per-account completion notifications.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, UsageFetchResult>> RefreshAllAsync(
        IReadOnlyList<ProfileMetadata> profiles,
        Action<Guid, UsageFetchResult>? onAccountCompleted,
        CancellationToken cancellationToken = default) =>
        RefreshAllAsync(profiles, cancellationToken);

    /// <summary>
    /// Invalidates cached usage for a profile (e.g. when removed).
    /// </summary>
    void Invalidate(Guid profileId);
}

using CodexSwitcher.Core.Accounts.Contracts;
using CodexSwitcher.Core.Accounts.Formatting;
using CodexSwitcher.Core.Accounts.Models;
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
using CodexSwitcher.Core.Providers.Contracts;

namespace CodexSwitcher.Core.Providers.Catalog;

/// <summary>
/// Thread-safe singleton service providing access to the loaded provider catalog,
/// on-demand reloading, and exact descriptor lookup and matching.
/// </summary>
public sealed class ProviderCatalogService : IProviderCatalogService
{
    private readonly ProviderCatalogLoader _loader;
    private readonly object _lock = new();
    private CatalogLoadResult _currentResult;

    public ProviderCatalogService(ProviderCatalogLoader loader)
    {
        _loader = loader ?? throw new ArgumentNullException(nameof(loader));
        _currentResult = _loader.LoadCatalog();
    }

    public CatalogLoadResult CurrentResult
    {
        get
        {
            lock (_lock)
            {
                return _currentResult;
            }
        }
    }

    public CatalogLoadResult Reload()
    {
        lock (_lock)
        {
            _currentResult = _loader.LoadCatalog();
            return _currentResult;
        }
    }

    public ProviderDescriptor? GetDescriptor(string? providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId)) return null;
        var catalog = CurrentResult.Catalog;
        return catalog.Providers.FirstOrDefault(p => string.Equals(p.Id, providerId, StringComparison.OrdinalIgnoreCase));
    }

    public ProviderDescriptor? MatchDescriptor(string? hostOrUrl)
    {
        if (string.IsNullOrWhiteSpace(hostOrUrl)) return null;
        var catalog = CurrentResult.Catalog;
        return catalog.Providers.FirstOrDefault(p => ProviderMatcher.Matches(p, hostOrUrl));
    }
}

using CodexSwitcher.Core.Abstractions;

namespace CodexSwitcher.Core.Catalog;

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

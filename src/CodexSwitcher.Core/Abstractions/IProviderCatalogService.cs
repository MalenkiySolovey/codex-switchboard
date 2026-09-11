using CodexSwitcher.Core.Catalog;

namespace CodexSwitcher.Core.Abstractions;

/// <summary>
/// Service providing access to the data-driven Provider Catalog.
/// </summary>
public interface IProviderCatalogService
{
    CatalogLoadResult CurrentResult { get; }
    ProviderCatalog Catalog => CurrentResult.Catalog;
    CatalogLoadResult Reload();
    ProviderDescriptor? GetDescriptor(string? providerId);
    ProviderDescriptor? MatchDescriptor(string? hostOrUrl);
}

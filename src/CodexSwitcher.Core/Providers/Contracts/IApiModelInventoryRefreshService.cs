using CodexSwitcher.Core.Providers.Models;

namespace CodexSwitcher.Core.Providers.Contracts;

/// <summary>
/// Single application-level operation for refreshing a persisted API
/// profile's model inventory. The operation owns discovery, merge,
/// persistence, catalog regeneration, active-target reconciliation, and
/// monotonic result publication.
/// </summary>
public interface IApiModelInventoryRefreshService
{
    event EventHandler<ModelInventoryRefreshResult>? RefreshStateChanged;

    ModelInventoryRefreshResult? GetLatestResult(Guid profileId);

    Task<ModelInventoryRefreshResult> RefreshAsync(
        Guid profileId,
        RefreshModelsOptions? options = null,
        CancellationToken cancellationToken = default);
}

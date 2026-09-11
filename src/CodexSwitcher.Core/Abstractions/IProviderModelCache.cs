namespace CodexSwitcher.Core.Abstractions;

public sealed record ProviderModelCacheEntry(
    Guid ProfileId,
    string RouteKey,
    int Revision,
    IReadOnlyList<string> Models,
    DateTimeOffset CachedAt);

/// <summary>
/// Route-specific model discovery cache keyed by (profileId, routeId/baseUrl, revision).
/// Ensures model truth is preserved across route changes and shared with cross-provider thread handoff/fork.
/// </summary>
public interface IProviderModelCache
{
    bool TryGetModels(Guid profileId, string routeKey, int revision, out IReadOnlyList<string> models);
    void SetModels(Guid profileId, string routeKey, int revision, IReadOnlyList<string> models);
    void Invalidate(Guid profileId, string? routeKey = null);
    IReadOnlyList<string>? GetLatestModels(Guid profileId);
}

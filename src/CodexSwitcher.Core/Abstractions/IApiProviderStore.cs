using CodexSwitcher.Core.Models;

namespace CodexSwitcher.Core.Abstractions;

/// <summary>
/// Persistent store for user-configured API provider profile metadata.
/// Guaranteed not to store or expose plaintext secrets.
/// </summary>
public interface IApiProviderStore
{
    IReadOnlyList<ApiProviderProfile> GetAll();
    ApiProviderProfile? GetById(Guid id);
    ApiProviderProfile? GetByStableCodexProviderId(string stableId);
    void Save(ApiProviderProfile profile);
    void SaveAll(IEnumerable<ApiProviderProfile> profiles);
    bool Delete(Guid id);
    bool SetStatus(Guid id, ApiProviderProfileStatus status);
}

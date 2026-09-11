using System.Text.Json;
using System.Text.Json.Serialization;
using CodexSwitcher.Core.Abstractions;
using CodexSwitcher.Core.Models;

namespace CodexSwitcher.Core.Services;

/// <summary>
/// Persists API provider metadata to api-providers.json with atomic writes and credential status validation.
/// Plaintext secrets are strictly excluded from this store.
/// </summary>
public sealed class ApiProviderStore : IApiProviderStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IFileSystem _fs;
    private readonly string _filePath;
    private readonly IApiKeySecretStore? _secretStore;
    private readonly object _sync = new();

    public ApiProviderStore(IFileSystem fs, string filePath, IApiKeySecretStore? secretStore = null)
    {
        _fs = fs ?? throw new ArgumentNullException(nameof(fs));
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        _secretStore = secretStore;
    }

    public IReadOnlyList<ApiProviderProfile> GetAll()
    {
        lock (_sync)
        {
            var profiles = LoadInternal();
            if (_secretStore is not null)
            {
                foreach (var profile in profiles)
                {
                    if (profile.Status != ApiProviderProfileStatus.Archived && !_secretStore.HasApiKey(profile.Id))
                    {
                        profile.Status = ApiProviderProfileStatus.CredentialMissing;
                    }
                }
            }
            return profiles.OrderBy(p => p.SortOrder).ThenBy(p => p.DisplayName).ToList();
        }
    }

    public ApiProviderProfile? GetById(Guid id)
    {
        lock (_sync)
        {
            return GetAll().FirstOrDefault(p => p.Id == id);
        }
    }

    public ApiProviderProfile? GetByStableCodexProviderId(string stableId)
    {
        if (string.IsNullOrWhiteSpace(stableId)) return null;
        lock (_sync)
        {
            return GetAll().FirstOrDefault(p => string.Equals(p.StableCodexProviderId, stableId, StringComparison.OrdinalIgnoreCase));
        }
    }

    public void Save(ApiProviderProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        lock (_sync)
        {
            var list = LoadInternal();
            var index = list.FindIndex(p => p.Id == profile.Id);
            if (index >= 0)
            {
                list[index] = profile;
            }
            else
            {
                list.Add(profile);
            }
            SaveInternal(list);
        }
    }

    public void SaveAll(IEnumerable<ApiProviderProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        lock (_sync)
        {
            SaveInternal(profiles.ToList());
        }
    }

    public bool Delete(Guid id)
    {
        lock (_sync)
        {
            var list = LoadInternal();
            var removed = list.RemoveAll(p => p.Id == id) > 0;
            if (removed)
            {
                SaveInternal(list);
            }
            return removed;
        }
    }

    public bool SetStatus(Guid id, ApiProviderProfileStatus status)
    {
        lock (_sync)
        {
            var list = LoadInternal();
            var profile = list.FirstOrDefault(p => p.Id == id);
            if (profile is null) return false;
            profile.Status = status;
            SaveInternal(list);
            return true;
        }
    }

    private List<ApiProviderProfile> LoadInternal()
    {
        if (!_fs.FileExists(_filePath))
            return [];

        try
        {
            var json = _fs.ReadAllText(_filePath);
            if (string.IsNullOrWhiteSpace(json))
                return [];

            return JsonSerializer.Deserialize<List<ApiProviderProfile>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private void SaveInternal(List<ApiProviderProfile> profiles)
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir) && !_fs.DirectoryExists(dir))
        {
            _fs.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(profiles, JsonOptions);
        _fs.WriteAllTextAtomic(_filePath, json);
    }
}

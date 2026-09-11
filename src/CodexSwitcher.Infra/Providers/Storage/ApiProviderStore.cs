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
using CodexSwitcher.Infra.Accounts.Storage;
using CodexSwitcher.Infra.Codex.Routing;
using CodexSwitcher.Infra.Codex.Runtime;
using CodexSwitcher.Infra.Codex.Threads;
using CodexSwitcher.Infra.Codex.Usage;
using CodexSwitcher.Infra.Common.Logging;
using CodexSwitcher.Infra.Common.Paths;
using CodexSwitcher.Infra.Common.Storage;
using CodexSwitcher.Infra.Common.Time;
using CodexSwitcher.Infra.Providers.Inspection;
using CodexSwitcher.Infra.Providers.Secrets;
using CodexSwitcher.Infra.Providers.Storage;
using CodexSwitcher.Infra.Scheduling;
using CodexSwitcher.Infra.Security.Dpapi;
using CodexSwitcher.Infra.Security.Hardening;
using CodexSwitcher.Infra.Security.Totp;
using CodexSwitcher.Infra.Settings;
using CodexSwitcher.Core.Providers.Contracts;
using CodexSwitcher.Core.Providers.Models;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexSwitcher.Infra.Providers.Storage;

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

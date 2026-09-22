using CodexSwitcher.Core.Common.Errors;
using CodexSwitcher.Core.Common.Storage;
using CodexSwitcher.Core.Providers.Contracts;
using CodexSwitcher.Core.Providers.Models;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexSwitcher.Infra.Providers.Storage;

/// <summary>
/// Persists API provider metadata to api-providers.json with atomic writes, rolling backups,
/// anti-truncation invariants, and corrupt-index recovery fail-safe.
/// Plaintext secrets are strictly excluded from this store.
/// </summary>
public sealed class ApiProviderStore : IApiProviderStore
{
    private const int MaxRollingBackups = 10;

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
    private string? _lastSavedJson;
    private int? _lastKnownCount;

    public ApiProviderStore(IFileSystem fs, string filePath, IApiKeySecretStore? secretStore = null)
    {
        _fs = fs ?? throw new ArgumentNullException(nameof(fs));
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        _secretStore = secretStore;
    }

    private string GetBackupsDir()
    {
        var parentDir = Path.GetDirectoryName(_filePath);
        return string.IsNullOrEmpty(parentDir)
            ? Path.Combine("backups", "api-providers")
            : Path.Combine(parentDir, "backups", "api-providers");
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
                    var secretOwnerId = profile.EndpointId ?? profile.Id;
                    if (profile.Status != ApiProviderProfileStatus.Archived && !_secretStore.HasApiKey(secretOwnerId))
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
            SaveInternal(list, ApiProviderSaveIntent.NormalUpdate);
        }
    }

    public void SaveAll(IEnumerable<ApiProviderProfile> profiles, ApiProviderSaveIntent intent = ApiProviderSaveIntent.NormalUpdate)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        lock (_sync)
        {
            SaveInternal(profiles.ToList(), intent);
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
                SaveInternal(list, ApiProviderSaveIntent.ExplicitDelete);
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
            SaveInternal(list, ApiProviderSaveIntent.NormalUpdate);
            return true;
        }
    }

    private List<ApiProviderProfile> LoadInternal()
    {
        if (!_fs.FileExists(_filePath))
        {
            _lastKnownCount = 0;
            _lastSavedJson = null;
            return [];
        }

        string json;
        try
        {
            json = _fs.ReadAllText(_filePath);
        }
        catch (Exception ex)
        {
            return AttemptBackupRecoveryOrThrow("Falha ao ler api-providers.json do disco.", ex);
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            _lastKnownCount = 0;
            _lastSavedJson = null;
            return [];
        }

        try
        {
            var list = JsonSerializer.Deserialize<List<ApiProviderProfile>>(json, JsonOptions);
            if (list == null)
            {
                return AttemptBackupRecoveryOrThrow("Desserialização de api-providers.json retornou nulo.", null);
            }

            _lastSavedJson = json;
            _lastKnownCount = list.Count;
            return list;
        }
        catch (JsonException ex)
        {
            return AttemptBackupRecoveryOrThrow("JSON malformado em api-providers.json.", ex);
        }
    }

    private void SaveInternal(List<ApiProviderProfile> profiles, ApiProviderSaveIntent intent)
    {
        // 1. Verificação de unicidade de IDs
        var uniqueIds = new HashSet<Guid>();
        foreach (var p in profiles)
        {
            if (!uniqueIds.Add(p.Id))
            {
                throw new InvalidOperationException($"ID de provedor API duplicado '{p.Id}' detectado na coleção a ser gravada.");
            }
        }

        // 2. Determina contagem existente no disco
        var existingCount = GetCurrentProfileCountUnderLock();

        // 3. Invariante anti-truncamento: NormalUpdate NUNCA pode reduzir o número de perfis
        if (intent == ApiProviderSaveIntent.NormalUpdate && profiles.Count < existingCount)
        {
            throw new ApiProviderTruncationException(existingCount, profiles.Count);
        }

        var json = JsonSerializer.Serialize(profiles, JsonOptions);
        if (string.Equals(_lastSavedJson, json, StringComparison.Ordinal))
        {
            return;
        }

        // 4. Backup rotativo antes de modificar o arquivo se já existia conteúdo válido
        if (existingCount > 0 && _fs.FileExists(_filePath))
        {
            CreateBackupUnderLock();
        }

        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir) && !_fs.DirectoryExists(dir))
        {
            _fs.CreateDirectory(dir);
        }

        _fs.WriteAllTextAtomic(_filePath, json);
        _lastSavedJson = json;
        _lastKnownCount = profiles.Count;

        // 5. Rotação / expurgo de backups antigos
        PruneBackupsUnderLock();
    }

    private int GetCurrentProfileCountUnderLock()
    {
        if (_lastKnownCount.HasValue)
        {
            return _lastKnownCount.Value;
        }

        if (!_fs.FileExists(_filePath))
        {
            _lastKnownCount = 0;
            return 0;
        }

        try
        {
            var existingJson = _fs.ReadAllText(_filePath);
            if (string.IsNullOrWhiteSpace(existingJson))
            {
                _lastKnownCount = 0;
                return 0;
            }

            var existingList = JsonSerializer.Deserialize<List<ApiProviderProfile>>(existingJson, JsonOptions);
            var count = existingList?.Count ?? 0;
            _lastKnownCount = count;
            return count;
        }
        catch
        {
            return _lastKnownCount ?? 0;
        }
    }

    private void CreateBackupUnderLock()
    {
        try
        {
            var backupsDir = GetBackupsDir();
            _fs.CreateDirectory(backupsDir);

            var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture);
            var backupPath = Path.Combine(backupsDir, $"api-providers.{timestamp}.json");
            var currentJson = _fs.ReadAllText(_filePath);
            _fs.WriteAllTextAtomic(backupPath, currentJson);
        }
        catch
        {
            // Falha na criação do backup não deve impedir a escrita principal se esta for válida
        }
    }

    private void PruneBackupsUnderLock()
    {
        try
        {
            var backupsDir = GetBackupsDir();
            if (!_fs.DirectoryExists(backupsDir))
                return;

            var files = _fs.EnumerateFiles(backupsDir, "api-providers.*.json")
                .OrderByDescending(f => f)
                .ToList();

            if (files.Count > MaxRollingBackups)
            {
                for (int i = MaxRollingBackups; i < files.Count; i++)
                {
                    try { _fs.Delete(files[i]); } catch { }
                }
            }
        }
        catch { }
    }

    private List<ApiProviderProfile> AttemptBackupRecoveryOrThrow(string reason, Exception? inner)
    {
        var backupsDir = GetBackupsDir();
        if (_fs.DirectoryExists(backupsDir))
        {
            var backupFiles = _fs.EnumerateFiles(backupsDir, "api-providers.*.json")
                .OrderByDescending(f => f)
                .ToList();

            foreach (var backupFile in backupFiles)
            {
                try
                {
                    var backupJson = _fs.ReadAllText(backupFile);
                    if (!string.IsNullOrWhiteSpace(backupJson))
                    {
                        var list = JsonSerializer.Deserialize<List<ApiProviderProfile>>(backupJson, JsonOptions);
                        if (list is not null && list.Count > 0)
                        {
                            // Recuperado com sucesso de um backup anterior válido! Restaura atomicamente
                            _fs.WriteAllTextAtomic(_filePath, backupJson);
                            _lastSavedJson = backupJson;
                            _lastKnownCount = list.Count;
                            return list;
                        }
                    }
                }
                catch
                {
                    // Tenta o próximo backup mais antigo
                }
            }
        }

        throw new CorruptApiProviderIndexException(
            $"{reason} Nenhum backup rotativo válido encontrado em '{backupsDir}'. Operação abortada para evitar conversão silenciosa em estado vazio.",
            inner);
    }
}

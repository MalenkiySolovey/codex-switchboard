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
using CodexSwitcher.Core.Accounts.Contracts;
using CodexSwitcher.Core.Accounts.Models;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexSwitcher.Infra.Accounts.Storage;

/// <summary>
/// Persiste os metadados dos perfis (profiles.json), separados do blob cifrado. Escrita atômica.
/// Sem segredos. Ver BUSINESS_RULES.md §2.1/§2.2 e §9 (separação metadados × blob).
/// Endurecido com invariantes anti-truncamento, backups rotativos atômicos e fail-safe contra corrupção.
/// </summary>
public sealed class ProfileStore : CodexSwitcher.Core.Accounts.Contracts.IProfileStore
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
    private readonly string _profilesPath;
    private readonly object _sync = new();
    private string? _lastSavedJson;
    private int? _lastKnownCount;

    public ProfileStore(IFileSystem fs, string profilesPath)
    {
        _fs = fs ?? throw new ArgumentNullException(nameof(fs));
        _profilesPath = profilesPath ?? throw new ArgumentNullException(nameof(profilesPath));
    }

    private string GetBackupsDir()
    {
        var parentDir = Path.GetDirectoryName(_profilesPath);
        return string.IsNullOrEmpty(parentDir)
            ? Path.Combine("backups", "profiles")
            : Path.Combine(parentDir, "backups", "profiles");
    }

    /// <summary>
    /// Carrega todos os perfis.
    /// Arquivo ausente -> lista vazia legítima (primeira execução).
    /// Arquivo corrompido -> tenta restauração por backup rotativo; falha seguro com exceção se irrecuperável.
    /// </summary>
    public List<ProfileMetadata> LoadAll()
    {
        lock (_sync)
        {
            if (!_fs.FileExists(_profilesPath))
            {
                _lastKnownCount = 0;
                _lastSavedJson = null;
                return [];
            }

            string json;
            try
            {
                json = _fs.ReadAllText(_profilesPath);
            }
            catch (Exception ex)
            {
                return AttemptBackupRecoveryOrThrow("Falha ao ler profiles.json do disco.", ex);
            }

            if (string.IsNullOrWhiteSpace(json))
            {
                _lastKnownCount = 0;
                _lastSavedJson = null;
                return [];
            }

            try
            {
                var list = JsonSerializer.Deserialize<List<ProfileMetadata>>(json, JsonOptions);
                if (list == null)
                {
                    return AttemptBackupRecoveryOrThrow("Desserialização de profiles.json retornou nulo.", null);
                }

                _lastSavedJson = json;
                _lastKnownCount = list.Count;
                return list;
            }
            catch (JsonException ex)
            {
                return AttemptBackupRecoveryOrThrow("JSON malformado em profiles.json.", ex);
            }
        }
    }

    /// <summary>
    /// Grava todos os perfis atomicamente, respeitando a intenção semântica e invariantes anti-perda de dados.
    /// </summary>
    public void SaveAll(IEnumerable<ProfileMetadata> profiles, ProfileSaveIntent intent = ProfileSaveIntent.NormalUpdate)
    {
        ArgumentNullException.ThrowIfNull(profiles);

        lock (_sync)
        {
            var snapshot = profiles.ToList();

            // 1. Verificação de unicidade de IDs
            var uniqueIds = new HashSet<Guid>();
            foreach (var p in snapshot)
            {
                if (!uniqueIds.Add(p.Id))
                {
                    throw new InvalidOperationException($"ID de perfil duplicado '{p.Id}' detectado na coleção a ser gravada.");
                }
            }

            // 2. Determina contagem existente no disco
            var existingCount = GetCurrentProfileCountUnderLock();

            // 3. Invariante anti-truncamento: NormalUpdate NUNCA pode reduzir o número de perfis
            if (intent == ProfileSaveIntent.NormalUpdate && snapshot.Count < existingCount)
            {
                throw new ProfileTruncationException(existingCount, snapshot.Count);
            }

            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            if (string.Equals(_lastSavedJson, json, StringComparison.Ordinal))
            {
                return;
            }

            // 4. Backup rotativo antes de modificar o arquivo se já existia conteúdo válido
            if (existingCount > 0 && _fs.FileExists(_profilesPath))
            {
                CreateBackupUnderLock();
            }

            var dir = Path.GetDirectoryName(_profilesPath);
            if (!string.IsNullOrEmpty(dir))
                _fs.CreateDirectory(dir);

            _fs.WriteAllTextAtomic(_profilesPath, json);
            _lastSavedJson = json;
            _lastKnownCount = snapshot.Count;

            // 5. Rotação / expurgo de backups antigos (mantém os MaxRollingBackups mais recentes)
            PruneBackupsUnderLock();
        }
    }

    private int GetCurrentProfileCountUnderLock()
    {
        if (_lastKnownCount.HasValue)
        {
            return _lastKnownCount.Value;
        }

        if (!_fs.FileExists(_profilesPath))
        {
            _lastKnownCount = 0;
            return 0;
        }

        try
        {
            var existingJson = _fs.ReadAllText(_profilesPath);
            if (string.IsNullOrWhiteSpace(existingJson))
            {
                _lastKnownCount = 0;
                return 0;
            }

            var existingList = JsonSerializer.Deserialize<List<ProfileMetadata>>(existingJson, JsonOptions);
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
            var backupPath = Path.Combine(backupsDir, $"profiles.{timestamp}.json");
            var currentJson = _fs.ReadAllText(_profilesPath);
            _fs.WriteAllTextAtomic(backupPath, currentJson);
        }
        catch
        {
            // Falha na criação do backup não deve impedir a escrita principal se esta for válida,
            // mas é tratada com segurança.
        }
    }

    private void PruneBackupsUnderLock()
    {
        try
        {
            var backupsDir = GetBackupsDir();
            if (!_fs.DirectoryExists(backupsDir))
                return;

            var files = _fs.EnumerateFiles(backupsDir, "profiles.*.json")
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

    private List<ProfileMetadata> AttemptBackupRecoveryOrThrow(string reason, Exception? inner)
    {
        var backupsDir = GetBackupsDir();
        if (_fs.DirectoryExists(backupsDir))
        {
            var backupFiles = _fs.EnumerateFiles(backupsDir, "profiles.*.json")
                .OrderByDescending(f => f)
                .ToList();

            foreach (var backupFile in backupFiles)
            {
                try
                {
                    var backupJson = _fs.ReadAllText(backupFile);
                    if (!string.IsNullOrWhiteSpace(backupJson))
                    {
                        var list = JsonSerializer.Deserialize<List<ProfileMetadata>>(backupJson, JsonOptions);
                        if (list is not null && list.Count > 0)
                        {
                            // Recuperado com sucesso de um backup anterior válido! Restaura atomicamente
                            _fs.WriteAllTextAtomic(_profilesPath, backupJson);
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

        throw new CorruptProfileIndexException(
            $"{reason} Nenhum backup rotativo válido encontrado em '{backupsDir}'. Operação abortada para evitar conversão silenciosa em estado vazio.",
            inner);
    }
}

using CodexSwitcher.Core.Accounts.Contracts;
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
using CodexSwitcher.Core.Accounts.Models;
using System.Text.Json;

namespace CodexSwitcher.Core.Transfer.Services;

/// <summary>
/// Safe copy-based legacy data migration from %LOCALAPPDATA%\CodexSwitcher to %LOCALAPPDATA%\CodexSwitchboard.
/// Invariants:
/// 1. Source legacy files are NEVER moved or deleted (100% read-only touch).
/// 2. Atomic writes to destination root.
/// 3. DPAPI CurrentUser encrypted blobs are copied byte-for-byte without decryption churn.
/// 4. Validates metadata and vault blobs before accepting.
/// 5. Refuses to overwrite destination if already populated.
/// </summary>
public sealed class LegacyMigrationService : ILegacyMigrationService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IFileSystem _fs;
    private readonly string _legacyRoot;
    private readonly string _destinationRoot;

    public LegacyMigrationService(IFileSystem fs, string legacyRoot, string destinationRoot)
    {
        _fs = fs ?? throw new ArgumentNullException(nameof(fs));
        _legacyRoot = legacyRoot ?? throw new ArgumentNullException(nameof(legacyRoot));
        _destinationRoot = destinationRoot ?? throw new ArgumentNullException(nameof(destinationRoot));
    }

    public bool CanMigrate()
    {
        // If destination root is already populated with profiles or vault data, refuse migration
        var destProfiles = Path.Combine(_destinationRoot, "profiles.json");
        var destVault = Path.Combine(_destinationRoot, "vault");
        if (_fs.FileExists(destProfiles)) return false;
        if (_fs.DirectoryExists(destVault) && _fs.EnumerateFiles(destVault, "*.bin").Count > 0) return false;

        // Check legacy presence
        if (!_fs.DirectoryExists(_legacyRoot)) return false;

        var legacyProfiles = Path.Combine(_legacyRoot, "profiles.json");
        var legacyVault = Path.Combine(_legacyRoot, "vault");

        var hasProfiles = _fs.FileExists(legacyProfiles);
        var hasVault = _fs.DirectoryExists(legacyVault) && _fs.EnumerateFiles(legacyVault, "*.bin").Count > 0;

        return hasProfiles || hasVault;
    }

    public Task<LegacyMigrationResult> MigrateAsync(CancellationToken ct = default)
    {
        // Guard 1: Destination check
        var destProfiles = Path.Combine(_destinationRoot, "profiles.json");
        var destVault = Path.Combine(_destinationRoot, "vault");
        if (_fs.FileExists(destProfiles) || (_fs.DirectoryExists(destVault) && _fs.EnumerateFiles(destVault, "*.bin").Count > 0))
        {
            return Task.FromResult(LegacyMigrationResult.Failed(
                "Destination data root already contains data. Migration refused to prevent silent overwrite.",
                destinationAlreadyExisted: true));
        }

        // Guard 2: Source check
        if (!_fs.DirectoryExists(_legacyRoot))
        {
            return Task.FromResult(LegacyMigrationResult.Failed(
                "Legacy directory not found.",
                noLegacyDataFound: true));
        }

        var legacyProfilesPath = Path.Combine(_legacyRoot, "profiles.json");
        var legacyVaultDir = Path.Combine(_legacyRoot, "vault");
        var legacySettingsPath = Path.Combine(_legacyRoot, "settings.json");

        var hasProfiles = _fs.FileExists(legacyProfilesPath);
        var hasVault = _fs.DirectoryExists(legacyVaultDir);

        if (!hasProfiles && (!hasVault || _fs.EnumerateFiles(legacyVaultDir, "*.bin").Count == 0))
        {
            return Task.FromResult(LegacyMigrationResult.Failed(
                "No legacy profile or vault data found to migrate.",
                noLegacyDataFound: true));
        }

        List<ProfileMetadata> profiles;
        string rawProfilesJson = "[]";

        if (hasProfiles)
        {
            rawProfilesJson = _fs.ReadAllText(legacyProfilesPath);
            try
            {
                profiles = JsonSerializer.Deserialize<List<ProfileMetadata>>(rawProfilesJson, JsonOptions)
                    ?? new List<ProfileMetadata>();
            }
            catch (JsonException ex)
            {
                return Task.FromResult(LegacyMigrationResult.Failed(
                    $"Legacy profiles metadata is corrupt: {ex.Message}"));
            }
        }
        else
        {
            profiles = new List<ProfileMetadata>();
        }

        // Validate that every profile has a valid, non-empty vault file
        if (hasProfiles && profiles.Count > 0)
        {
            if (!hasVault)
            {
                return Task.FromResult(LegacyMigrationResult.Failed(
                    "Legacy vault directory is missing despite profile metadata presence."));
            }

            foreach (var p in profiles)
            {
                if (p.Id == Guid.Empty)
                {
                    return Task.FromResult(LegacyMigrationResult.Failed(
                        "Legacy profile has an empty or invalid GUID identifier."));
                }

                var blobPath = Path.Combine(legacyVaultDir, $"{p.Id}.bin");
                if (!_fs.FileExists(blobPath))
                {
                    return Task.FromResult(LegacyMigrationResult.Failed(
                        $"Legacy vault item for profile '{p.AccountEmail ?? p.DisplayName}' ({p.Id}) is missing."));
                }

                var bytes = _fs.ReadAllBytes(blobPath);
                if (bytes.Length == 0)
                {
                    return Task.FromResult(LegacyMigrationResult.Failed(
                        $"Legacy vault item for profile '{p.AccountEmail ?? p.DisplayName}' ({p.Id}) is zero bytes / corrupt."));
                }
            }
        }

        // Prepare destination directories
        _fs.CreateDirectory(_destinationRoot);
        _fs.CreateDirectory(destVault);
        _fs.CreateDirectory(Path.Combine(_destinationRoot, "backups"));
        _fs.CreateDirectory(Path.Combine(_destinationRoot, "work"));

        // Copy vault blobs atomically
        int copiedCount = 0;
        if (hasVault)
        {
            var vaultFiles = _fs.EnumerateFiles(legacyVaultDir, "*.bin");
            foreach (var vf in vaultFiles)
            {
                var bytes = _fs.ReadAllBytes(vf);
                var fileName = Path.GetFileName(vf);
                var destFile = Path.Combine(destVault, fileName);
                _fs.WriteAllBytesAtomic(destFile, bytes);
                copiedCount++;
            }
        }

        // Copy profiles.json atomically
        if (hasProfiles)
        {
            _fs.WriteAllTextAtomic(destProfiles, rawProfilesJson);
        }

        // Copy settings.json if present and valid
        if (_fs.FileExists(legacySettingsPath))
        {
            try
            {
                var settingsText = _fs.ReadAllText(legacySettingsPath);
                var settingsObj = JsonSerializer.Deserialize<AppSettings>(settingsText, JsonOptions);
                if (settingsObj != null)
                {
                    _fs.WriteAllTextAtomic(Path.Combine(_destinationRoot, "settings.json"), settingsText);
                }
            }
            catch
            {
                // Ignored: corrupt settings does not block profile migration
            }
        }

        int count = profiles.Count > 0 ? profiles.Count : copiedCount;
        return Task.FromResult(LegacyMigrationResult.Succeeded(
            count,
            $"Migrated {count} account(s) safely to Codex Switchboard."));
    }
}

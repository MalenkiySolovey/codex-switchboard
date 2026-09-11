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
using CodexSwitcher.Core.Accounts.Models;

namespace CodexSwitcher.Infra.Common.Paths;

/// <summary>
/// Resolve os caminhos do app (%LOCALAPPDATA%\CodexSwitcher\...) e do Codex (CODEX_HOME ou
/// %USERPROFILE%\.codex). Usa Local (não Roaming), pois DPAPI CurrentUser não é portável (§2.1/§7).
/// </summary>
public sealed class AppPaths
{
    public static string DefaultRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexSwitchboard");

    public static string LegacyDefaultRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexSwitcher");

    public string Root { get; }
    public string LegacyRoot { get; }
    public string VaultDir => Path.Combine(Root, "vault");
    public string BackupsDir => Path.Combine(Root, "backups");
    public string TotpDir => Path.Combine(Root, "totp");
    public string ProfilesPath => Path.Combine(Root, "profiles.json");
    public string SettingsPath => Path.Combine(Root, "settings.json");
    public string AuditLogPath => Path.Combine(Root, "audit.log");
    public string UsageCachePath => Path.Combine(Root, "usage-cache.json");

    public string CatalogDir => Path.Combine(Root, "catalog");
    public string CatalogPath => Path.Combine(CatalogDir, "providers.catalog.json");
    public string CatalogSigPath => Path.Combine(CatalogDir, "providers.catalog.sig");
    public string CatalogPreviousPath => Path.Combine(CatalogDir, "providers.catalog.previous.json");
    public string LocalCatalogPath => Path.Combine(CatalogDir, "providers.local.json");
    public string ApiProvidersPath => Path.Combine(Root, "api-providers.json");
    public string BrokerDir => Path.Combine(Root, "broker");
    public string BrokerExecutablePath => Path.Combine(BrokerDir, "CodexSwitchboard.KeyBroker.exe");
    public string ApiKeysDir => Path.Combine(Root, "api-keys");
    public string LegacyApiKeysDir => Path.Combine(Root, "keys");

    /// <summary>Caminho do arquivo de credencial TOTP cifrado de um perfil.</summary>
    public string GetTotpPath(Guid profileId) => Path.Combine(TotpDir, $"{profileId:N}.bin");

    /// <summary>Caminho canônico do arquivo de chave API cifrada de um perfil de provedor.</summary>
    public string GetApiKeyPath(Guid profileId) => Path.Combine(ApiKeysDir, $"{profileId:N}.bin");

    /// <summary>Caminho legado (desenvolvimento / builds anteriores) do arquivo de chave API cifrada.</summary>
    public string GetLegacyApiKeyPath(Guid profileId) => Path.Combine(LegacyApiKeysDir, $"{profileId:N}.bin");

    /// <summary>
    /// Migra atomicamente um blob de chave API da pasta legada 'keys' para a pasta canônica 'api-keys',
    /// sem decifrar os dados. Se ambos os arquivos existirem, o canônico é preservado e não sobrescrito.
    /// </summary>
    public bool MigrateLegacyApiKeyIfNeeded(Guid profileId)
    {
        var canonical = GetApiKeyPath(profileId);
        var legacy = GetLegacyApiKeyPath(profileId);

        if (!File.Exists(legacy))
            return false;

        if (File.Exists(canonical))
            return false; // Ambos existem: não sobrescreve

        try
        {
            Directory.CreateDirectory(ApiKeysDir);
            File.Move(legacy, canonical);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Varre a pasta legada 'keys' e migra atomicamente todos os blobs para 'api-keys' sem decifrar.
    /// </summary>
    public int MigrateAllLegacyApiKeys()
    {
        if (!Directory.Exists(LegacyApiKeysDir))
            return 0;

        int migrated = 0;
        try
        {
            Directory.CreateDirectory(ApiKeysDir);
            foreach (var legacyFile in Directory.EnumerateFiles(LegacyApiKeysDir, "*.bin"))
            {
                var fileName = Path.GetFileName(legacyFile);
                var canonical = Path.Combine(ApiKeysDir, fileName);
                if (!File.Exists(canonical))
                {
                    try
                    {
                        File.Move(legacyFile, canonical);
                        migrated++;
                    }
                    catch
                    {
                        // Continua para outros arquivos
                    }
                }
            }
        }
        catch
        {
            // Ignora falhas de enumeração
        }

        return migrated;
    }

    // Pasta de trabalho isolada para login/refresh (CODEX_HOME efêmero). NÃO usar %TEMP%: o codex
    // recusa criar binários auxiliares sob o diretório temporário do sistema. Ver §5/§6.
    public string TempRoot => Path.Combine(Root, "work");
    public CodexPaths Codex { get; }

    public AppPaths(string? root = null, string? codexHome = null, string? legacyRoot = null)
    {
        // Active Switchboard data root: strictly CODEXSWITCHBOARD_HOME or DefaultRoot.
        // Never falls back to CODEXSWITCHER_HOME to guarantee isolation from upstream legacy switcher.
        Root = root
            ?? Environment.GetEnvironmentVariable("CODEXSWITCHBOARD_HOME")
            ?? DefaultRoot;

        LegacyRoot = LegacyDataRootResolver.ResolveLegacyRoot(legacyRoot);

        var home = codexHome
            ?? Environment.GetEnvironmentVariable("CODEX_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        Codex = CodexPaths.ForHome(home);
    }

    /// <summary>Cria as pastas necessárias. Não toca o .codex real.</summary>
    public void EnsureDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(VaultDir);
        Directory.CreateDirectory(BackupsDir);
        Directory.CreateDirectory(TempRoot);
        Directory.CreateDirectory(TotpDir);
        Directory.CreateDirectory(CatalogDir);
        Directory.CreateDirectory(BrokerDir);
        Directory.CreateDirectory(ApiKeysDir);
    }
}

/// <summary>
/// Dedicated resolver for legacy CodexSwitcher data root.
/// Guaranteed to be isolated from the active Switchboard destination root.
/// </summary>
public static class LegacyDataRootResolver
{
    public static string ResolveLegacyRoot(string? explicitLegacyRoot = null) =>
        explicitLegacyRoot
        ?? Environment.GetEnvironmentVariable("CODEXSWITCHER_HOME")
        ?? AppPaths.LegacyDefaultRoot;
}

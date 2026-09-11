using CodexSwitcher.Core.Models;

namespace CodexSwitcher.Infra;

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
    public string ApiKeysDir => Path.Combine(Root, "api-keys");

    /// <summary>Caminho do arquivo de credencial TOTP cifrado de um perfil.</summary>
    public string GetTotpPath(Guid profileId) => Path.Combine(TotpDir, $"{profileId:N}.bin");

    /// <summary>Caminho do arquivo de chave API cifrada de um perfil de provedor.</summary>
    public string GetApiKeyPath(Guid profileId) => Path.Combine(ApiKeysDir, $"{profileId:N}.bin");

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

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
namespace CodexSwitcher.Infra.Common.Storage;

/// <summary>
/// Limpeza das pastas efêmeras de perfis descartáveis (WebView2 <c>codex-webview-*</c> do login,
/// CODEX_HOME <c>codex-login-*</c> do login, e <c>codex-browser-*</c> de cada aba do navegador
/// privado) sob o TempRoot. O WebView2 grava cache/cookies/histórico no seu perfil isolado; apagamos
/// a pasta ao fechar, mas handles ainda presos ou arquivos read-only do plugin-clone do app-server
/// (.git) podem impedir a exclusão. Este helper força a exclusão (limpa atributos read-only) e varre
/// resíduos de sessões anteriores na inicialização.
/// </summary>
public static class TempCleanup
{
    private static readonly string[] EphemeralPrefixes = ["codex-webview-", "codex-login-", "codex-browser-", "usage-"];

    /// <summary>
    /// Apaga, na inicialização, quaisquer pastas efêmeras que sobraram de execuções anteriores.
    /// Best-effort: nenhuma sessão de login/navegador está ativa no arranque, então é seguro varrer tudo.
    /// </summary>
    public static void SweepLoginTemp(string tempRoot)
    {
        if (!Directory.Exists(tempRoot)) return;

        foreach (var dir in EnumerateDirs(tempRoot))
        {
            var name = Path.GetFileName(dir);
            if (EphemeralPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                TryForceDelete(dir);
        }
    }

    /// <summary>Apaga a pasta recursivamente, limpando atributos read-only. Retorna true se sumiu.</summary>
    public static bool TryForceDelete(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return true;
            ClearReadOnlyAttributes(dir);
            Directory.Delete(dir, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void ClearReadOnlyAttributes(string dir)
    {
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            try
            {
                var attrs = File.GetAttributes(file);
                if ((attrs & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static IEnumerable<string> EnumerateDirs(string root)
    {
        try { return Directory.EnumerateDirectories(root); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }
}

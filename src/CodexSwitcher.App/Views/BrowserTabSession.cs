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
using Microsoft.Web.WebView2.Core;

namespace CodexSwitcher.App.Views;

/// <summary>
/// Sessão descartável de navegação: pasta de perfil única (<c>codex-browser-*</c>) + ambiente
/// WebView2 próprios, nunca reaproveitados de execuções anteriores. Uma sessão nasce com cada aba
/// criada pelo usuário (botão "+") e é compartilhada apenas com as abas que as páginas dela abrirem
/// (links <c>target=_blank</c>/popups), que precisam da mesma sessão para funcionar (window.opener,
/// logins em popup). A pasta é apagada quando a última aba da sessão fecha; resíduos de sessões que
/// não puderam ser apagados são varridos pelo TempCleanup no próximo arranque.
/// </summary>
internal sealed class BrowserTabSession
{
    private int _refCount;

    public string UserDataFolder { get; }
    public Task<CoreWebView2Environment> Environment { get; }

    public BrowserTabSession(string tempRoot)
    {
        UserDataFolder = Path.Combine(tempRoot, "codex-browser-" + Guid.NewGuid().ToString("N"));
        Environment = CreateEnvironmentAsync();
    }

    private async Task<CoreWebView2Environment> CreateEnvironmentAsync()
    {
        Directory.CreateDirectory(UserDataFolder);
        return await CoreWebView2Environment.CreateWithOptionsAsync(
            string.Empty, UserDataFolder, new CoreWebView2EnvironmentOptions());
    }

    public void AddRef() => Interlocked.Increment(ref _refCount);

    /// <summary>Solta uma referência; ao fechar a última aba da sessão, apaga a pasta de perfil
    /// (com retentativas, enquanto o WebView2 solta os handles).</summary>
    public async Task ReleaseAsync()
    {
        if (Interlocked.Decrement(ref _refCount) > 0) return;

        for (var attempt = 0; attempt < 8; attempt++)
        {
            if (TempCleanup.TryForceDelete(UserDataFolder)) break;
            await Task.Delay(200);
        }
    }
}

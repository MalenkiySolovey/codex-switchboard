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
using CodexSwitcher.App.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;
using Windows.System;

namespace CodexSwitcher.App.Views;

/// <summary>
/// Uma aba do <see cref="PrivateBrowserWindow"/>: navegador completo (endereço, voltar/avançar/
/// recarregar, cadeado 2FA) rodando sobre uma <see cref="BrowserTabSession"/> descartável. Abas
/// criadas pelo usuário (botão "+") têm cada uma sua própria sessão, isoladas entre si como
/// navegadores separados, sem cookies/cache/histórico compartilhado nem persistido. Links/popups que
/// uma página abre (<c>target=_blank</c>, window.open) viram novas abas AQUI dentro (nunca janelas
/// soltas do sistema), compartilhando a sessão da aba que os abriu, como numa janela anônima real.
/// Ao fechar, o WebView2 é descartado e a pasta da sessão apagada com a última aba dela (mesma
/// técnica de <see cref="LoginWindow"/>, ver [[login-clean-guest-session]]).
/// </summary>
public sealed partial class PrivateBrowserTab : UserControl
{
    private readonly Strings _loc = Strings.Current;
    private readonly BrowserTabSession _session;
    private readonly bool _isPopup;
    private readonly TaskCompletionSource<CoreWebView2> _readyTcs = new();
    private bool _ready;

    public event EventHandler<string>? TitleChanged;

    /// <summary>Clique no cadeado de 2FA; a janela mostra o popup (TotpPanel único) preso ao botão.</summary>
    public event EventHandler<Button>? TwoFactorRequested;

    /// <summary>Uma página desta aba pediu nova janela (link/popup); a janela hospeda a aba filha.</summary>
    public event EventHandler<PrivateBrowserTab>? NewTabRequested;

    public PrivateBrowserTab(string tempRoot) : this(new BrowserTabSession(tempRoot), isPopup: false) { }

    private PrivateBrowserTab(BrowserTabSession session, bool isPopup)
    {
        InitializeComponent();
        _session = session;
        _isPopup = isPopup;
        session.AddRef();

        ToolTipService.SetToolTip(BackButton, _loc.BrowserBackTooltip);
        ToolTipService.SetToolTip(ForwardButton, _loc.BrowserForwardTooltip);
        ToolTipService.SetToolTip(ReloadButton, _loc.BrowserReloadTooltip);
        ToolTipService.SetToolTip(TwoFactorButton, _loc.TwoFactorTooltip);
        AddressBox.PlaceholderText = _loc.BrowserAddressPlaceholder;

        Loaded += OnLoaded;
    }

    private void OnTwoFactorClick(object sender, RoutedEventArgs e) =>
        TwoFactorRequested?.Invoke(this, TwoFactorButton);

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            var env = await _session.Environment;
            await Web.EnsureCoreWebView2Async(env);

            Web.CoreWebView2.NavigationStarting += (_, _) => LoadingRing.IsActive = true;
            Web.CoreWebView2.NavigationCompleted += (_, _) =>
            {
                LoadingRing.IsActive = false;
                UpdateNavButtons();
            };
            Web.CoreWebView2.SourceChanged += (_, _) => AddressBox.Text = Web.CoreWebView2.Source;
            Web.CoreWebView2.HistoryChanged += (_, _) => UpdateNavButtons();
            Web.CoreWebView2.DocumentTitleChanged += (_, _) =>
                TitleChanged?.Invoke(this, Web.CoreWebView2.DocumentTitle);
            Web.CoreWebView2.NewWindowRequested += OnNewWindowRequested;

            _ready = true;
            ReloadButton.IsEnabled = true;
            AddressBox.IsEnabled = true;
            // Aba-popup não navega: o WebView2 dela recebe a navegação pedida pela aba que a abriu.
            if (!_isPopup) Web.CoreWebView2.Navigate("about:blank");
            _readyTcs.TrySetResult(Web.CoreWebView2);
        }
        catch (Exception ex)
        {
            _readyTcs.TrySetException(ex);
            AddressBox.Text = ex.Message;
        }
    }

    /// <summary>Links <c>target=_blank</c>/window.open viram uma nova aba nesta janela (na mesma
    /// sessão da aba de origem, para popups de login funcionarem), nunca uma janela solta.</summary>
    private async void OnNewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            var child = new PrivateBrowserTab(_session, isPopup: true);
            NewTabRequested?.Invoke(this, child);
            args.NewWindow = await child._readyTcs.Task;
            args.Handled = true;
        }
        catch (Exception)
        {
            // Sem aba filha utilizável; deixa o WebView2 abrir a janela padrão para não perder o link.
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void UpdateNavButtons()
    {
        if (!_ready) return;
        BackButton.IsEnabled = Web.CoreWebView2.CanGoBack;
        ForwardButton.IsEnabled = Web.CoreWebView2.CanGoForward;
    }

    private void Navigate(string input)
    {
        if (!_ready) return;
        Web.CoreWebView2.Navigate(NormalizeUrl(input));
    }

    private static string NormalizeUrl(string input)
    {
        input = input.Trim();
        if (input.Length == 0) return "about:blank";

        if (Uri.TryCreate(input, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            return input;

        if (input.Contains(' ') || !input.Contains('.'))
            return "https://www.bing.com/search?q=" + Uri.EscapeDataString(input);

        return "https://" + input;
    }

    private void OnAddressKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter) Navigate(AddressBox.Text);
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (_ready && Web.CoreWebView2.CanGoBack) Web.CoreWebView2.GoBack();
    }

    private void OnForwardClick(object sender, RoutedEventArgs e)
    {
        if (_ready && Web.CoreWebView2.CanGoForward) Web.CoreWebView2.GoForward();
    }

    private void OnReloadClick(object sender, RoutedEventArgs e)
    {
        if (_ready) Web.CoreWebView2.Reload();
    }

    /// <summary>Descarta o WebView2 e solta a sessão (a pasta é apagada com a última aba dela).</summary>
    public async Task CleanupAsync()
    {
        try { Web.Close(); } catch (Exception) { /* já fechando */ }
        await _session.ReleaseAsync();
    }
}

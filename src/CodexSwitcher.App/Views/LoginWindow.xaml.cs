using CodexSwitcher.App.Localization;
using CodexSwitcher.App.Services;
using CodexSwitcher.Core.Abstractions;
using CodexSwitcher.Infra;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace CodexSwitcher.App.Views;

/// <summary>
/// Login efêmero: WebView2 com userDataFolder descartável e único (sessão visitante, sem cookies),
/// dirigindo o fluxo OAuth do <c>codex app-server</c> num CODEX_HOME isolado. O app-server devolve a
/// URL de autorização (nunca abre o navegador do sistema) e escreve o <c>auth.json</c> ao concluir.
/// Ao fim, captura o auth.json gerado, descarta o WebView2 e apaga as pastas temporárias.
/// Ver BUSINESS_RULES.md §5 e a memória [[login-clean-guest-session]].
/// </summary>
public sealed partial class LoginWindow : Window
{
    private readonly ICodexCli _codex;
    private readonly AppPaths _paths;
    private readonly string _userDataFolder;
    private readonly string _codexHome;
    private readonly DispatcherQueue _dispatcher;
    private readonly TaskCompletionSource<byte[]?> _tcs = new();
    private readonly CancellationTokenSource _cts = new();

    private readonly Strings _loc = Strings.Current;
    private ICodexLoginSession? _session;
    private bool _completed;

    public LoginWindow(ICodexCli codex, AppPaths paths)
    {
        InitializeComponent();
        _codex = codex;
        _paths = paths;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _userDataFolder = Path.Combine(paths.TempRoot, "codex-webview-" + Guid.NewGuid().ToString("N"));
        _codexHome = Path.Combine(paths.TempRoot, "codex-login-" + Guid.NewGuid().ToString("N"));

        Title = _loc.LoginTitle;
        StatusText.Text = _loc.LoginPreparing;
        CleanNoteText.Text = _loc.LoginCleanNote;
        CancelButton.Content = _loc.Cancel;
        ToolTipService.SetToolTip(TwoFactorButton, _loc.TwoFactorTooltip);

        Closed += OnClosed;
    }

    public async Task<byte[]?> ShowAndWaitAsync()
    {
        Activate();
        await StartAsync();
        return await _tcs.Task;
    }

    private async Task StartAsync()
    {
        if (!_codex.IsAvailable)
        {
            SetStatus(_loc.LoginCodexNotFound);
            HintText.Text = _loc.LoginInstallCodex;
            Spinner.IsActive = false;
            return;
        }

        if (!WebView2Bootstrap.IsRuntimeInstalled())
        {
            ShowWebView2Missing();
            return;
        }

        try
        {
            Directory.CreateDirectory(_userDataFolder);
            Directory.CreateDirectory(_codexHome);
            // Força file-store no login isolado (ponto 1) sem tocar o config real.
            await File.WriteAllTextAsync(Path.Combine(_codexHome, "config.toml"),
                "cli_auth_credentials_store = \"file\"\n");

            var env = await CoreWebView2Environment.CreateWithOptionsAsync(
                string.Empty, _userDataFolder, new CoreWebView2EnvironmentOptions());
            await Web.EnsureCoreWebView2Async(env);
            Web.CoreWebView2.NavigationStarting += OnNavigationStarting;

            SetStatus(_loc.LoginWaitingPage);
            _ = RunLoginAsync();
        }
        catch (Exception ex)
        {
            SetStatus(_loc.LoginWebView2Failed);
            HintText.Text = _loc.LoginWebView2Hint + ex.Message;
            Spinner.IsActive = false;
        }
    }

    private void ShowWebView2Missing()
    {
        SetStatus(_loc.LoginWebView2Missing);
        HintText.Text = _loc.LoginWebView2MissingHint;
        InstallWebView2Button.Content = _loc.LoginWebView2InstallButton;
        InstallWebView2Button.Visibility = Visibility.Visible;
        Spinner.IsActive = false;
    }

    private void OnInstallWebView2Click(object sender, RoutedEventArgs e)
    {
        if (!WebView2Bootstrap.OpenOfficialDownloadPage())
        {
            SetStatus(_loc.LoginWebView2InstallFailed);
        }
    }

    private async Task RunLoginAsync()
    {
        try
        {
            _session = await _codex.StartChatGptLoginAsync(_codexHome, _cts.Token);

            // Abre a URL de autorização na sessão visitante (WebView2), nunca no navegador do sistema.
            _dispatcher.TryEnqueue(() =>
            {
                try
                {
                    Web.CoreWebView2.Navigate(_session.AuthUrl);
                    SetStatus(_loc.LoginCleanOpened);
                    HintText.Text = _loc.LoginCleanHint;
                }
                catch (Exception)
                {
                    // Navegação falhou; o app-server ainda aguarda o callback local.
                }
            });

            var result = await _session.Completion;

            if (result.Success)
            {
                var authPath = Path.Combine(_codexHome, "auth.json");
                for (var i = 0; i < 30 && !File.Exists(authPath); i++)
                    await Task.Delay(100, _cts.Token);

                if (File.Exists(authPath))
                {
                    Complete(await File.ReadAllBytesAsync(authPath));
                    return;
                }
            }

            ShowFailure(result.Error);
        }
        catch (OperationCanceledException)
        {
            Complete(null);
        }
        catch (Exception ex)
        {
            ShowFailure(ex.Message);
        }
    }

    private void OnNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (args.Uri.Contains("localhost:1455", StringComparison.OrdinalIgnoreCase)
            || args.Uri.Contains("/auth/callback", StringComparison.OrdinalIgnoreCase))
        {
            _dispatcher.TryEnqueue(() => SetStatus(_loc.LoginCompleting));
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Complete(null);

    /// <summary>Mostra o erro na própria janela e deixa o usuário fechar (botão vira "Fechar").</summary>
    private void ShowFailure(string? detail)
    {
        _dispatcher.TryEnqueue(() =>
        {
            if (_completed) return;
            SetStatus(_loc.LoginFailed);
            HintText.Text = string.IsNullOrWhiteSpace(detail) ? _loc.LoginFailedHint : detail;
            Spinner.IsActive = false;
            CancelButton.Content = _loc.Close;
        });
    }

    private void Complete(byte[]? result)
    {
        if (_completed) return;
        _completed = true;
        _dispatcher.TryEnqueue(() =>
        {
            _tcs.TrySetResult(result);
            Close();
        });
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _completed = true; // impede atualizações de UI após o fechamento (ex.: ShowFailure tardio).
        _cts.Cancel();
        _tcs.TrySetResult(null);
        // Encerra a sessão do app-server (cancela o login e mata o processo).
        if (_session is not null)
            _ = _session.DisposeAsync();
        // Descartar handles do WebView2 antes de apagar (ponto 8).
        try { Web.Close(); } catch (Exception) { /* já fechando */ }
        // O popup de 2FA é só ocultado ao perder foco (não recriado), então o timer sobreviveria à
        // janela se não for parado aqui explicitamente.
        TotpPanel.StopTimer();
        _ = CleanupTempAsync();
    }

    private async Task CleanupTempAsync()
    {
        // Retenta enquanto o WebView2/app-server soltam os handles; força read-only (arquivos .git do
        // plugin-clone do app-server). Resíduos remanescentes são varridos no próximo arranque.
        foreach (var dir in new[] { _userDataFolder, _codexHome })
        {
            for (var attempt = 0; attempt < 8; attempt++)
            {
                if (Infra.Io.TempCleanup.TryForceDelete(dir)) break;
                await Task.Delay(200);
            }
        }
    }

    private void SetStatus(string text)
    {
        StatusText.Text = text;
        Spinner.IsActive = !_completed;
    }
}

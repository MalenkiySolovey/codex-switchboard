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
using Windows.ApplicationModel.DataTransfer;

namespace CodexSwitcher.App.Views;

/// <summary>
/// Conteúdo do gerador de código 2FA (TOTP, RFC 6238), reaproveitado tanto pela <see cref="TotpWindow"/>
/// (janela cheia, aberta a partir da tela principal) quanto por um <c>Flyout</c> preso ao botão "2FA" da
/// <see cref="LoginWindow"/> (popup preso ao botão, como o de uma extensão de navegador). Por ser uma
/// instância única e persistente no host, fechar/ocultar o popup não reinicia o segredo colado nem o
/// código em contagem, diferente de recriar a janela do zero.
/// </summary>
public sealed partial class TotpPanel : UserControl
{
    private readonly Strings _loc = Strings.Current;
    private readonly DispatcherTimer _timer;
    private TotpSecret? _secret;
    private string _currentCode = string.Empty;
    private DateTimeOffset _copiedFlashUntil = DateTimeOffset.MinValue;

    public TotpPanel()
    {
        InitializeComponent();

        HeaderText.Text = _loc.TotpTitle;
        SubtitleText.Text = _loc.TotpSubtitle;
        SecretLabel.Text = _loc.TotpSecretLabel;
        SecretBox.PlaceholderText = _loc.TotpSecretPlaceholder;
        EmptyHintText.Text = _loc.TotpEmptyHint;
        CopyButtonText.Text = _loc.TotpCopy;
        ToolTipService.SetToolTip(PasteButton, _loc.TotpPaste);
        ToolTipService.SetToolTip(CodeButton, _loc.TotpClickToCopy);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += OnTick;
    }

    /// <summary>Para o timer quando o host (janela ou popup) é definitivamente descartado.</summary>
    internal void StopTimer() => _timer.Stop();

    private void OnSecretChanged(object sender, RoutedEventArgs e)
    {
        var text = SecretBox.Password;

        if (string.IsNullOrWhiteSpace(text))
        {
            _secret = null;
            _timer.Stop();
            ErrorText.Visibility = Visibility.Collapsed;
            ShowCode(false);
            return;
        }

        if (Totp.TryParse(text, out var parsed, out _))
        {
            _secret = parsed;
            ErrorText.Visibility = Visibility.Collapsed;
            AccountText.Text = DescribeAccount(parsed!);
            AccountText.Visibility = string.IsNullOrEmpty(AccountText.Text) ? Visibility.Collapsed : Visibility.Visible;
            ShowCode(true);
            Render();
            _timer.Start();
        }
        else
        {
            _secret = null;
            _timer.Stop();
            ShowCode(false);
            ErrorText.Text = _loc.TotpInvalid;
            ErrorText.Visibility = Visibility.Visible;
        }
    }

    private void OnTick(object? sender, object e) => Render();

    private void Render()
    {
        if (_secret is null) return;

        var code = _secret.Compute(DateTimeOffset.UtcNow);
        _currentCode = code.Code;
        CodeText.Text = code.Formatted;
        SecondsText.Text = code.SecondsRemaining.ToString();
        ExpiryText.Text = _loc.TotpExpiresIn(code.SecondsRemaining);
        Countdown.Value = code.Period <= 0 ? 0 : code.SecondsRemaining * 100.0 / code.Period;

        if (DateTimeOffset.UtcNow >= _copiedFlashUntil && CopiedPanel.Visibility == Visibility.Visible)
            CopiedPanel.Visibility = Visibility.Collapsed;
    }

    private static string DescribeAccount(TotpSecret secret)
    {
        if (!string.IsNullOrWhiteSpace(secret.Issuer)) return secret.Issuer!;
        if (!string.IsNullOrWhiteSpace(secret.Label)) return secret.Label!;
        return string.Empty;
    }

    private void ShowCode(bool visible)
    {
        CodeCard.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        CopyButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        EmptyHint.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
        if (!visible) CopiedPanel.Visibility = Visibility.Collapsed;
    }

    private void OnCopyClick(object sender, RoutedEventArgs e) => CopyCurrentCode();

    private void OnCodeClick(object sender, RoutedEventArgs e) => CopyCurrentCode();

    /// <summary>Copia o código atual (sem espaços) e mostra o feedback "Copiado!".</summary>
    private void CopyCurrentCode()
    {
        if (string.IsNullOrEmpty(_currentCode)) return;
        try
        {
            var data = new DataPackage();
            data.SetText(_currentCode);
            Clipboard.SetContent(data);

            CopiedText.Text = _loc.TotpCopied;
            CopiedPanel.Visibility = Visibility.Visible;
            _copiedFlashUntil = DateTimeOffset.UtcNow.AddSeconds(2);
        }
        catch (Exception)
        {
            // Clipboard ocupado por outro app; ignorar silenciosamente.
        }
    }

    private async void OnPasteClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var content = Clipboard.GetContent();
            if (content.Contains(StandardDataFormats.Text))
            {
                var text = await content.GetTextAsync();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    SecretBox.Password = text.Trim();
                    SecretBox.SelectAll();
                }
            }
        }
        catch (Exception)
        {
            // Sem texto no clipboard ou acesso negado; ignorar.
        }
    }
}

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
using System;
using System.Linq;
using System.Threading.Tasks;
using CodexSwitcher.App.Dialogs;
using CodexSwitcher.App.Dialogs.Common;
using CodexSwitcher.App.Dialogs.Shared;
using CodexSwitcher.App.Localization;
using CodexSwitcher.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CodexSwitcher.App.Dialogs.Totp;

/// <summary>
/// Implementation of dialogs specific to TOTP 2FA secret management, degraded Windows auth fallbacks, and settings navigation.
/// </summary>
public sealed class TotpDialogService : CommonDialogService, ITotpDialogService
{
    public TotpDialogService(IDialogHost host) : base(host)
    {
    }

    public async Task<TotpSetupResult?> PromptTotpSetupAsync(string accountName, bool isCurrentlyConfigured)
    {
        var panel = new StackPanel { Spacing = 14, MaxWidth = 460 };

        // 1. Account header
        var headerPanel = new StackPanel { Spacing = 2 };
        headerPanel.Children.Add(new TextBlock
        {
            Text = accountName,
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        panel.Children.Add(headerPanel);

        // 2. Security warning banner
        var warnBorder = new Border
        {
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["LayerFillColorAltBrush"],
            BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 8, 12, 8),
        };
        var warnStack = new StackPanel { Spacing = 6 };
        var warnHeader = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        warnHeader.Children.Add(new FontIcon { Glyph = "\uE72E", FontSize = 14 });
        warnHeader.Children.Add(new TextBlock { Text = _loc.TotpKeySavedLocally, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 12 });
        warnStack.Children.Add(warnHeader);
        warnStack.Children.Add(new TextBlock
        {
            Text = _loc.TotpSecurityWarning,
            FontSize = 12,
            Opacity = 0.8,
            TextWrapping = TextWrapping.Wrap
        });
        warnBorder.Child = warnStack;
        panel.Children.Add(warnBorder);

        // 3. Status or Input area
        var inputSection = new StackPanel { Spacing = 8 };
        var promptText = new TextBlock
        {
            Text = _loc.TotpEnterKeyPrompt,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.Medium,
            TextWrapping = TextWrapping.Wrap
        };
        inputSection.Children.Add(promptText);

        var keyGrid = new Grid { ColumnSpacing = 8 };
        keyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        keyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var passwordBox = new PasswordBox
        {
            PlaceholderText = _loc.TotpSecretPlaceholder,
            IsPasswordRevealButtonEnabled = true,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
        };
        Grid.SetColumn(passwordBox, 0);
        keyGrid.Children.Add(passwordBox);

        var pasteBtn = new Button
        {
            Content = new FontIcon { Glyph = "\uE77F", FontSize = 14 },
        };
        ToolTipService.SetToolTip(pasteBtn, _loc.TotpPaste);
        pasteBtn.Click += async (_, _) =>
        {
            try
            {
                var content = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
                if (content.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
                {
                    var text = await content.GetTextAsync();
                    if (!string.IsNullOrWhiteSpace(text))
                        passwordBox.Password = text.Trim();
                }
            }
            catch { }
        };
        Grid.SetColumn(pasteBtn, 1);
        keyGrid.Children.Add(pasteBtn);
        inputSection.Children.Add(keyGrid);

        var errorText = new TextBlock
        {
            Text = _loc.TotpInvalid,
            FontSize = 12,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["HealthDangerBrush"],
            Visibility = Visibility.Collapsed,
            TextWrapping = TextWrapping.Wrap
        };
        inputSection.Children.Add(errorText);

        var syncNotice = new TextBlock
        {
            Text = _loc.TotpTimeSyncNotice,
            FontSize = 11,
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap
        };
        inputSection.Children.Add(syncNotice);

        bool isReplacing = !isCurrentlyConfigured;
        if (!isReplacing)
        {
            inputSection.Visibility = Visibility.Collapsed;
        }

        var manageButtonsPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var replaceButton = new Button
        {
            Content = _loc.ReplaceTotpKey,
        };
        var removeButton = new Button
        {
            Content = _loc.RemoveTotpKey,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["HealthDangerBrush"],
        };

        if (isCurrentlyConfigured)
        {
            replaceButton.Click += (_, _) =>
            {
                isReplacing = true;
                inputSection.Visibility = Visibility.Visible;
                manageButtonsPanel.Visibility = Visibility.Collapsed;
            };
            manageButtonsPanel.Children.Add(replaceButton);
            manageButtonsPanel.Children.Add(removeButton);
            panel.Children.Add(manageButtonsPanel);
        }

        panel.Children.Add(inputSection);

        ContentDialog dialog = null!;
        bool requestedRemove = false;

        removeButton.Click += async (_, _) =>
        {
            dialog.Hide();
            var confirmed = await ConfirmAsync(
                _loc.TotpRemoveConfirmationTitle,
                _loc.TotpRemoveConfirmationMessage,
                _loc.TotpConfirmRemoveButton,
                destructive: true);
            if (confirmed)
            {
                requestedRemove = true;
            }
        };

        dialog = new ContentDialog
        {
            Title = isCurrentlyConfigured ? _loc.TotpManageTitle : _loc.TotpSetupTitle,
            Content = panel,
            PrimaryButtonText = _loc.TotpSaveKeyButton,
            CloseButtonText = _loc.Cancel,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        dialog.PrimaryButtonClick += (s, args) =>
        {
            if (!isReplacing)
            {
                return;
            }

            var secretText = passwordBox.Password?.Trim();
            string? err = null;
            if (string.IsNullOrWhiteSpace(secretText) || !CodexSwitcher.Core.Security.Totp.Totp.TryParse(secretText, out _, out err))
            {
                args.Cancel = true;
                errorText.Text = err ?? _loc.TotpInvalid;
                errorText.Visibility = Visibility.Visible;
            }
            else
            {
                errorText.Visibility = Visibility.Collapsed;
            }
        };

        var result = await dialog.ShowAsync();

        if (requestedRemove)
        {
            return new TotpSetupResult(TotpSetupAction.RemoveKey);
        }

        if (result == ContentDialogResult.Primary && isReplacing)
        {
            var key = passwordBox.Password?.Trim();
            passwordBox.Password = string.Empty;
            return new TotpSetupResult(TotpSetupAction.SaveNewKey, key);
        }

        passwordBox.Password = string.Empty;
        return null;
    }


    public async Task<TransientVerificationChoice> PromptTransientVerificationFallbackAsync(string message)
    {
        var dialog = new ContentDialog
        {
            Title = _loc.WindowsVerificationTransientTitle,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = _loc.WindowsVerificationTryAgainButton,
            SecondaryButtonText = _loc.WindowsVerificationShowCodeOnceButton,
            CloseButtonText = _loc.Cancel,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        var result = await dialog.ShowAsync();
        return result switch
        {
            ContentDialogResult.Primary => TransientVerificationChoice.TryAgain,
            ContentDialogResult.Secondary => TransientVerificationChoice.ShowCodeOnce,
            _ => TransientVerificationChoice.Cancel
        };
    }


    public async Task OpenWindowsSignInOptionsAsync()
    {
        try
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:signinoptions"));
        }
        catch
        {
            // Fallback silencioso se o launcher do sistema não estiver disponível
        }
    }

}

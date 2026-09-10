using CodexSwitcher.App.Localization;
using CodexSwitcher.Core.Abstractions;
using CodexSwitcher.Core.Models;
using CodexSwitcher.Infra;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace CodexSwitcher.App.Services;

/// <summary>Implementa as interações de UI com ContentDialogs Fluent e a janela de login efêmero.</summary>
public sealed class UiInteractionService : IUiInteraction
{
    private readonly ICodexCli _codex;
    private readonly AppPaths _paths;
    private readonly Strings _loc = Strings.Current;
    private Window? _window;

    public UiInteractionService(ICodexCli codex, AppPaths paths)
    {
        _codex = codex;
        _paths = paths;
    }

    /// <summary>Liga o serviço à janela principal (fonte do XamlRoot para os diálogos).</summary>
    public void Attach(Window window) => _window = window;

    private XamlRoot XamlRoot =>
        _window?.Content.XamlRoot ?? throw new InvalidOperationException("UI ainda não anexada.");

    public async Task<bool> ConfirmSwitchAsync(SwitchPlan plan)
    {
        var panel = new StackPanel { Spacing = 10 };

        panel.Children.Add(new TextBlock
        {
            Text = _loc.CurrentAccount(plan.FromProfile?.DisplayName ?? _loc.NoManaged),
            Opacity = 0.8,
            TextWrapping = TextWrapping.Wrap,
        });

        var desktop = plan.DesktopToClose;
        var cli = plan.CliToClose;

        if (desktop.Count > 0)
        {
            var names = string.Join(", ", desktop.Select(p => p.ProcessName).Distinct(StringComparer.OrdinalIgnoreCase));
            panel.Children.Add(SectionText(_loc.WillCloseReopen, _loc.AppLabel(names)));
        }

        if (cli.Count > 0)
        {
            var names = string.Join(", ", cli.Select(p => p.ProcessName).Distinct(StringComparer.OrdinalIgnoreCase));
            panel.Children.Add(SectionText(_loc.WillCloseCli, names));
        }

        if (desktop.Count == 0 && cli.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = _loc.NoAppsRunning,
                Opacity = 0.8,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        if (plan.HasActiveCliWork)
        {
            panel.Children.Add(new InfoBar
            {
                IsOpen = true,
                IsClosable = false,
                Severity = InfoBarSeverity.Warning,
                Title = _loc.CliActiveTitle,
                Message = _loc.CliActiveMsg,
            });
        }

        panel.Children.Add(new TextBlock
        {
            Text = _loc.IdeNote,
            Opacity = 0.7,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        });

        var dialog = new ContentDialog
        {
            Title = _loc.ConfirmSwitchTitle(plan.ToProfile.DisplayName),
            Content = panel,
            PrimaryButtonText = _loc.Confirm,
            CloseButtonText = _loc.Cancel,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    public async Task<bool> ConfirmAsync(string title, string message, string okText, bool destructive = false)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = okText,
            CloseButtonText = _loc.Cancel,
            DefaultButton = destructive ? ContentDialogButton.Close : ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    public async Task<string?> PromptTextAsync(string title, string prompt, string initialValue, string okText)
    {
        var box = new TextBox { Text = initialValue, SelectionStart = initialValue?.Length ?? 0 };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(box);

        var dialog = new ContentDialog
        {
            Title = title,
            Content = panel,
            PrimaryButtonText = okText,
            CloseButtonText = _loc.Cancel,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? box.Text?.Trim() : null;
    }

    public async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "OK",
            XamlRoot = XamlRoot,
        };
        await dialog.ShowAsync();
    }

    public async Task<byte[]?> RunEphemeralLoginAsync()
    {
        var login = new Views.LoginWindow(_codex, _paths);
        return await login.ShowAndWaitAsync();
    }

    public async Task<string?> PickImportFileAsync()
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".codexswitchboard");
        picker.FileTypeFilter.Add(".codexswitcher");
        picker.FileTypeFilter.Add(".json");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(_window));
        var file = await picker.PickSingleFileAsync();
        return file is null ? null : await FileIO.ReadTextAsync(file);
    }

    public async Task<bool> SaveExportFileAsync(string suggestedFileName, string contents)
    {
        var picker = new FileSavePicker
        {
            SuggestedFileName = suggestedFileName,
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
        };
        picker.FileTypeChoices.Add("Codex Switchboard accounts (*.codexswitchboard)", [".codexswitchboard"]);
        picker.FileTypeChoices.Add("Codex Switcher accounts (*.codexswitcher)", [".codexswitcher"]);
        picker.FileTypeChoices.Add("JSON files (*.json)", [".json"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(_window));
        StorageFile? file = await picker.PickSaveFileAsync();
        if (file is null) return false;
        await FileIO.WriteTextAsync(file, contents);
        return true;
    }

    public async Task<SubscriptionTracking?> PromptSubscriptionTrackingAsync(string accountName, SubscriptionTracking? current)
    {
        var panel = new StackPanel { Spacing = 14, MinWidth = 360, MaxWidth = 420 };

        // Disclaimer
        var disclaimer = new InfoBar
        {
            IsOpen = true,
            IsClosable = false,
            Severity = InfoBarSeverity.Informational,
            Message = _loc.SubscriptionTrackingDisclaimer,
        };
        panel.Children.Add(disclaimer);

        // Mode RadioButtons
        var modePanel = new StackPanel { Spacing = 6 };
        modePanel.Children.Add(new TextBlock { Text = _loc.SubscriptionModeLabel, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        var radioRenewal = new RadioButton { Content = _loc.SubscriptionModeRenewal, IsChecked = current?.Mode != SubscriptionTrackingMode.Expiration };
        var radioExpiration = new RadioButton { Content = _loc.SubscriptionModeExpiration, IsChecked = current?.Mode == SubscriptionTrackingMode.Expiration };
        var radioStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        radioStack.Children.Add(radioRenewal);
        radioStack.Children.Add(radioExpiration);
        modePanel.Children.Add(radioStack);
        panel.Children.Add(modePanel);

        // Target DatePicker
        var targetPanel = new StackPanel { Spacing = 6 };
        targetPanel.Children.Add(new TextBlock { Text = _loc.SubscriptionTargetLabel, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        var targetPicker = new DatePicker();
        if (current?.NextRenewalOrExpiryOn is { } dt)
        {
            targetPicker.Date = new DateTimeOffset(dt.ToDateTime(TimeOnly.MinValue));
        }
        else
        {
            targetPicker.Date = DateTimeOffset.Now;
        }
        targetPanel.Children.Add(targetPicker);
        panel.Children.Add(targetPanel);

        // Started DatePicker (Optional)
        var startedPanel = new StackPanel { Spacing = 6 };
        var startedCheck = new CheckBox { Content = _loc.SubscriptionStartedLabel, IsChecked = current?.StartedOn.HasValue == true };
        var startedPicker = new DatePicker
        {
            IsEnabled = startedCheck.IsChecked == true,
        };
        if (current?.StartedOn is { } st)
        {
            startedPicker.Date = new DateTimeOffset(st.ToDateTime(TimeOnly.MinValue));
        }
        else
        {
            startedPicker.Date = DateTimeOffset.Now;
        }
        startedCheck.Checked += (_, _) => startedPicker.IsEnabled = true;
        startedCheck.Unchecked += (_, _) => startedPicker.IsEnabled = false;
        startedPanel.Children.Add(startedCheck);
        startedPanel.Children.Add(startedPicker);
        panel.Children.Add(startedPanel);

        // Monthly Estimation Button
        var isEstimated = current?.IsEstimated == true;
        var estimateButton = new Button
        {
            Content = _loc.SubscriptionEstimateButton,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        estimateButton.Click += (_, _) =>
        {
            var start = DateOnly.FromDateTime(startedPicker.Date.DateTime);
            var today = DateOnly.FromDateTime(DateTime.Today);
            var estimated = SubscriptionTracking.EstimateNextMonthlyRenewal(start, today);
            if (estimated.HasValue)
            {
                targetPicker.Date = new DateTimeOffset(estimated.Value.ToDateTime(TimeOnly.MinValue));
                radioRenewal.IsChecked = true;
                isEstimated = true;
            }
        };
        panel.Children.Add(estimateButton);

        // Manage Link Button
        var manageLink = new HyperlinkButton
        {
            Content = _loc.SubscriptionManageLink,
            NavigateUri = new Uri(CodexSwitcher.Core.Support.SubscriptionFormatter.OfficialBillingUrl),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        panel.Children.Add(manageLink);

        var dialog = new ContentDialog
        {
            Title = $"{_loc.SubscriptionTrackingTitle}: {accountName}",
            Content = panel,
            PrimaryButtonText = _loc.Save,
            SecondaryButtonText = _loc.SubscriptionClearButton,
            CloseButtonText = _loc.Cancel,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Secondary)
        {
            // Clear tracking
            return null;
        }
        if (result == ContentDialogResult.Primary)
        {
            var targetDate = DateOnly.FromDateTime(targetPicker.Date.DateTime);
            DateOnly? startedDate = startedCheck.IsChecked == true
                ? DateOnly.FromDateTime(startedPicker.Date.DateTime)
                : null;
            var mode = radioExpiration.IsChecked == true
                ? SubscriptionTrackingMode.Expiration
                : SubscriptionTrackingMode.Renewal;

            return new SubscriptionTracking(
                StartedOn: startedDate,
                NextRenewalOrExpiryOn: targetDate,
                Mode: mode,
                Source: SubscriptionDateSource.UserProvided,
                IsEstimated: isEstimated);
        }

        // Canceled: return current unchanged
        return current;
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
            if (string.IsNullOrWhiteSpace(secretText) || !CodexSwitcher.Core.Security.Totp.TryParse(secretText, out _, out err))
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

    private static StackPanel SectionText(string heading, string body)
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(new TextBlock { Text = heading, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = body, Opacity = 0.85, TextWrapping = TextWrapping.Wrap });
        return panel;
    }
}

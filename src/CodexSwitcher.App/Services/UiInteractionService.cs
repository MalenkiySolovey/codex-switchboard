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
        picker.FileTypeChoices.Add("Codex Switcher accounts", [".json"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(_window));
        StorageFile? file = await picker.PickSaveFileAsync();
        if (file is null) return false;
        await FileIO.WriteTextAsync(file, contents);
        return true;
    }

    private static StackPanel SectionText(string heading, string body)
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(new TextBlock { Text = heading, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = body, Opacity = 0.85, TextWrapping = TextWrapping.Wrap });
        return panel;
    }
}

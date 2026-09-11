using System;
using System.Threading.Tasks;
using CodexSwitcher.App.Dialogs.Shared;
using CodexSwitcher.App.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CodexSwitcher.App.Dialogs.Common;

/// <summary>
/// Common dialog implementation providing confirmation, simple text prompts, and informational alerts.
/// </summary>
public class CommonDialogService : ICommonDialogService
{
    protected readonly IDialogHost Host;
    protected readonly Strings Loc = Strings.Current;
    protected Strings _loc => Loc;

    public CommonDialogService(IDialogHost host)
    {
        Host = host ?? throw new ArgumentNullException(nameof(host));
    }

    protected XamlRoot XamlRoot => Host.XamlRoot;

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

}

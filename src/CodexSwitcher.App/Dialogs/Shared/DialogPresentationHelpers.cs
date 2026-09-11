using System;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CodexSwitcher.App.Dialogs.Shared;

public static class DialogPresentationHelpers
{
    public static StackPanel SectionText(string label, string value)
    {
        var sp = new StackPanel { Spacing = 2 };
        sp.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, FontSize = 12, Opacity = 0.8 });
        sp.Children.Add(new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap });
        return sp;
    }

    public static async Task OpenWindowsSignInOptionsAsync()
    {
        try
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:signinoptions"));
        }
        catch
        {
            // Silent fallback if system launcher unavailable
        }
    }
}

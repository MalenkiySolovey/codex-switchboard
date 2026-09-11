using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace CodexSwitcher.App.Shell.Windowing;

/// <summary>
/// Presentation platform service managing window chrome, title bar integration, and icon configuration.
/// Encapsulates WinUI AppWindow title-bar mechanics so ViewModels remain decoupled from window handles.
/// </summary>
public sealed class WindowChromeService
{
    public void ConfigureTitleBar(Window window, UIElement? titleBarElement)
    {
        ArgumentNullException.ThrowIfNull(window);

        window.ExtendsContentIntoTitleBar = true;
        if (titleBarElement is not null)
        {
            window.SetTitleBar(titleBarElement);
        }
    }

    public void TrySetWindowIcon(Window window, AppWindow appWindow)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(appWindow);

        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico");
            if (File.Exists(iconPath))
            {
                appWindow.SetIcon(iconPath);
            }
        }
        catch
        {
            // Cosmetic window adjustment; non-critical
        }
    }
}

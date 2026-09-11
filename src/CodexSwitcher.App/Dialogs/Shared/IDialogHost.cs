using System;
using Microsoft.UI.Xaml;

namespace CodexSwitcher.App.Dialogs.Shared;

/// <summary>
/// Narrow abstraction for UI window hosting, providing XamlRoot and HWND for ContentDialogs and WinRT file pickers.
/// Prevents feature dialog services from statically accessing MainWindow or ServiceLocator.
/// </summary>
public interface IDialogHost
{
    void Attach(Window window);
    XamlRoot XamlRoot { get; }
    IntPtr WindowHandle { get; }
    void InitializePicker(object picker);
}

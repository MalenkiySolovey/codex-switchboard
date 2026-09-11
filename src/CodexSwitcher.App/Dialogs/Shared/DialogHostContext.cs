using System;
using Microsoft.UI.Xaml;

namespace CodexSwitcher.App.Dialogs.Shared;

/// <summary>
/// Concrete platform context hosting ContentDialogs and WinRT file pickers against the application window.
/// </summary>
public sealed class DialogHostContext : IDialogHost
{
    private Window? _window;

    public void Attach(Window window) => _window = window;

    public XamlRoot XamlRoot =>
        _window?.Content?.XamlRoot ?? throw new InvalidOperationException("UI host not attached.");

    public IntPtr WindowHandle =>
        _window != null ? WinRT.Interop.WindowNative.GetWindowHandle(_window) : IntPtr.Zero;

    public void InitializePicker(object picker)
    {
        var hwnd = WindowHandle;
        if (hwnd != IntPtr.Zero)
        {
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        }
    }
}

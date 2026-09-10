namespace CodexSwitcher.App.Services;

/// <summary>
/// Fornece o identificador de janela nativo (HWND) da janela principal para interop do Windows.
/// </summary>
public interface IWindowHandleProvider
{
    IntPtr MainWindowHandle { get; }
}

public sealed class WindowHandleProvider : IWindowHandleProvider
{
    public IntPtr MainWindowHandle { get; set; } = IntPtr.Zero;
}

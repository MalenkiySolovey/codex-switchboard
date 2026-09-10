using System.Runtime.InteropServices;
using CodexSwitcher.App.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace CodexSwitcher.App;

public sealed partial class MainWindow : Window
{
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SW_RESTORE = 9;

    private readonly AppWindow _appWindow;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(Root.TitleBarElement);
        TrySetIcon();

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        _appWindow = AppWindow.GetFromWindowId(id);
        _appWindow.Changed += OnAppWindowChanged;
        VisibilityChanged += OnVisibilityChanged;
        Activated += OnWindowActivated;

        if (AppHost.Services.GetService(typeof(IUiInteraction)) is UiInteractionService ui)
            ui.Attach(this);

        Closed += OnWindowClosed;
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            Root.ViewModel.HideAllRevealedTotp();
        }
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        try
        {
            Root.ViewModel.HideAllRevealedTotp();
            Root.ViewModel.Cleanup();
        }
        catch { }

        AppHost.Shutdown();
        Microsoft.UI.Xaml.Application.Current?.Exit();
        Environment.Exit(0);
    }

    public void BringToFront()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        ShowWindow(hwnd, SW_RESTORE);
        SetForegroundWindow(hwnd);
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidPresenterChange || args.DidSizeChange || args.DidVisibilityChange)
        {
            UpdateForegroundState();
        }
    }

    private void OnVisibilityChanged(object sender, WindowVisibilityChangedEventArgs args)
    {
        UpdateForegroundState();
    }

    private void UpdateForegroundState()
    {
        bool isVisible = Visible;
        bool isMinimized = false;
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            isMinimized = presenter.State == OverlappedPresenterState.Minimized;
        }

        bool active = isVisible && !isMinimized;
        if (!active)
        {
            Root.ViewModel.HideAllRevealedTotp();
        }
        Root.ViewModel.SetForegroundActive(active);
    }

    private void TrySetIcon()
    {
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico");
            if (!File.Exists(iconPath))
                return;
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            AppWindow.GetFromWindowId(id).SetIcon(iconPath);
        }
        catch (Exception)
        {
            // Ícone é cosmético; falha não deve impedir a janela de abrir.
        }
    }
}

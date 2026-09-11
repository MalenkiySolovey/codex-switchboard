using System.Runtime.InteropServices;
using CodexSwitcher.App.Services;
using CodexSwitcher.App.Shell.Theme;
using CodexSwitcher.App.Shell.Windowing;
using CodexSwitcher.Core.Abstractions;
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
    private readonly WindowChromeService _chromeService;
    private readonly WindowLifecycleCoordinator _lifecycleCoordinator;

    public MainWindow()
    {
        InitializeComponent();

        _chromeService = (AppHost.Services.GetService(typeof(WindowChromeService)) as WindowChromeService) ?? new WindowChromeService();
        _lifecycleCoordinator = (AppHost.Services.GetService(typeof(WindowLifecycleCoordinator)) as WindowLifecycleCoordinator)
            ?? new WindowLifecycleCoordinator(AppHost.Services.GetService(typeof(ITotpRevealAuthorizationService)) as ITotpRevealAuthorizationService);

        _chromeService.ConfigureTitleBar(this, Root.TitleBarElement);

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        _appWindow = AppWindow.GetFromWindowId(id);
        _chromeService.TrySetWindowIcon(this, _appWindow);

        _appWindow.Changed += OnAppWindowChanged;
        VisibilityChanged += OnVisibilityChanged;
        Activated += OnWindowActivated;

        if (AppHost.Services.GetService(typeof(IThemeService)) is IThemeService themeService && Content is FrameworkElement rootVisual)
        {
            themeService.Initialize(rootVisual);
        }

        if (AppHost.Services.GetService(typeof(WindowHandleProvider)) is WindowHandleProvider handleProvider)
            handleProvider.MainWindowHandle = hwnd;

        if (AppHost.Services.GetService(typeof(CodexSwitcher.App.Dialogs.Shared.IDialogHost)) is CodexSwitcher.App.Dialogs.Shared.IDialogHost dialogHost)
            dialogHost.Attach(this);

        Closed += OnWindowClosed;
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        _lifecycleCoordinator.HandleActivation(args.WindowActivationState, () => Root.ViewModel.HideAllRevealedTotp());
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _lifecycleCoordinator.HandleWindowClosed(
            () => Root.ViewModel.HideAllRevealedTotp(),
            () => Root.ViewModel.Cleanup());
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

        _lifecycleCoordinator.HandleForegroundChange(
            isVisible,
            isMinimized,
            active => Root.ViewModel.SetForegroundActive(active),
            () => Root.ViewModel.HideAllRevealedTotp());
    }
}

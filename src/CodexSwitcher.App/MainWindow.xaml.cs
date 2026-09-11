using CodexSwitcher.Core.Accounts.Contracts;
using CodexSwitcher.Core.Accounts.Formatting;
using CodexSwitcher.Core.Accounts.Models;
using CodexSwitcher.Core.Accounts.Services;
using CodexSwitcher.Core.Common.Dispatcher;
using CodexSwitcher.Core.Common.Enums;
using CodexSwitcher.Core.Common.Environment;
using CodexSwitcher.Core.Common.Errors;
using CodexSwitcher.Core.Common.Lifecycle;
using CodexSwitcher.Core.Common.Logging;
using CodexSwitcher.Core.Common.Storage;
using CodexSwitcher.Core.Common.Time;
using CodexSwitcher.Core.Providers.Catalog;
using CodexSwitcher.Core.Providers.Contracts;
using CodexSwitcher.Core.Providers.Models;
using CodexSwitcher.Core.Providers.Services;
using CodexSwitcher.Core.Routing.Contracts;
using CodexSwitcher.Core.Routing.Models;
using CodexSwitcher.Core.Routing.Services;
using CodexSwitcher.Core.Security.Secrets;
using CodexSwitcher.Core.Security.Totp;
using CodexSwitcher.Core.Security.Verification;
using CodexSwitcher.Core.Settings.Contracts;
using CodexSwitcher.Core.Settings.Models;
using CodexSwitcher.Core.Threads.Contracts;
using CodexSwitcher.Core.Threads.Models;
using CodexSwitcher.Core.Transfer.Contracts;
using CodexSwitcher.Core.Transfer.Models;
using CodexSwitcher.Core.Transfer.Services;
using CodexSwitcher.Core.Usage.Contracts;
using CodexSwitcher.Core.Usage.Formatting;
using CodexSwitcher.Core.Usage.Models;
using CodexSwitcher.Core.Usage.Services;
using CodexSwitcher.Infra.Accounts.Storage;
using CodexSwitcher.Infra.Codex.Routing;
using CodexSwitcher.Infra.Codex.Runtime;
using CodexSwitcher.Infra.Codex.Threads;
using CodexSwitcher.Infra.Codex.Usage;
using CodexSwitcher.Infra.Common.Logging;
using CodexSwitcher.Infra.Common.Paths;
using CodexSwitcher.Infra.Common.Storage;
using CodexSwitcher.Infra.Common.Time;
using CodexSwitcher.Infra.Providers.Inspection;
using CodexSwitcher.Infra.Providers.Secrets;
using CodexSwitcher.Infra.Providers.Storage;
using CodexSwitcher.Infra.Scheduling;
using CodexSwitcher.Infra.Security.Dpapi;
using CodexSwitcher.Infra.Security.Hardening;
using CodexSwitcher.Infra.Security.Totp;
using CodexSwitcher.Infra.Settings;
using System.Runtime.InteropServices;
using CodexSwitcher.App.Services;
using CodexSwitcher.App.Shell.Theme;
using CodexSwitcher.App.Shell.Windowing;
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

    public MainWindow(
        WindowChromeService chromeService,
        WindowLifecycleCoordinator lifecycleCoordinator,
        IThemeService themeService,
        WindowHandleProvider handleProvider,
        CodexSwitcher.App.Dialogs.Shared.IDialogHost dialogHost,
        CodexSwitcher.App.Shell.ShellViewModel shellViewModel,
        CodexSwitcher.Infra.Common.Paths.AppPaths paths,
        Func<CodexSwitcher.App.Features.Settings.SettingsViewModel> settingsVmFactory)
    {
        InitializeComponent();

        _chromeService = chromeService ?? throw new ArgumentNullException(nameof(chromeService));
        _lifecycleCoordinator = lifecycleCoordinator ?? throw new ArgumentNullException(nameof(lifecycleCoordinator));

        Root.Initialize(shellViewModel, paths, settingsVmFactory);

        _chromeService.ConfigureTitleBar(this, Root.TitleBarElement);

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        _appWindow = AppWindow.GetFromWindowId(id);
        _chromeService.TrySetWindowIcon(this, _appWindow);

        _appWindow.Changed += OnAppWindowChanged;
        VisibilityChanged += OnVisibilityChanged;
        Activated += OnWindowActivated;

        if (Content is FrameworkElement rootVisual)
        {
            themeService.Initialize(rootVisual);
        }

        handleProvider.MainWindowHandle = hwnd;
        dialogHost.Attach(this);

        Closed += OnWindowClosed;
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        _lifecycleCoordinator.HandleActivation(args.WindowActivationState, () => Root.ViewModel?.HideAllRevealedTotp());
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _lifecycleCoordinator.HandleWindowClosed(
            () => Root.ViewModel?.HideAllRevealedTotp(),
            () => Root.ViewModel?.Cleanup());
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

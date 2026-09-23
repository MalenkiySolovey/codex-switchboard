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
using System.Linq;
using CodexSwitcher.App.Features.Accounts;
using CodexSwitcher.App.Features.Providers;
using CodexSwitcher.App.Localization;
using CodexSwitcher.App.Services;
using CodexSwitcher.App.Shell;
using CodexSwitcher.App.Features.Settings;
using CodexSwitcher.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace CodexSwitcher.App.Views;

public sealed partial class ShellView : UserControl
{
    private AppPaths? _paths;
    private Func<SettingsViewModel>? _settingsVmFactory;

    public ShellViewModel ViewModel { get; private set; } = null!;
    public Strings Loc => Strings.Current;

    /// <summary>Elemento usado como região de arraste da barra de título (Mica).</summary>
    public UIElement TitleBarElement => AppTitleBar;

    public ShellView()
    {
        InitializeComponent();
        TrySetTitleBarIcon();
        Loaded += OnLoaded;
    }

    public void Initialize(ShellViewModel viewModel, AppPaths paths, Func<SettingsViewModel> settingsVmFactory)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _settingsVmFactory = settingsVmFactory ?? throw new ArgumentNullException(nameof(settingsVmFactory));
    }

    private void TrySetTitleBarIcon()
    {
        try
        {
            var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico");
            if (System.IO.File.Exists(iconPath))
                AppIcon.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(iconPath));
        }
        catch (Exception)
        {
            // Ícone é cosmético.
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        StartupTracer.Instance.RecordMilestone("T11:ShellLoaded");
        Loaded -= OnLoaded;
        if (ViewModel is not null)
        {
            await ViewModel.LoadCommand.ExecuteAsync(null);
        }

        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            StartupTracer.Instance.RecordMilestone("T12:UIInteractive");
            StartupTracer.Instance.FlushToFile();
        });
    }

    private TotpWindow? _totpWindow;

    private void OnOpen2FaClick(object sender, RoutedEventArgs e)
    {
        // Reaproveita a janelinha se já estiver aberta (evita duplicatas).
        if (_totpWindow is not null)
        {
            _totpWindow.Activate();
            return;
        }

        _totpWindow = new TotpWindow();
        _totpWindow.Closed += (_, _) => _totpWindow = null;
        _totpWindow.Activate();
    }

    private PrivateBrowserWindow? _browserWindow;

    private void OnOpenBrowserClick(object sender, RoutedEventArgs e)
    {
        // Reaproveita a janela se já estiver aberta (evita duplicatas).
        if (_browserWindow is not null)
        {
            _browserWindow.Activate();
            return;
        }

        var paths = _paths ?? throw new InvalidOperationException("AppPaths não inicializado.");
        _browserWindow = new PrivateBrowserWindow(paths);
        _browserWindow.Closed += (_, _) => _browserWindow = null;
        _browserWindow.Activate();
    }

    private SettingsWindow? _settingsWindow;

    private void OnOpenSettingsClick(object sender, RoutedEventArgs e)
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        var factory = _settingsVmFactory ?? throw new InvalidOperationException("SettingsViewModel factory não inicializada.");
        var vm = factory();

        vm.OnMigrationCompleted = () =>
        {
            DispatcherQueue.TryEnqueue(async () =>
            {
                if (ViewModel is not null)
                {
                    await ViewModel.LoadCommand.ExecuteAsync(null);
                }
            });
        };

        _settingsWindow = new SettingsWindow(vm);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Activate();
    }

    private static AccountItemViewModel? ItemOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as AccountItemViewModel;

    private void OnSwitchClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) ViewModel.Accounts.SwitchCommand.Execute(item);
    }

    private void OnRefreshAccountClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) ViewModel.Accounts.RefreshAccountCommand.Execute(item);
    }

    private void OnRevealTotpClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) ViewModel.Accounts.RevealTotpCommand.Execute(item);
    }

    private void OnHideTotpClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) ViewModel.Accounts.HideTotpCommand.Execute(item);
    }

    private void OnCopyTotpClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) ViewModel.Accounts.CopyTotpCodeCommand.Execute(item);
    }

    private void OnAddOrManageTotpClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) ViewModel.Accounts.AddOrManageTotpCommand.Execute(item);
    }

    private void OnToggleCollapseClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            ViewModel.Accounts.ToggleAccountCollapseCommand.Execute(item);
            sw.Stop();
            System.Diagnostics.Debug.WriteLine($"[PerfDiag] Account toggle collapse (Id={item.Id}, IsCompact={item.IsCompact}) took {sw.ElapsedMilliseconds}ms ({sw.ElapsedTicks} ticks)");
        }
    }

    private void OnExportItemClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) ViewModel.Accounts.ExportOneCommand.Execute(item);
    }

    private void OnRenameItemClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) ViewModel.Accounts.RenameCommand.Execute(item);
    }

    private void OnSubscriptionTrackingClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) ViewModel.Accounts.EditSubscriptionTrackingCommand.Execute(item);
    }

    private void OnRemoveItemClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) ViewModel.Accounts.RemoveCommand.Execute(item);
    }

    private void OnReauthenticateClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) ViewModel.Accounts.ReauthenticateCommand.Execute(item);
    }

    private void OnMarkReLoginClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) ViewModel.Accounts.MarkNeedsReLoginCommand.Execute(item);
    }

    private void OnMarkUsedClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) ViewModel.Accounts.MarkUsedCommand.Execute(item);
    }

    private void OnUnmarkUsedClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) ViewModel.Accounts.UnmarkUsedCommand.Execute(item);
    }

    private void OnAccountsReordered(object sender, DragItemsCompletedEventArgs e)
    {
        if (e.DropResult != DataPackageOperation.Move) return;
        var orderedIds = ViewModel.Accounts.Items.Select(a => a.Id).ToList();
        ViewModel.Accounts.ReorderCommand.Execute(orderedIds);
    }

    private static ApiProviderItemViewModel? ApiItemOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as ApiProviderItemViewModel;

    private void OnSwitchApiClick(object sender, RoutedEventArgs e)
    {
        if (ApiItemOf(sender) is { } item) ViewModel.ApiProviders.SwitchToApiProviderCommand.Execute(item);
    }

    private void OnToggleRouteClick(object sender, RoutedEventArgs e)
    {
        if (ApiItemOf(sender) is { } item) ViewModel.ApiProviders.ToggleRouteCommand.Execute(item);
    }

    private void OnContinueOnClick(object sender, RoutedEventArgs e)
    {
        if (ApiItemOf(sender) is { } item) ViewModel.ApiProviders.ContinueOnCommand.Execute(item);
    }

    private void OnDiscoverModelsClick(object sender, RoutedEventArgs e)
    {
        if (ApiItemOf(sender) is { } item) ViewModel.ApiProviders.DiscoverModelsCommand.Execute(item);
    }

    private void OnEditApiProviderClick(object sender, RoutedEventArgs e)
    {
        if (ApiItemOf(sender) is { } item) ViewModel.ApiProviders.EditApiProviderCommand.Execute(item);
    }

    private void OnCloneApiProviderClick(object sender, RoutedEventArgs e)
    {
        if (ApiItemOf(sender) is { } item) ViewModel.ApiProviders.CloneProfileCommand.Execute(item);
    }

    private void OnCloneWithNewKeyClick(object sender, RoutedEventArgs e)
    {
        if (ApiItemOf(sender) is { } item) ViewModel.ApiProviders.CloneWithNewKeyCommand.Execute(item);
    }

    private void OnAddAnotherModelClick(object sender, RoutedEventArgs e)
    {
        if (ApiItemOf(sender) is { } item) ViewModel.ApiProviders.AddAnotherModelCommand.Execute(item);
    }

    private void OnRetestCompatibilityClick(object sender, RoutedEventArgs e)
    {
        if (ApiItemOf(sender) is { } item) ViewModel.ApiProviders.RetestCompatibilityCommand.Execute(item);
    }

    private void OnExportDiagnosticsClick(object sender, RoutedEventArgs e)
    {
        if (ApiItemOf(sender) is { } item) ViewModel.ApiProviders.ExportDiagnosticsCommand.Execute(item);
    }

    private void OnRotateApiKeyClick(object sender, RoutedEventArgs e)
    {
        if (ApiItemOf(sender) is { } item) ViewModel.ApiProviders.RotateApiKeyCommand.Execute(item);
    }

    private void OnRemoveApiCredentialClick(object sender, RoutedEventArgs e)
    {
        if (ApiItemOf(sender) is { } item) ViewModel.ApiProviders.RemoveCredentialCommand.Execute(item);
    }

    private void OnRemoveApiProviderClick(object sender, RoutedEventArgs e)
    {
        if (ApiItemOf(sender) is { } item) ViewModel.ApiProviders.RemoveApiProviderCommand.Execute(item);
    }

    private void OnMoveUpApiProviderClick(object sender, RoutedEventArgs e)
    {
        if (ApiItemOf(sender) is { } item) ViewModel.ApiProviders.MoveUpCommand.Execute(item);
    }

    private void OnMoveDownApiProviderClick(object sender, RoutedEventArgs e)
    {
        if (ApiItemOf(sender) is { } item) ViewModel.ApiProviders.MoveDownCommand.Execute(item);
    }

    private void OnApiProvidersReordered(object sender, DragItemsCompletedEventArgs e)
    {
        if (e.DropResult != DataPackageOperation.Move) return;
        var orderedIds = ViewModel.ApiProviders.Items.Select(a => a.Id).ToList();
        ViewModel.ApiProviders.ReorderCommand.Execute(orderedIds);
    }

    private void OnSelectChatGptTabClick(object sender, RoutedEventArgs e)
    {
        ViewModel.SelectedTab = 0;
    }

    private void OnSelectApiProvidersTabClick(object sender, RoutedEventArgs e)
    {
        ViewModel.SelectedTab = 1;
    }
}

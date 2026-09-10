using System.Linq;
using CodexSwitcher.App.Localization;
using CodexSwitcher.App.Services;
using CodexSwitcher.App.ViewModels;
using CodexSwitcher.Infra;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace CodexSwitcher.App.Views;

public sealed partial class AccountsView : UserControl
{
    public MainViewModel ViewModel { get; }
    public Strings Loc => Strings.Current;

    /// <summary>Elemento usado como região de arraste da barra de título (Mica).</summary>
    public UIElement TitleBarElement => AppTitleBar;

    public AccountsView()
    {
        InitializeComponent();
        ViewModel = AppHost.Services.GetService(typeof(MainViewModel)) as MainViewModel
                    ?? throw new InvalidOperationException("MainViewModel não registrado.");
        TrySetTitleBarIcon();
        Loaded += OnLoaded;
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
        Loaded -= OnLoaded;
        await ViewModel.LoadCommand.ExecuteAsync(null);
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

        var paths = AppHost.Services.GetService(typeof(AppPaths)) as AppPaths
                    ?? throw new InvalidOperationException("AppPaths não registrado.");
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

        var vm = AppHost.Services.GetService(typeof(SettingsViewModel)) as SettingsViewModel
                 ?? throw new InvalidOperationException("SettingsViewModel não registrado.");

        vm.OnMigrationCompleted = () =>
        {
            DispatcherQueue.TryEnqueue(async () =>
            {
                await ViewModel.LoadCommand.ExecuteAsync(null);
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
        if (ItemOf(sender) is { } item) ViewModel.SwitchCommand.Execute(item);
    }

    private void OnRefreshAccountClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) ViewModel.RefreshAccountCommand.Execute(item);
    }

    private void OnToggleCollapseClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) ViewModel.ToggleAccountCollapseCommand.Execute(item);
    }

    private void OnExportItemClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) ViewModel.ExportOneCommand.Execute(item);
    }

    private void OnRenameItemClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) ViewModel.RenameCommand.Execute(item);
    }

    private void OnSubscriptionTrackingClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) ViewModel.EditSubscriptionTrackingCommand.Execute(item);
    }

    private void OnRemoveItemClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) ViewModel.RemoveCommand.Execute(item);
    }

    private void OnMarkReLoginClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) ViewModel.MarkNeedsReLoginCommand.Execute(item);
    }

    private void OnMarkUsedClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) ViewModel.MarkUsedCommand.Execute(item);
    }

    private void OnUnmarkUsedClick(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) ViewModel.UnmarkUsedCommand.Execute(item);
    }

    private void OnAccountsReordered(object sender, DragItemsCompletedEventArgs e)
    {
        if (e.DropResult != DataPackageOperation.Move) return;
        var orderedIds = ViewModel.Accounts.Select(a => a.Id).ToList();
        ViewModel.ReorderCommand.Execute(orderedIds);
    }
}

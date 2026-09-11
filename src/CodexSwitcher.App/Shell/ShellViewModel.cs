using CodexSwitcher.App.Features.Accounts;
using CodexSwitcher.App.Features.Providers;
using CodexSwitcher.App.Shell.Routing;
using CodexSwitcher.App.Shell.State;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CodexSwitcher.App.Shell;

/// <summary>
/// Shell coordinator ViewModel: manages top-level navigation tabs, window-level lifecycle delegation,
/// and composes feature slices (<see cref="AccountsViewModel"/> and <see cref="ApiProvidersViewModel"/>)
/// alongside shared presentation state (<see cref="IAppNotificationService"/>, <see cref="IAppBusyService"/>,
/// and <see cref="ActiveTargetPresentationCoordinator"/>).
/// Holds zero dependencies on backend storage, credential vaults, or filesystem paths.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject, IDisposable
{
    public AccountsViewModel Accounts { get; }
    public ApiProvidersViewModel ApiProviders { get; }
    public ActiveTargetPresentationCoordinator Routing { get; }
    public IAppNotificationService Notifications { get; }
    public IAppBusyService Busy { get; }

    [ObservableProperty] public partial int SelectedTab { get; set; }

    public ShellViewModel(
        AccountsViewModel accounts,
        ApiProvidersViewModel apiProviders,
        ActiveTargetPresentationCoordinator routing,
        IAppNotificationService notifications,
        IAppBusyService busy)
    {
        Accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        ApiProviders = apiProviders ?? throw new ArgumentNullException(nameof(apiProviders));
        Routing = routing ?? throw new ArgumentNullException(nameof(routing));
        Notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        Busy = busy ?? throw new ArgumentNullException(nameof(busy));

        Accounts.TargetStateChanged += (_, _) => RefreshRoutingSummary();
        ApiProviders.TargetStateChanged += (_, _) => RefreshRoutingSummary();
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        await Accounts.LoadCommand.ExecuteAsync(null);
        RefreshRoutingSummary();
    }

    public void RefreshRoutingSummary()
    {
        var activeTarget = Routing.RefreshRouting(Accounts.ActiveProfile);
        Accounts.Rebuild(activeTarget, Routing.IsRoutingActiveToApi);
        ApiProviders.Rebuild(activeTarget, Routing.IsRoutingActiveToApi);
    }

    [RelayCommand]
    private void SelectChatGptTab() => SelectedTab = 0;

    [RelayCommand]
    private void SelectApiProvidersTab() => SelectedTab = 1;

    public void SetForegroundActive(bool active) => Accounts.SetForegroundActive(active);

    public void HideAllRevealedTotp() => Accounts.HideAllRevealedTotp();

    public void Cleanup() => Accounts.Cleanup();

    public void Dispose()
    {
        Accounts.Dispose();
        ApiProviders.Dispose();
    }
}

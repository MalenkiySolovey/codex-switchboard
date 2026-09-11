using CodexSwitcher.App.Features.Accounts;
using CodexSwitcher.App.Features.Providers;
using CodexSwitcher.App.Localization;
using CodexSwitcher.App.Services;
using CodexSwitcher.Core.Abstractions;
using CodexSwitcher.Core.Models;
using CodexSwitcher.Infra;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;

namespace CodexSwitcher.App.ViewModels;

/// <summary>
/// Shell coordinator ViewModel: manages tabs, global notifications, busy overlay,
/// active routing target summary banner, and delegates feature slices to
/// <see cref="AccountsViewModel"/> and <see cref="ApiProvidersViewModel"/>.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly ICodexActiveTargetResolver? _activeTargetResolver;
    private readonly AppPaths? _paths;
    private readonly Strings _loc = Strings.Current;

    public AccountsViewModel Accounts { get; }
    public ApiProvidersViewModel ApiProviders { get; }

    [ObservableProperty] public partial int SelectedTab { get; set; }
    [ObservableProperty] public partial string ActiveTargetSummary { get; set; }
    [ObservableProperty] public partial string ActiveTargetCredentialSlot { get; set; }
    [ObservableProperty] public partial bool IsRoutingActiveToApi { get; set; }

    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string? BusyText { get; set; }
    [ObservableProperty] public partial bool InfoOpen { get; set; }
    [ObservableProperty] public partial string InfoMessage { get; set; }
    [ObservableProperty] public partial string InfoTitle { get; set; }
    [ObservableProperty] public partial InfoBarSeverity InfoSeverity { get; set; }

    public MainViewModel(
        AccountsViewModel accounts,
        ApiProvidersViewModel apiProviders,
        ICodexActiveTargetResolver? activeTargetResolver = null,
        AppPaths? paths = null)
    {
        Accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        ApiProviders = apiProviders ?? throw new ArgumentNullException(nameof(apiProviders));
        _activeTargetResolver = activeTargetResolver;
        _paths = paths;

        Accounts.InfoRequested += ShowInfo;
        Accounts.RunBusyRequested += RunBusy;
        Accounts.TargetStateChanged += (_, _) => RefreshRoutingSummary();

        ApiProviders.InfoRequested += ShowInfo;
        ApiProviders.RunBusyRequested += RunBusy;
        ApiProviders.TargetStateChanged += (_, _) => RefreshRoutingSummary();

        InfoMessage = string.Empty;
        InfoTitle = string.Empty;
        InfoSeverity = InfoBarSeverity.Informational;
        ActiveTargetSummary = string.Empty;
        ActiveTargetCredentialSlot = string.Empty;
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        await Accounts.LoadCommand.ExecuteAsync(null);
        RefreshRoutingSummary();
    }

    public void RefreshRoutingSummary()
    {
        var activeProfile = Accounts.ActiveProfile;
        var configTomlPath = _paths?.Codex.ConfigTomlPath
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "config.toml");

        var activeTarget = _activeTargetResolver?.ResolveActiveTarget(configTomlPath, activeProfile?.Id, activeProfile?.AccountEmail)
            ?? new ActiveTarget.ChatGpt(activeProfile?.Id, activeProfile?.AccountEmail);

        if (activeTarget is ActiveTarget.Api apiTarget)
        {
            IsRoutingActiveToApi = true;
            ActiveTargetSummary = $"{apiTarget.Profile.Nickname} ({apiTarget.Profile.SelectedModel})";
            ActiveTargetCredentialSlot = activeProfile?.DisplayName ?? _loc.NoManaged;
        }
        else
        {
            IsRoutingActiveToApi = false;
            ActiveTargetSummary = activeProfile?.DisplayName ?? _loc.NoManaged;
            ActiveTargetCredentialSlot = activeProfile?.DisplayName ?? _loc.NoManaged;
        }

        Accounts.Rebuild(activeTarget, IsRoutingActiveToApi);
        ApiProviders.Rebuild(activeTarget, IsRoutingActiveToApi);
    }

    [RelayCommand]
    private void SelectChatGptTab() => SelectedTab = 0;

    [RelayCommand]
    private void SelectApiProvidersTab() => SelectedTab = 1;

    public void SetForegroundActive(bool active) => Accounts.SetForegroundActive(active);

    public void HideAllRevealedTotp() => Accounts.HideAllRevealedTotp();

    public void Cleanup() => Accounts.Cleanup();

    public void ShowInfo(string title, string message, InfoBarSeverity severity)
    {
        InfoTitle = title;
        InfoMessage = message;
        InfoSeverity = severity;
        InfoOpen = true;
    }

    public async Task RunBusy(string text, Func<Task> action)
    {
        try
        {
            IsBusy = true;
            BusyText = text;
            await action();
        }
        catch (Exception ex)
        {
            ShowInfo(_loc.ErrorTitle, ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            IsBusy = false;
            BusyText = null;
        }
    }

    public void Dispose()
    {
        Accounts.Dispose();
        ApiProviders.Dispose();
    }
}

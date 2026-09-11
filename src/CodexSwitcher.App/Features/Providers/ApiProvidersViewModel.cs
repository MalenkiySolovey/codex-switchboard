using System.Collections.ObjectModel;
using CodexSwitcher.App.Localization;
using CodexSwitcher.App.Services;
using CodexSwitcher.App.ViewModels;
using CodexSwitcher.Core.Abstractions;
using CodexSwitcher.Core.Catalog;
using CodexSwitcher.Core.Models;
using CodexSwitcher.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;

namespace CodexSwitcher.App.Features.Providers;

/// <summary>
/// Dedicated ViewModel orchestrating presentation, commands, and workflows for API Providers.
/// Isolates provider capabilities, route switching, model discovery, and cross-provider thread handoff.
/// Strictly enforces that stored plaintext credentials are never retrieved into the presentation layer.
/// </summary>
public sealed partial class ApiProvidersViewModel : ObservableObject, IDisposable
{
    private static Strings Loc => Strings.Current;

    private readonly IApiProviderStore _apiProviderStore;
    private readonly IProviderInspectionService _inspectionService;
    private readonly IApiKeySecretStore _secretStore;
    private readonly ICodexTargetSwitchService _targetSwitchService;
    private readonly ICodexThreadHandoffService _threadHandoffService;
    private readonly IProviderCatalogService _catalogService;
    private readonly IProviderModelCache _modelCache;
    private readonly ProfileService _profileService;
    private readonly IUiInteraction _ui;
    private readonly IClock _clock;
    private readonly AppSettings _settings;
    private readonly CancellationTokenSource _cts = new();

    public ObservableCollection<ApiProviderItemViewModel> Items { get; } = [];

    [ObservableProperty]
    public partial bool ShowEmptyState { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string? BusyText { get; set; }

    public event EventHandler? TargetStateChanged;
    public event Action<string, string, InfoBarSeverity>? InfoRequested;
    public event Func<string, Func<Task>, Task>? RunBusyRequested;

    public ApiProvidersViewModel(
        IApiProviderStore apiProviderStore,
        IProviderInspectionService inspectionService,
        IApiKeySecretStore secretStore,
        ICodexTargetSwitchService targetSwitchService,
        ICodexThreadHandoffService threadHandoffService,
        IProviderCatalogService catalogService,
        IProviderModelCache modelCache,
        ProfileService profileService,
        IUiInteraction ui,
        IClock clock,
        AppSettings settings,
        IAppLifetime? appLifetime = null)
    {
        _apiProviderStore = apiProviderStore ?? throw new ArgumentNullException(nameof(apiProviderStore));
        _inspectionService = inspectionService ?? throw new ArgumentNullException(nameof(inspectionService));
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        _targetSwitchService = targetSwitchService ?? throw new ArgumentNullException(nameof(targetSwitchService));
        _threadHandoffService = threadHandoffService ?? throw new ArgumentNullException(nameof(threadHandoffService));
        _catalogService = catalogService ?? throw new ArgumentNullException(nameof(catalogService));
        _modelCache = modelCache ?? throw new ArgumentNullException(nameof(modelCache));
        _profileService = profileService ?? throw new ArgumentNullException(nameof(profileService));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));

        appLifetime?.ApplicationStopping.Register(() => CancelOperations());
    }

    /// <summary>
    /// Rebuilds the provider cards list with updated active routing indicators and model caches.
    /// </summary>
    public void Rebuild(ActiveTarget? activeTarget, bool isRoutingActiveToApi)
    {
        Items.Clear();
        var apiProfiles = _apiProviderStore.GetAll();
        foreach (var prof in apiProfiles)
        {
            var desc = _catalogService.GetDescriptor(prof.CatalogProviderId);
            bool hasSecret = _secretStore.HasApiKey(prof.Id);
            bool isTargetActive = isRoutingActiveToApi && activeTarget is ActiveTarget.Api a && a.Profile.Id == prof.Id;
            var vm = new ApiProviderItemViewModel(prof, desc, hasSecret, isTargetActive);
            var routeKey = !string.IsNullOrWhiteSpace(prof.SelectedRouteId) ? prof.SelectedRouteId : prof.BaseUrl;
            if (_modelCache.TryGetModels(prof.Id, routeKey, 1, out var cachedModels) && cachedModels.Count > 0)
            {
                vm.SetDiscoveredModels(cachedModels);
            }
            Items.Add(vm);
        }
        ShowEmptyState = Items.Count == 0;
    }

    public void CancelOperations()
    {
        try
        {
            if (!_cts.IsCancellationRequested)
            {
                _cts.Cancel();
            }
        }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        CancelOperations();
        _cts.Dispose();
    }

    private void ShowInfo(string title, string message, InfoBarSeverity severity)
    {
        InfoRequested?.Invoke(title, message, severity);
    }

    private async Task RunBusyAsync(string text, Func<Task> action)
    {
        if (RunBusyRequested != null)
        {
            await RunBusyRequested.Invoke(text, action);
        }
        else
        {
            IsBusy = true;
            BusyText = text;
            try
            {
                await action();
            }
            finally
            {
                IsBusy = false;
                BusyText = null;
            }
        }
    }

    [RelayCommand]
    public async Task AddApiProviderAsync()
    {
        var descriptors = _catalogService.CurrentResult.Catalog.Providers ?? (IReadOnlyList<ProviderDescriptor>)Array.Empty<ProviderDescriptor>();
        var result = await _ui.PromptAddApiProviderAsync(descriptors);
        if (result is null) return;

        var id = Guid.NewGuid();
        var profile = new ApiProviderProfile
        {
            Id = id,
            Nickname = result.Nickname,
            CatalogProviderId = result.CatalogProviderId,
            StableCodexProviderId = ApiProviderProfile.GenerateStableCodexProviderId(id),
            BaseUrl = result.BaseUrl,
            SelectedRouteId = result.SelectedRouteId,
            SelectedModel = result.SelectedModel,
            KeyPreview = ApiProviderProfile.ComputeKeyPreview(result.ApiKey),
            Status = string.IsNullOrWhiteSpace(result.ApiKey) ? ApiProviderProfileStatus.CredentialMissing : ApiProviderProfileStatus.Active,
            CreatedAt = _clock.UtcNow,
        };

        if (!string.IsNullOrWhiteSpace(result.ApiKey))
        {
            _secretStore.SaveApiKey(profile.Id, result.ApiKey);
        }

        _apiProviderStore.Save(profile);

        if (result.SaveAndSwitch)
        {
            await SwitchToApiProviderInternalAsync(profile);
        }

        TargetStateChanged?.Invoke(this, EventArgs.Empty);
        ShowInfo(Loc.ProviderDialogTitle, $"API Provider '{profile.Nickname}' saved.", InfoBarSeverity.Success);
    }

    [RelayCommand]
    public async Task SwitchToApiProviderAsync(ApiProviderItemViewModel? item)
    {
        if (item is null || !item.CanSwitch) return;

        if (!item.HasSecret)
        {
            ShowInfo(Loc.ErrorTitle, Loc.ApiKeyRequired, InfoBarSeverity.Warning);
            return;
        }

        if (_settings.AlwaysConfirmSwitch && !await _ui.ConfirmSwitchToApiAsync(item.DisplayName, item.SelectedModel, item.SelectedRoute))
            return;

        await SwitchToApiProviderInternalAsync(item.Profile);
        TargetStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task SwitchToApiProviderInternalAsync(ApiProviderProfile profile)
    {
        await RunBusyAsync(Loc.BusySwitching(profile.Nickname), async () =>
        {
            var res = await _targetSwitchService.SwitchToApiProviderAsync(
                profile.Id,
                SwitchExecutionOptions.From(_settings));

            if (res.Outcome is TargetSwitchOutcome.Success or TargetSwitchOutcome.SuccessWithReopenWarning)
            {
                ShowInfo(Loc.SwitchedTitle, Loc.SwitchedMsg(profile.Nickname), InfoBarSeverity.Success);
            }
            else
            {
                ShowInfo(Loc.SwitchFailedTitle, Loc.SwitchFailedMsg, InfoBarSeverity.Error);
            }
        });
    }

    [RelayCommand]
    public async Task ToggleRouteAsync(ApiProviderItemViewModel? item)
    {
        if (item is null || item.Routes.Count <= 1 || item.Descriptor is null) return;

        var currentRouteId = item.Profile.SelectedRouteId;
        var routes = item.Descriptor.Routes;
        var currentIndex = routes.FindIndex(r => r.Id.Equals(currentRouteId, StringComparison.OrdinalIgnoreCase));
        var nextIndex = currentIndex >= 0 ? (currentIndex + 1) % routes.Count : 0;
        var nextRoute = routes[nextIndex];

        item.Profile.SelectedRouteId = nextRoute.Id;
        item.Profile.BaseUrl = nextRoute.BaseUrl;

        _apiProviderStore.Save(item.Profile);

        if (item.IsTargetActive)
        {
            await _targetSwitchService.SwitchApiRouteAsync(
                item.Profile.Id,
                nextRoute.Id,
                nextRoute.BaseUrl,
                SwitchExecutionOptions.From(_settings));
        }

        TargetStateChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    public async Task SwitchRouteAsync((ApiProviderItemViewModel? Item, string RouteId) args)
    {
        var (item, routeId) = args;
        if (item is null || item.Descriptor is null || string.IsNullOrWhiteSpace(routeId)) return;

        var route = item.Descriptor.Routes.FirstOrDefault(r => r.Id.Equals(routeId, StringComparison.OrdinalIgnoreCase));
        if (route is null || route.Id.Equals(item.Profile.SelectedRouteId, StringComparison.OrdinalIgnoreCase)) return;

        item.Profile.SelectedRouteId = route.Id;
        item.Profile.BaseUrl = route.BaseUrl;

        _apiProviderStore.Save(item.Profile);

        if (item.IsTargetActive)
        {
            await _targetSwitchService.SwitchApiRouteAsync(
                item.Profile.Id,
                route.Id,
                route.BaseUrl,
                SwitchExecutionOptions.From(_settings));
        }

        TargetStateChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    public async Task EditApiProviderAsync(ApiProviderItemViewModel? item)
    {
        if (item is null) return;

        var result = await _ui.PromptEditApiProviderAsync(item.Profile, item.Descriptor);
        if (result is null) return;

        item.Profile.Nickname = result.Nickname;
        item.Profile.BaseUrl = result.BaseUrl;
        item.Profile.SelectedRouteId = result.SelectedRouteId;
        item.Profile.SelectedModel = result.SelectedModel;

        _apiProviderStore.Save(item.Profile);

        if (item.IsTargetActive)
        {
            await _targetSwitchService.SwitchToApiProviderAsync(
                item.Profile.Id,
                SwitchExecutionOptions.From(_settings));
        }

        TargetStateChanged?.Invoke(this, EventArgs.Empty);
        ShowInfo(Loc.EditProviderDialogTitle, "Provider updated.", InfoBarSeverity.Success);
    }

    [RelayCommand]
    public async Task RotateApiKeyAsync(ApiProviderItemViewModel? item)
    {
        if (item is null) return;

        var newKey = await _ui.PromptRotateApiKeyAsync(item.DisplayName);
        if (string.IsNullOrWhiteSpace(newKey)) return;

        _secretStore.SaveApiKey(item.Profile.Id, newKey);
        item.Profile.Status = ApiProviderProfileStatus.Active;
        item.Profile.KeyPreview = ApiProviderProfile.ComputeKeyPreview(newKey);
        _apiProviderStore.Save(item.Profile);

        TargetStateChanged?.Invoke(this, EventArgs.Empty);
        ShowInfo(Loc.RotateKeyDialogTitle, "API key updated successfully.", InfoBarSeverity.Success);
    }

    [RelayCommand]
    public async Task RemoveCredentialAsync(ApiProviderItemViewModel? item)
    {
        if (item is null) return;

        bool ok = await _ui.ConfirmAsync(
            Loc.RemoveCredentialConfirmTitle(item.DisplayName),
            Loc.RemoveCredentialConfirmMessage,
            Loc.RemoveCredential,
            destructive: true);

        if (!ok) return;

        _secretStore.DeleteApiKey(item.Profile.Id);
        item.Profile.Status = ApiProviderProfileStatus.CredentialMissing;
        item.Profile.KeyPreview = string.Empty;
        _apiProviderStore.Save(item.Profile);

        if (item.IsTargetActive)
        {
            await _targetSwitchService.SwitchToChatGptAsync(null, _profileService.Profiles, SwitchExecutionOptions.From(_settings));
        }

        TargetStateChanged?.Invoke(this, EventArgs.Empty);
        ShowInfo(Loc.RemoveCredential, "API key credential removed.", InfoBarSeverity.Informational);
    }

    [RelayCommand]
    public async Task RemoveApiProviderAsync(ApiProviderItemViewModel? item)
    {
        if (item is null) return;

        bool ok = await _ui.ConfirmAsync(
            Loc.RemoveTitle,
            Loc.RemoveConfirm(item.DisplayName),
            Loc.Remove,
            destructive: true);

        if (!ok) return;

        _secretStore.DeleteApiKey(item.Profile.Id);
        _apiProviderStore.Delete(item.Profile.Id);

        if (item.IsTargetActive)
        {
            await _targetSwitchService.SwitchToChatGptAsync(null, _profileService.Profiles, SwitchExecutionOptions.From(_settings));
        }

        TargetStateChanged?.Invoke(this, EventArgs.Empty);
        ShowInfo(Loc.RemovedTitle, Loc.RemovedMsg(item.DisplayName), InfoBarSeverity.Informational);
    }

    [RelayCommand]
    public async Task ContinueOnAsync(ApiProviderItemViewModel? item)
    {
        if (item is null) return;

        if (!item.HasSecret)
        {
            ShowInfo(Loc.ErrorTitle, Loc.ApiKeyRequired, InfoBarSeverity.Warning);
            return;
        }

        IReadOnlyList<CodexThreadSummary> threads;
        try
        {
            threads = await _threadHandoffService.ListThreadsAsync(50, _cts.Token);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            ShowInfo(Loc.ErrorTitle, $"Failed to retrieve threads: {ex.Message}", InfoBarSeverity.Error);
            return;
        }

        if (threads.Count == 0)
        {
            ShowInfo(Loc.ThreadPickerTitle, "No recent conversations found in Codex to continue.", InfoBarSeverity.Informational);
            return;
        }

        var targetModel = !string.IsNullOrWhiteSpace(item.SelectedModel)
            ? item.SelectedModel
            : _modelCache.GetLatestModels(item.Profile.Id)?.FirstOrDefault()
              ?? item.Descriptor?.Codex.DefaultModel
              ?? "gpt-5.6-sol";

        var selected = await _ui.PromptContinueOnThreadAsync(threads, item.DisplayName, targetModel);
        if (selected is null) return;

        await RunBusyAsync(Loc.ContinueOn, async () =>
        {
            var forkResult = await _threadHandoffService.ForkThreadAsync(
                selected.Id,
                item.Profile.StableCodexProviderId,
                targetModel,
                null,
                _cts.Token);

            if (!item.IsTargetActive)
            {
                await _targetSwitchService.SwitchToApiProviderAsync(item.Profile.Id, SwitchExecutionOptions.From(_settings), _cts.Token);
            }

            ShowInfo(Loc.ForkSuccessTitle, Loc.ForkSuccessMessage(item.DisplayName, selected.Name ?? selected.Id), InfoBarSeverity.Success);
        });

        TargetStateChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    public async Task DiscoverModelsAsync(ApiProviderItemViewModel? item)
    {
        if (item is null) return;

        item.IsLoadingModels = true;
        item.ModelsStatusMessage = Loc.DiscoveringModels;

        try
        {
            var snapshot = await _inspectionService.InspectAsync(item.Profile.Id, _cts.Token);

            if (snapshot.Models.Count > 0)
            {
                item.SetDiscoveredModels(snapshot.Models);
            }
            else if (!string.IsNullOrWhiteSpace(snapshot.Error))
            {
                item.ModelsStatusMessage = snapshot.Error;
            }
            else
            {
                item.ModelsStatusMessage = "No models discovered.";
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            item.ModelsStatusMessage = "Discovery canceled.";
        }
        catch (Exception ex)
        {
            item.ModelsStatusMessage = ex.Message;
        }
        finally
        {
            item.IsLoadingModels = false;
        }
    }
}

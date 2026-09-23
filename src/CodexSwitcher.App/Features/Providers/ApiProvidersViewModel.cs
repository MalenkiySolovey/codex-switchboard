using CodexSwitcher.Core.Accounts.Contracts;
using CodexSwitcher.Core.Accounts.Formatting;
using CodexSwitcher.Core.Accounts.Models;
using CodexSwitcher.Core.Common.Dispatcher;
using CodexSwitcher.Core.Common.Enums;
using CodexSwitcher.Core.Common.Environment;
using CodexSwitcher.Core.Common.Errors;
using CodexSwitcher.Core.Common.Lifecycle;
using CodexSwitcher.Core.Common.Logging;
using CodexSwitcher.Core.Common.Storage;
using CodexSwitcher.Core.Common.Time;
using CodexSwitcher.Core.Providers.Services;
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
using CodexSwitcher.Core.Routing.Contracts;
using CodexSwitcher.Core.Routing.Models;
using CodexSwitcher.Core.Routing.Services;
using CodexSwitcher.Core.Providers.Catalog;
using CodexSwitcher.Core.Providers.Contracts;
using CodexSwitcher.Core.Providers.Models;
using CodexSwitcher.Core.Accounts.Services;
using System.Collections.ObjectModel;
using CodexSwitcher.App.Dialogs;
using CodexSwitcher.App.Localization;
using CodexSwitcher.App.Services;
using CodexSwitcher.App.Shell.State;
using CodexSwitcher.App.ViewModels;
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
    private readonly IApiModelInventoryRefreshService _modelRefreshService;
    private readonly IApiKeySecretStore _secretStore;
    private readonly ICodexTargetSwitchService _targetSwitchService;
    private readonly ICodexThreadHandoffService _threadHandoffService;
    private readonly IProviderCatalogService _catalogService;
    private readonly IProviderModelCache _modelCache;
    private readonly ProfileService _profileService;
    private readonly IProviderDialogService _ui;
    private readonly IClock _clock;
    private readonly AppSettings _settings;
    private readonly IAppNotificationService _notifications;
    private readonly IAppBusyService _busyService;
    private readonly IProviderCompatibilityProbeService? _probeService;
    private readonly IProcessManager? _processManager;
    private readonly IUiDispatcher _dispatcher;
    private readonly CancellationTokenSource _cts = new();

    public ObservableCollection<ApiProviderItemViewModel> Items { get; } = [];

    [ObservableProperty]
    public partial bool ShowEmptyState { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string? BusyText { get; set; }

    public event EventHandler? TargetStateChanged;

    public ApiProvidersViewModel(
        IApiProviderStore apiProviderStore,
        IApiModelInventoryRefreshService modelRefreshService,
        IApiKeySecretStore secretStore,
        ICodexTargetSwitchService targetSwitchService,
        ICodexThreadHandoffService threadHandoffService,
        IProviderCatalogService catalogService,
        IProviderModelCache modelCache,
        ProfileService profileService,
        IProviderDialogService ui,
        IClock clock,
        AppSettings settings,
        IAppNotificationService notifications,
        IAppBusyService busyService,
        IAppLifetime? appLifetime = null,
        IProviderCompatibilityProbeService? probeService = null,
        IProcessManager? processManager = null,
        IUiDispatcher? dispatcher = null)
    {
        _apiProviderStore = apiProviderStore ?? throw new ArgumentNullException(nameof(apiProviderStore));
        _modelRefreshService = modelRefreshService ?? throw new ArgumentNullException(nameof(modelRefreshService));
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        _targetSwitchService = targetSwitchService ?? throw new ArgumentNullException(nameof(targetSwitchService));
        _threadHandoffService = threadHandoffService ?? throw new ArgumentNullException(nameof(threadHandoffService));
        _catalogService = catalogService ?? throw new ArgumentNullException(nameof(catalogService));
        _modelCache = modelCache ?? throw new ArgumentNullException(nameof(modelCache));
        _profileService = profileService ?? throw new ArgumentNullException(nameof(profileService));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _busyService = busyService ?? throw new ArgumentNullException(nameof(busyService));
        _probeService = probeService;
        _processManager = processManager;
        _dispatcher = dispatcher ?? ImmediateUiDispatcher.Instance;
        _modelRefreshService.RefreshStateChanged += OnRefreshStateChanged;

        appLifetime?.ApplicationStopping.Register(() => CancelOperations());
    }

    /// <summary>
    /// Rebuilds the provider cards list with updated active routing indicators and model caches.
    /// </summary>
    public void Rebuild(ActiveTarget? activeTarget, bool isRoutingActiveToApi)
    {
        var existingMap = Items.ToDictionary(x => x.Id);
        var targetList = new List<ApiProviderItemViewModel>();
        var apiProfiles = _apiProviderStore.GetAll();

        foreach (var prof in apiProfiles)
        {
            var desc = _catalogService.GetDescriptor(prof.CatalogProviderId);
            bool hasSecret = _secretStore.HasApiKey(prof.EndpointId ?? prof.Id);
            bool isTargetActive = isRoutingActiveToApi && activeTarget is ActiveTarget.Api a && a.Profile.Id == prof.Id;

            if (existingMap.TryGetValue(prof.Id, out var existing))
            {
                existing.UpdateProfile(prof, desc, hasSecret, isTargetActive);
                var routeKey = !string.IsNullOrWhiteSpace(prof.SelectedRouteId) ? prof.SelectedRouteId : prof.BaseUrl;
                if (_modelCache.TryGetModels(prof.Id, routeKey, 1, out var cachedModels) && cachedModels.Count > 0)
                {
                    existing.SetDiscoveredModels(cachedModels);
                }
                if (_modelRefreshService.GetLatestResult(prof.Id) is { } latestRefresh)
                {
                    existing.ApplyRefreshResult(latestRefresh);
                }
                targetList.Add(existing);
            }
            else
            {
                var vm = new ApiProviderItemViewModel(prof, desc, hasSecret, isTargetActive);
                var routeKey = !string.IsNullOrWhiteSpace(prof.SelectedRouteId) ? prof.SelectedRouteId : prof.BaseUrl;
                if (_modelCache.TryGetModels(prof.Id, routeKey, 1, out var cachedModels) && cachedModels.Count > 0)
                {
                    vm.SetDiscoveredModels(cachedModels);
                }
                if (_modelRefreshService.GetLatestResult(prof.Id) is { } latestRefresh)
                {
                    vm.ApplyRefreshResult(latestRefresh);
                }
                targetList.Add(vm);
            }
        }

        SyncCollection(Items, targetList);
        ShowEmptyState = Items.Count == 0;
    }

    private static void SyncCollection(ObservableCollection<ApiProviderItemViewModel> collection, List<ApiProviderItemViewModel> target)
    {
        if (collection.SequenceEqual(target))
        {
            return;
        }

        for (int i = collection.Count - 1; i >= 0; i--)
        {
            if (!target.Contains(collection[i]))
            {
                collection.RemoveAt(i);
            }
        }

        for (int i = 0; i < target.Count; i++)
        {
            var item = target[i];
            var currentIndex = collection.IndexOf(item);
            if (currentIndex < 0)
            {
                collection.Insert(i, item);
            }
            else if (currentIndex != i)
            {
                collection.Move(currentIndex, i);
            }
        }
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
        _modelRefreshService.RefreshStateChanged -= OnRefreshStateChanged;
        _cts.Dispose();
    }

    private void OnRefreshStateChanged(object? sender, ModelInventoryRefreshResult result)
    {
        _dispatcher.Enqueue(() =>
        {
            var item = Items.FirstOrDefault(candidate => candidate.Id == result.ProfileId);
            if (item is null)
            {
                return;
            }

            if (_apiProviderStore.GetById(result.ProfileId) is { } persisted)
            {
                item.UpdateProfile(persisted, item.Descriptor, item.HasSecret, item.IsTargetActive);
            }
            item.ApplyRefreshResult(result);
        });
    }

    private void ShowInfo(string title, string message, InfoBarSeverity severity)
    {
        _notifications.Show(title, message, severity);
    }

    private async Task RunBusyAsync(string text, Func<Task> action)
    {
        IsBusy = true;
        BusyText = text;
        try
        {
            await _busyService.RunAsync(text, action);
        }
        finally
        {
            IsBusy = false;
            BusyText = null;
        }
    }

    [RelayCommand]
    public async Task AddApiProviderAsync()
    {
        var descriptors = _catalogService.CurrentResult.Catalog.Providers ?? (IReadOnlyList<ProviderDescriptor>)Array.Empty<ProviderDescriptor>();
        var result = await _ui.PromptAddApiProviderAsync(descriptors);
        if (result is null) return;

        var id = Guid.NewGuid();
        var endpointId = Guid.NewGuid();
        var profile = new ApiProviderProfile
        {
            Id = id,
            EndpointId = endpointId,
            Nickname = result.Nickname,
            CatalogProviderId = result.CatalogProviderId,
            StableCodexProviderId = ApiProviderProfile.GenerateStableCodexProviderId(id),
            BaseUrl = result.BaseUrl,
            SelectedRouteId = result.SelectedRouteId,
            SelectedModel = result.SelectedModel,
            KeyPreview = ApiProviderProfile.ComputeKeyPreview(result.ApiKey),
            Status = string.IsNullOrWhiteSpace(result.ApiKey) ? ApiProviderProfileStatus.CredentialMissing : ApiProviderProfileStatus.Active,
            CreatedAt = _clock.UtcNow,
            ModelOverrides = result.ModelOverrides,
            TransportOverrides = result.TransportOverrides,
            RoutePoolLabel = result.RoutePoolLabel,
            ProviderPresetId = result.ProviderPresetId,
            DiscoveredModels = result.DiscoveredModels,
            CompatibilityLevel = result.CompatibilityLevel,
            LastProbeReport = result.LastProbeReport,
        };

        ApplyDiscoveredModelsToInventory(profile, result.DiscoveredModels, _clock.UtcNow);

        if (!string.IsNullOrWhiteSpace(result.ApiKey))
        {
            _secretStore.SaveApiKey(profile.EndpointId ?? profile.Id, result.ApiKey);
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
            try
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
                    var msg = !string.IsNullOrWhiteSpace(res.Message) ? res.Message : Loc.SwitchFailedMsg;
                    ShowInfo(Loc.SwitchFailedTitle, msg, InfoBarSeverity.Error);
                }
            }
            catch (Exception ex)
            {
                ShowInfo(Loc.SwitchFailedTitle, ex.Message, InfoBarSeverity.Error);
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
        item.Profile.ModelOverrides = result.ModelOverrides;
        item.Profile.TransportOverrides = result.TransportOverrides;
        item.Profile.RoutePoolLabel = result.RoutePoolLabel;
        if (result.ProviderPresetId != null) item.Profile.ProviderPresetId = result.ProviderPresetId;
        if (result.ModelInventory is not null)
        {
            item.Profile.ModelInventory = result.ModelInventory;
        }
        if (result.DiscoveredModels != null)
        {
            ApplyDiscoveredModelsToInventory(item.Profile, result.DiscoveredModels, _clock.UtcNow);
        }
        if (result.CompatibilityLevel != CodexCompatibilityLevel.Unknown) item.Profile.CompatibilityLevel = result.CompatibilityLevel;
        if (result.LastProbeReport != null) item.Profile.LastProbeReport = result.LastProbeReport;

        _apiProviderStore.Save(item.Profile);
        item.UpdateProfile(item.Profile, item.Descriptor, item.HasSecret, item.IsTargetActive);

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

        var secretOwnerId = item.Profile.EndpointId ?? item.Profile.Id;
        _secretStore.SaveApiKey(secretOwnerId, newKey);
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

        var secretOwnerId = item.Profile.EndpointId ?? item.Profile.Id;
        _secretStore.DeleteApiKey(secretOwnerId);
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

        var secretOwnerId = item.Profile.EndpointId ?? item.Profile.Id;
        _apiProviderStore.Delete(item.Profile.Id);

        var remaining = _apiProviderStore.GetAll();
        if (!remaining.Any(p => (p.EndpointId ?? p.Id) == secretOwnerId))
        {
            _secretStore.DeleteApiKey(secretOwnerId);
        }

        if (item.IsTargetActive)
        {
            await _targetSwitchService.SwitchToChatGptAsync(null, _profileService.Profiles, SwitchExecutionOptions.From(_settings));
        }

        TargetStateChanged?.Invoke(this, EventArgs.Empty);
        ShowInfo(Loc.RemovedTitle, Loc.RemovedMsg(item.DisplayName), InfoBarSeverity.Informational);
    }

    [RelayCommand]
    public void MoveUp(ApiProviderItemViewModel? item)
    {
        if (item is null) return;
        var idx = Items.IndexOf(item);
        if (idx <= 0) return;

        Items.Move(idx, idx - 1);
        for (int i = 0; i < Items.Count; i++)
        {
            Items[i].Profile.SortOrder = i;
        }

        _apiProviderStore.SaveAll(Items.Select(x => x.Profile), ApiProviderSaveIntent.NormalUpdate);
    }

    [RelayCommand]
    public void MoveDown(ApiProviderItemViewModel? item)
    {
        if (item is null) return;
        var idx = Items.IndexOf(item);
        if (idx < 0 || idx >= Items.Count - 1) return;

        Items.Move(idx, idx + 1);
        for (int i = 0; i < Items.Count; i++)
        {
            Items[i].Profile.SortOrder = i;
        }

        _apiProviderStore.SaveAll(Items.Select(x => x.Profile), ApiProviderSaveIntent.NormalUpdate);
    }

    [RelayCommand]
    public void Reorder(IReadOnlyList<Guid>? orderedIds)
    {
        if (orderedIds is null || orderedIds.Count == 0) return;

        var order = 0;
        foreach (var id in orderedIds)
        {
            var item = Items.FirstOrDefault(x => x.Id == id);
            if (item is null) continue;
            item.Profile.SortOrder = order++;
        }

        _apiProviderStore.SaveAll(Items.OrderBy(x => x.Profile.SortOrder).Select(x => x.Profile), ApiProviderSaveIntent.NormalUpdate);
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

        // Do not derive a continuation target from card state or source-thread
        // metadata. Reload the destination profile from the authoritative
        // store so an old card can never leak its previous :free/non-free
        // model into a newly selected provider.
        var targetProfile = _apiProviderStore.GetById(item.Id);
        if (targetProfile is null || !TryGetEnabledSelectedModel(targetProfile, out var targetModel))
        {
            ShowInfo(Loc.ErrorTitle, "Select an enabled model before continuing a chat on this API profile.", InfoBarSeverity.Warning);
            return;
        }

        var selected = await _ui.PromptContinueOnThreadAsync(threads, item.DisplayName, targetModel);
        if (selected is null) return;

        // Assess tool compatibility and RequiresFreshThread boundary
        var toolPolicy = EffectiveToolPolicy.Resolve(item.Profile);
        var assessment = await _threadHandoffService.AssessThreadCompatibilityAsync(selected.Id, toolPolicy, _cts.Token);

        if (assessment.RequiresFreshThread)
        {
            var acceptFresh = await _ui.PromptFreshThreadChoiceAsync(
                selected.Name ?? selected.Id,
                assessment.IncompatibleFeatures,
                item.DisplayName,
                targetModel);

            if (!acceptFresh)
            {
                return;
            }

            await RunBusyAsync(Loc.ContinueOn, async () =>
            {
                var switchOptions = SwitchExecutionOptions.From(_settings) with { ReopenDesktopAfterSwitch = false };
                var switchResult = await _targetSwitchService.SwitchToApiProviderAsync(
                    targetProfile.Id,
                    switchOptions,
                    _cts.Token);

                if (switchResult.Outcome != TargetSwitchOutcome.Success &&
                    switchResult.Outcome != TargetSwitchOutcome.SuccessWithReopenWarning &&
                    switchResult.Outcome != TargetSwitchOutcome.NoOp)
                {
                    ShowInfo(Loc.ErrorTitle, $"Routing switch failed: {switchResult.Message}. Thread start aborted.", InfoBarSeverity.Error);
                    return;
                }

                var resolvedTarget = ResolveAuthoritativeTargetProfile(targetProfile.Id);
                if (!TryGetVerifiedTargetModel(resolvedTarget, switchResult, out var exactTargetModel))
                {
                    ShowInfo(Loc.ErrorTitle, "The active Codex provider, selected model, catalog, or runtime model list no longer matches this destination profile. No fresh chat was created.", InfoBarSeverity.Warning);
                    return;
                }

                var freshResult = await _threadHandoffService.StartFreshThreadAsync(
                    resolvedTarget.StableCodexProviderId,
                    exactTargetModel,
                    selected.Cwd,
                    selected.Name != null ? $"{selected.Name} (Fresh)" : null,
                    targetProfileId: resolvedTarget.Id,
                    targetCatalogPath: switchResult.DiagnosticTrace?.EffectiveModelCatalogJson,
                    cancellationToken: _cts.Token);

                var openedInDesktop = await LaunchAndOpenContinuationAsync(freshResult.ForkedThreadId);

                ShowInfo(
                    "Fresh Chat Created",
                    $"Started fresh conversation on {item.DisplayName}.\nProvider: {freshResult.TargetModelProvider}\nModel: {freshResult.TargetModel}\nThread: {freshResult.ForkedThreadId}" +
                    (openedInDesktop
                        ? "\nOpened the exact continuation in Codex Desktop."
                        : "\nContinuation is persisted, but Codex Desktop could not be opened to this thread automatically."),
                    InfoBarSeverity.Success);
            });

            TargetStateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        await RunBusyAsync(Loc.ContinueOn, async () =>
        {
            // Routing switch transaction MUST happen before the fork transaction per ARCH-R6
            var switchOptions = SwitchExecutionOptions.From(_settings) with { ReopenDesktopAfterSwitch = false };
            var switchResult = await _targetSwitchService.SwitchToApiProviderAsync(
                targetProfile.Id,
                switchOptions,
                _cts.Token);

            if (switchResult.Outcome != TargetSwitchOutcome.Success &&
                switchResult.Outcome != TargetSwitchOutcome.SuccessWithReopenWarning &&
                switchResult.Outcome != TargetSwitchOutcome.NoOp)
            {
                ShowInfo(Loc.ErrorTitle, $"Routing switch failed: {switchResult.Message}. Fork aborted.", InfoBarSeverity.Error);
                return;
            }

            var resolvedTarget = ResolveAuthoritativeTargetProfile(targetProfile.Id);
            if (!TryGetVerifiedTargetModel(resolvedTarget, switchResult, out var exactTargetModel))
            {
                ShowInfo(Loc.ErrorTitle, "The active Codex provider, selected model, catalog, or runtime model list no longer matches this destination profile. No continuation was created.", InfoBarSeverity.Warning);
                return;
            }

            var forkResult = await _threadHandoffService.ForkThreadAsync(
                selected.Id,
                    resolvedTarget.StableCodexProviderId,
                    exactTargetModel,
                    null,
                targetProfileId: resolvedTarget.Id,
                targetCatalogPath: switchResult.DiagnosticTrace?.EffectiveModelCatalogJson,
                cancellationToken: _cts.Token);

            var openedInDesktop = await LaunchAndOpenContinuationAsync(forkResult.ForkedThreadId);

            ShowInfo(
                Loc.ForkSuccessTitle,
                $"Continuation created successfully.\nProvider: {forkResult.TargetModelProvider}\nModel: {forkResult.TargetModel}\nThread: {forkResult.ForkedThreadId}" +
                (openedInDesktop
                    ? "\nOpened the exact continuation in Codex Desktop."
                    : "\nContinuation is persisted, but Codex Desktop could not be opened to this thread automatically."),
                InfoBarSeverity.Success);
        });

        TargetStateChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    public async Task DiscoverModelsAsync(ApiProviderItemViewModel? item)
    {
        if (item is null) return;
        if (item.IsLoadingModels) return;

        item.IsLoadingModels = true;
        item.ModelsStatusMessage = Loc.DiscoveringModels;

        try
        {
            long requestGeneration = 0;
            var progress = new Progress<string>(message =>
            {
                var latest = _modelRefreshService.GetLatestResult(item.Id);
                if (requestGeneration > 0 && latest?.RequestGeneration == requestGeneration &&
                    latest.Outcome == ModelInventoryRefreshOutcome.Refreshing)
                {
                    item.ModelsStatusMessage = message;
                }
            });
            var refreshTask = _modelRefreshService.RefreshAsync(
                item.Id,
                new RefreshModelsOptions(SwitchExecutionOptions.From(_settings), progress),
                _cts.Token);
            requestGeneration = _modelRefreshService.GetLatestResult(item.Id)?.RequestGeneration ?? 0;
            var result = await refreshTask;
            item.ApplyRefreshResult(result);
            if (_apiProviderStore.GetById(item.Id) is { } persisted)
            {
                item.UpdateProfile(persisted, item.Descriptor, item.HasSecret, item.IsTargetActive);
                item.ApplyRefreshResult(result);
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

    private ApiProviderProfile ResolveAuthoritativeTargetProfile(Guid profileId) =>
        _apiProviderStore.GetById(profileId)
        ?? throw new InvalidOperationException("Destination API profile no longer exists.");

    private async Task<bool> LaunchAndOpenContinuationAsync(string threadId)
    {
        if (_processManager is null || string.IsNullOrWhiteSpace(threadId))
        {
            return false;
        }

        if (!_processManager.TryLaunchDesktop())
        {
            return false;
        }

        // Wait for the registered MSIX app to appear in the process inventory
        // before sending codex://threads/<id>; ShellExecute acceptance alone
        // does not prove that Desktop has finished starting.
        for (var attempt = 0; attempt < 60; attempt++)
        {
            if (_cts.IsCancellationRequested)
            {
                return false;
            }

            try
            {
                if (_processManager.FindRunningCodexProcesses().Any(process => process.Kind == CodexProcessKind.DesktopApp))
                {
                    return _processManager.TryOpenThreadDeepLink(threadId);
                }
            }
            catch
            {
                return false;
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), _cts.Token);
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                return false;
            }
        }

        return false;
    }

    private static bool TryGetEnabledSelectedModel(ApiProviderProfile profile, out string model)
    {
        model = profile.SelectedModel ?? string.Empty;
        if (string.IsNullOrWhiteSpace(model))
        {
            return false;
        }

        var inventory = profile.ModelInventory;
        if (inventory is null || inventory.Models.Count == 0)
        {
            // Legacy profile migration belongs to the target transaction, where
            // it is persisted atomically with the configuration projection.
            return true;
        }

        var selectedModel = model;
        var candidate = inventory.Models.FirstOrDefault(candidate =>
            string.Equals(candidate.Slug, selectedModel, StringComparison.Ordinal));
        return candidate is not null && candidate.Enabled;
    }

    private static bool TryGetVerifiedTargetModel(
        ApiProviderProfile profile,
        TargetSwitchResult switchResult,
        out string model)
    {
        if (!TryGetEnabledSelectedModel(profile, out model))
        {
            return false;
        }

        var trace = switchResult.DiagnosticTrace;
        return trace is { ActiveModelListVerified: true } &&
            string.Equals(trace.EffectiveModelProvider, profile.StableCodexProviderId, StringComparison.Ordinal) &&
            string.Equals(trace.EffectiveModel, model, StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(trace.EffectiveModelCatalogJson);
    }

    private static void ApplyDiscoveredModelsToInventory(
        ApiProviderProfile profile,
        List<string>? models,
        DateTimeOffset observedAt)
    {
        if (models is null || models.Count == 0)
        {
            return;
        }

        profile.DiscoveredModels = models.Distinct(StringComparer.Ordinal).ToList();
        profile.ModelInventory ??= new ApiProviderModelInventory();
        profile.ModelInventory.MergeDiscoveredModels(profile.DiscoveredModels, observedAt);
        profile.ModelInventory.SelectedModel ??= profile.SelectedModel;
    }

    private static ApiProviderModelInventory CloneModelInventory(ApiProviderModelInventory? source)
    {
        if (source is null)
        {
            return new ApiProviderModelInventory();
        }

        return new ApiProviderModelInventory
        {
            SelectedModel = source.SelectedModel,
            DiscoveryStatus = source.DiscoveryStatus,
            LastDiscoveryAt = source.LastDiscoveryAt,
            Models = source.Models.Select(entry => new ApiProviderModelItem
            {
                Slug = entry.Slug,
                DisplayName = entry.DisplayName,
                Enabled = entry.Enabled,
                DiscoverySource = entry.DiscoverySource,
                Availability = entry.Availability,
                LastSeenAt = entry.LastSeenAt,
                ContextWindow = entry.ContextWindow,
                ContextEvidence = entry.ContextEvidence,
                Capabilities = entry.Capabilities,
                UserOverrides = entry.UserOverrides is null
                    ? null
                    : new CodexModelOverrides
                    {
                        ContextWindowTokens = entry.UserOverrides.ContextWindowTokens,
                        AutoCompactTokenLimit = entry.UserOverrides.AutoCompactTokenLimit,
                        AutoCompactTokenLimitScope = entry.UserOverrides.AutoCompactTokenLimitScope,
                        ReasoningEffort = entry.UserOverrides.ReasoningEffort,
                        ReasoningSummary = entry.UserOverrides.ReasoningSummary,
                        Verbosity = entry.UserOverrides.Verbosity,
                        ToolOutputTokenLimit = entry.UserOverrides.ToolOutputTokenLimit,
                    },
            }).ToList(),
        };
    }

    [RelayCommand]
    public void CloneProfile(ApiProviderItemViewModel? item)
    {
        if (item is null) return;

        var newId = Guid.NewGuid();
        var original = item.Profile;

        var clone = new ApiProviderProfile
        {
            Id = newId,
            Nickname = $"{original.Nickname} (Copy)",
            CatalogProviderId = original.CatalogProviderId,
            StableCodexProviderId = ApiProviderProfile.GenerateStableCodexProviderId(newId),
            BaseUrl = original.BaseUrl,
            SelectedRouteId = original.SelectedRouteId,
            SelectedModel = original.SelectedModel,
            WireApi = original.WireApi,
            KeyPreview = original.KeyPreview,
            Status = original.Status,
            CreatedAt = _clock.UtcNow,
            ModelOverrides = original.ModelOverrides,
            TransportOverrides = original.TransportOverrides,
            EndpointId = original.EndpointId ?? original.Id,
            ProviderPresetId = original.ProviderPresetId,
            RoutePoolLabel = original.RoutePoolLabel,
            DiscoveredModels = original.DiscoveredModels != null ? new List<string>(original.DiscoveredModels) : null,
            ModelInventory = CloneModelInventory(original.ModelInventory),
            CompatibilityLevel = original.CompatibilityLevel,
            LastProbeReport = original.LastProbeReport,
        };

        if (item.HasSecret)
        {
            _secretStore.CloneApiKey(original.Id, newId);
            clone.Status = ApiProviderProfileStatus.Active;
        }
        else
        {
            clone.Status = ApiProviderProfileStatus.CredentialMissing;
        }

        _apiProviderStore.Save(clone);
        TargetStateChanged?.Invoke(this, EventArgs.Empty);
        ShowInfo("Provider Cloned", $"Cloned '{original.Nickname}' to '{clone.Nickname}'. Key copied via secure DPAPI store.", InfoBarSeverity.Success);
    }

    [RelayCommand]
    public async Task CloneWithNewKeyAsync(ApiProviderItemViewModel? item)
    {
        if (item is null) return;

        var newKey = await _ui.PromptRotateApiKeyAsync($"Clone of {item.DisplayName}");
        if (string.IsNullOrWhiteSpace(newKey)) return;

        var newId = Guid.NewGuid();
        var newEndpointId = Guid.NewGuid();
        var original = item.Profile;

        var clone = new ApiProviderProfile
        {
            Id = newId,
            EndpointId = newEndpointId,
            Nickname = $"{original.Nickname} (New Key)",
            CatalogProviderId = original.CatalogProviderId,
            StableCodexProviderId = ApiProviderProfile.GenerateStableCodexProviderId(newId),
            BaseUrl = original.BaseUrl,
            SelectedRouteId = original.SelectedRouteId,
            SelectedModel = original.SelectedModel,
            WireApi = original.WireApi,
            KeyPreview = ApiProviderProfile.ComputeKeyPreview(newKey),
            Status = ApiProviderProfileStatus.Active,
            CreatedAt = _clock.UtcNow,
            ModelOverrides = original.ModelOverrides,
            TransportOverrides = original.TransportOverrides,
            ProviderPresetId = original.ProviderPresetId,
            RoutePoolLabel = original.RoutePoolLabel,
            DiscoveredModels = original.DiscoveredModels != null ? new List<string>(original.DiscoveredModels) : null,
            ModelInventory = CloneModelInventory(original.ModelInventory),
            CompatibilityLevel = original.CompatibilityLevel,
            LastProbeReport = original.LastProbeReport,
        };

        _secretStore.SaveApiKey(newEndpointId, newKey);
        _apiProviderStore.Save(clone);

        TargetStateChanged?.Invoke(this, EventArgs.Empty);
        ShowInfo("Provider Cloned", $"Created '{clone.Nickname}' with new API key.", InfoBarSeverity.Success);
    }

    [RelayCommand]
    public async Task AddAnotherModelAsync(ApiProviderItemViewModel? item)
    {
        if (item is null) return;

        var newId = Guid.NewGuid();
        var original = item.Profile;
        var endpointId = original.EndpointId ?? original.Id;

        var candidate = new ApiProviderProfile
        {
            Id = newId,
            Nickname = $"{original.Nickname} - New Model",
            CatalogProviderId = original.CatalogProviderId,
            StableCodexProviderId = ApiProviderProfile.GenerateStableCodexProviderId(newId),
            BaseUrl = original.BaseUrl,
            SelectedRouteId = original.SelectedRouteId,
            SelectedModel = original.SelectedModel,
            WireApi = original.WireApi,
            KeyPreview = original.KeyPreview,
            Status = item.HasSecret ? ApiProviderProfileStatus.Active : ApiProviderProfileStatus.CredentialMissing,
            CreatedAt = _clock.UtcNow,
            ModelOverrides = original.ModelOverrides,
            TransportOverrides = original.TransportOverrides,
            EndpointId = endpointId,
            ProviderPresetId = original.ProviderPresetId,
            RoutePoolLabel = original.RoutePoolLabel,
            DiscoveredModels = original.DiscoveredModels != null ? new List<string>(original.DiscoveredModels) : null,
            ModelInventory = CloneModelInventory(original.ModelInventory),
            CompatibilityLevel = original.CompatibilityLevel,
            LastProbeReport = original.LastProbeReport,
        };

        var result = await _ui.PromptEditApiProviderAsync(candidate, item.Descriptor);
        if (result is null)
        {
            return;
        }

        candidate.Nickname = result.Nickname;
        candidate.BaseUrl = result.BaseUrl;
        candidate.SelectedRouteId = result.SelectedRouteId;
        candidate.SelectedModel = result.SelectedModel;
        candidate.ModelOverrides = result.ModelOverrides;
        candidate.TransportOverrides = result.TransportOverrides;
        candidate.RoutePoolLabel = result.RoutePoolLabel;
        if (result.ModelInventory is not null) candidate.ModelInventory = result.ModelInventory;
        if (result.DiscoveredModels != null) candidate.DiscoveredModels = result.DiscoveredModels;
        if (result.CompatibilityLevel != CodexCompatibilityLevel.Unknown) candidate.CompatibilityLevel = result.CompatibilityLevel;
        if (result.LastProbeReport != null) candidate.LastProbeReport = result.LastProbeReport;

        _apiProviderStore.Save(candidate);
        TargetStateChanged?.Invoke(this, EventArgs.Empty);
        ShowInfo("Model Added", $"Added '{candidate.SelectedModel}' under '{candidate.Nickname}'.", InfoBarSeverity.Success);
    }

    [RelayCommand]
    public async Task RetestCompatibilityAsync(ApiProviderItemViewModel? item)
    {
        if (item is null) return;
        if (!item.HasSecret)
        {
            ShowInfo("Retest Error", "Cannot retest compatibility: API key is missing.", InfoBarSeverity.Warning);
            return;
        }

        await RunBusyAsync($"Testing compatibility for {item.DisplayName}...", async () =>
        {
            try
            {
                var secretOwnerId = item.Profile.EndpointId ?? item.Profile.Id;
                var key = _secretStore.GetApiKey(secretOwnerId);
                if (string.IsNullOrWhiteSpace(key))
                {
                    ShowInfo("Retest Error", "Could not retrieve API key for probe.", InfoBarSeverity.Warning);
                    return;
                }

                if (_probeService != null)
                {
                    var report = await _probeService.ProbeCompatibilityAsync(
                        item.Profile.BaseUrl,
                        key,
                        item.Profile.SelectedModel ?? "gpt-5.6-sol");

                    item.Profile.LastProbeReport = report;
                    item.Profile.CompatibilityLevel = report.CompatibilityLevel;
                    if (report.DiscoveredModelIds != null && report.DiscoveredModelIds.Count > 0)
                    {
                        item.Profile.DiscoveredModels = report.DiscoveredModelIds;
                    }

                    _apiProviderStore.Save(item.Profile);
                    item.UpdateProfile(item.Profile, item.Descriptor, item.HasSecret, item.IsTargetActive);

                    var severity = report.CompatibilityLevel switch
                    {
                        CodexCompatibilityLevel.CodexCompatible => InfoBarSeverity.Success,
                        CodexCompatibilityLevel.PartiallyCompatible => InfoBarSeverity.Warning,
                        _ => InfoBarSeverity.Error
                    };
                    ShowInfo("Compatibility Result", $"{item.DisplayName}: {report.CompatibilityLevel}. {report.DiagnosticSummary}", severity);
                }
            }
            catch (Exception ex)
            {
                ShowInfo("Compatibility Test Failed", ex.Message, InfoBarSeverity.Error);
            }
        });
    }

    [RelayCommand]
    public void ExportDiagnostics(ApiProviderItemViewModel? item)
    {
        if (item is null) return;

        if (item.Profile.LastProbeReport is null)
        {
            ShowInfo("Diagnostic Export", "No compatibility report available. Please run a compatibility test first.", InfoBarSeverity.Informational);
            return;
        }

        var json = item.Profile.LastProbeReport.GenerateSanitizedExport(item.DisplayName, item.Profile.RoutePoolLabel);
        var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
        dp.SetText(json);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);

        ShowInfo("Export Complete", "Sanitized diagnostic report copied to clipboard. Raw secrets excluded.", InfoBarSeverity.Success);
    }
}

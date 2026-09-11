using System.Collections.ObjectModel;
using CodexSwitcher.App.Localization;
using CodexSwitcher.App.Services;
using CodexSwitcher.Core.Abstractions;
using CodexSwitcher.Core.Catalog;
using CodexSwitcher.Core.Models;
using CodexSwitcher.Core.Services;
using CodexSwitcher.Infra;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CodexSwitcher.App.ViewModels;

/// <summary>ViewModel principal: lista de contas, switch, importação/exportação, adicionar/adotar, renomear/remover.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly ProfileService _profiles;
    private readonly SwitchService _switch;
    private readonly SettingsStore _settingsStore;
    private readonly AppSettings _settings;
    private readonly IClock _clock;
    private readonly IUiInteraction _ui;
    private readonly IUsageService _usageService;
    private readonly UsagePollingCoordinator _pollingCoordinator;
    private readonly Strings _loc = Strings.Current;

    private readonly List<AccountItemViewModel> _all = [];
    private readonly Dictionary<Guid, AccountUsageViewModel> _usages = [];
    private DispatcherTimer? _countdownTimer;
    private DispatcherTimer? _pollingTimer;
    private readonly IUiDispatcher _dispatcher;
    private readonly IAppLifetime _appLifetime;
    private readonly ITotpCredentialStore? _totpStore;
    private readonly ITotpRevealAuthorizationService? _authService;
    private DispatcherTimer? _totpPresentationTimer;
    private bool _hasShownDegradedNoticeThisSession;
    private CancellationTokenSource? _startupRefreshCts;

    // Phase 10C services
    private readonly ICodexActiveTargetResolver? _activeTargetResolver;
    private readonly ICodexTargetSwitchService? _targetSwitchService;
    private readonly IApiProviderStore? _apiProviderStore;
    private readonly IApiKeySecretStore? _secretStore;
    private readonly IProviderCatalogService? _catalogService;
    private readonly IDeclarativeProviderInspector? _providerInspector;
    private readonly ICodexThreadHandoffService? _threadHandoffService;
    private readonly IProviderModelCache? _modelCache;
    private readonly AppPaths? _paths;

    public ObservableCollection<AccountItemViewModel> Accounts { get; } = [];
    public ObservableCollection<ApiProviderItemViewModel> ApiProviders { get; } = [];

    [ObservableProperty] public partial string SearchText { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string? BusyText { get; set; }
    [ObservableProperty] public partial bool IsRefreshingUsage { get; set; }
    [ObservableProperty] public partial bool InfoOpen { get; set; }
    [ObservableProperty] public partial string InfoMessage { get; set; }
    [ObservableProperty] public partial string InfoTitle { get; set; }
    [ObservableProperty] public partial InfoBarSeverity InfoSeverity { get; set; }
    [ObservableProperty] public partial bool ShowEmptyState { get; set; }
    [ObservableProperty] public partial bool ShowAdoptPrompt { get; set; }
    [ObservableProperty] public partial bool HasDetectedActiveAccount { get; set; }
    [ObservableProperty] public partial string DetectedActiveAccountDetails { get; set; }

    // Phase 10C properties
    [ObservableProperty] public partial int SelectedTab { get; set; }
    [ObservableProperty] public partial string ActiveTargetSummary { get; set; }
    [ObservableProperty] public partial string ActiveTargetCredentialSlot { get; set; }
    [ObservableProperty] public partial bool IsRoutingActiveToApi { get; set; }
    [ObservableProperty] public partial bool ShowApiProvidersEmptyState { get; set; }

    public bool ShowNormalEmptyState => ShowEmptyState && !HasDetectedActiveAccount;
    public bool ShowDetectedAccountEmptyState => ShowEmptyState && HasDetectedActiveAccount;

    public MainViewModel(
        ProfileService profiles, SwitchService switchService,
        SettingsStore settingsStore, AppSettings settings, IClock clock, IUiInteraction ui,
        IUsageService usageService, UsagePollingCoordinator pollingCoordinator,
        IUiDispatcher? dispatcher = null, IAppLifetime? appLifetime = null,
        ITotpCredentialStore? totpStore = null,
        ITotpRevealAuthorizationService? authService = null,
        ICodexActiveTargetResolver? activeTargetResolver = null,
        ICodexTargetSwitchService? targetSwitchService = null,
        IApiProviderStore? apiProviderStore = null,
        IApiKeySecretStore? secretStore = null,
        IProviderCatalogService? catalogService = null,
        IDeclarativeProviderInspector? providerInspector = null,
        ICodexThreadHandoffService? threadHandoffService = null,
        AppPaths? paths = null,
        IProviderModelCache? modelCache = null)
    {
        _profiles = profiles;
        _switch = switchService;
        _settingsStore = settingsStore;
        _settings = settings;
        _clock = clock;
        _ui = ui;
        _usageService = usageService;
        _pollingCoordinator = pollingCoordinator;
        _dispatcher = dispatcher ?? ImmediateUiDispatcher.Instance;
        _appLifetime = appLifetime ?? new AppLifetime();
        _totpStore = totpStore;
        _authService = authService;
        _activeTargetResolver = activeTargetResolver;
        _targetSwitchService = targetSwitchService;
        _apiProviderStore = apiProviderStore;
        _secretStore = secretStore;
        _catalogService = catalogService;
        _providerInspector = providerInspector;
        _threadHandoffService = threadHandoffService;
        _paths = paths;
        _modelCache = modelCache;

        _pollingCoordinator.UsageUpdated += OnUsageUpdated;
        _pollingCoordinator.RefreshingStateChanged += OnRefreshingStateChanged;

        SearchText = string.Empty;
        InfoMessage = string.Empty;
        InfoTitle = string.Empty;
        InfoSeverity = InfoBarSeverity.Informational;
        DetectedActiveAccountDetails = string.Empty;
        ActiveTargetSummary = string.Empty;
        ActiveTargetCredentialSlot = string.Empty;
    }

    public int AccountCount => _all.Count;

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    [RelayCommand]
    private async Task LoadAsync()
    {
        await RunBusy(_loc.BusyLoading, () =>
        {
            _profiles.Load();
            return Task.CompletedTask;
        });

        // Cache-first startup: restore cached usage immediately without network delay
        await _usageService.LoadCacheAsync();
        var cachedMap = _usageService.GetAllCached();
        var now = _clock.UtcNow;
        foreach (var (profileId, cacheEntry) in cachedMap)
        {
            var state = UsagePresentationMapper.MapFromCache(cacheEntry, profileId, now, _loc.Pt);
            var vm = GetOrCreateUsageVm(profileId);
            vm.ApplyState(state, now);
        }

        RebuildList();
        StartTimers();

        // Background initial refresh across accounts
        if (_profiles.Profiles.Count > 0)
        {
            _ = TriggerBackgroundRefreshForProfiles(_profiles.Profiles);
        }
    }

    private void StartTimers()
    {
        if (_countdownTimer is null)
        {
            _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _countdownTimer.Tick += (_, _) =>
            {
                var now = _clock.UtcNow;
                foreach (var u in _usages.Values)
                    u.UpdateCountdowns(now);
            };
            _countdownTimer.Start();
        }

        if (_pollingTimer is null)
        {
            _pollingTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
            _pollingTimer.Tick += async (_, _) =>
            {
                await _pollingCoordinator.OnTimerTickAsync(_profiles.Profiles);
            };
            _pollingTimer.Start();
        }
    }

    public void SetForegroundActive(bool active)
    {
        _ = _pollingCoordinator.SetForegroundActiveAsync(active, _profiles.Profiles);
    }

    private void OnUsageUpdated(object? sender, AccountUsageUpdatedEventArgs e)
    {
        _dispatcher.Enqueue(() =>
        {
            var vm = GetOrCreateUsageVm(e.ProfileId);
            vm.ApplyState(e.State, _clock.UtcNow);
        });
    }

    private void OnRefreshingStateChanged(object? sender, bool isRefreshing)
    {
        _dispatcher.Enqueue(() =>
        {
            IsRefreshingUsage = isRefreshing;
        });
    }

    private AccountUsageViewModel GetOrCreateUsageVm(Guid profileId)
    {
        if (!_usages.TryGetValue(profileId, out var vm))
        {
            vm = new AccountUsageViewModel(profileId);
            _usages[profileId] = vm;
        }
        return vm;
    }

    public Task TriggerBackgroundRefreshForProfiles(IReadOnlyList<ProfileMetadata> profiles)
    {
        if (profiles.Count == 0)
            return Task.CompletedTask;

        _startupRefreshCts?.Cancel();
        _startupRefreshCts?.Dispose();
        _startupRefreshCts = CancellationTokenSource.CreateLinkedTokenSource(_appLifetime.ApplicationStopping);

        var token = _startupRefreshCts.Token;
        return Task.Run(async () =>
        {
            try
            {
                await _pollingCoordinator.TriggerRefreshAllAsync(profiles, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _dispatcher.Enqueue(() =>
                {
                    ShowInfo(_loc.ErrorTitle, ex.Message, InfoBarSeverity.Warning);
                });
            }
        }, token);
    }

    [RelayCommand]
    private async Task RefreshAllAsync()
    {
        if (_all.Count == 0) return;
        try
        {
            await _pollingCoordinator.TriggerRefreshAllAsync(_profiles.Profiles);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ShowInfo(_loc.ErrorTitle, ex.Message, InfoBarSeverity.Warning);
        }
    }

    [RelayCommand]
    private async Task RefreshAccountAsync(AccountItemViewModel? item)
    {
        if (item is null) return;
        try
        {
            await _pollingCoordinator.TriggerRefreshAccountAsync(item.Profile);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ShowInfo(_loc.ErrorTitle, ex.Message, InfoBarSeverity.Warning);
        }
    }

    [RelayCommand]
    private void SelectChatGptTab() => SelectedTab = 0;

    [RelayCommand]
    private void SelectApiProvidersTab() => SelectedTab = 1;

    [RelayCommand]
    private async Task SwitchAsync(AccountItemViewModel? item)
    {
        if (item is null || !item.CanSwitch) return;

        _profiles.Reconcile();
        var from = _profiles.Profiles.FirstOrDefault(p => p.IsActive);
        var plan = _switch.BuildPlan(from, item.Profile, _settings.CloseReopenMode);

        if (_settings.AlwaysConfirmSwitch && !await _ui.ConfirmSwitchAsync(plan))
            return;

        SwitchResult? result = null;
        await RunBusy(_loc.BusySwitching(item.DisplayName), async () =>
        {
            if (_targetSwitchService is not null)
            {
                var targetRes = await _targetSwitchService.SwitchToChatGptAsync(
                    item.Id,
                    _profiles.Profiles,
                    SwitchExecutionOptions.From(_settings));
                var outcome = targetRes.Outcome switch
                {
                    TargetSwitchOutcome.Success => SwitchOutcome.Success,
                    TargetSwitchOutcome.SuccessWithReopenWarning => SwitchOutcome.SuccessWithReopenWarning,
                    TargetSwitchOutcome.RolledBack => SwitchOutcome.RolledBack,
                    TargetSwitchOutcome.AbortedProcessRemnant => SwitchOutcome.AbortedProcessRemnant,
                    _ => SwitchOutcome.Failed,
                };
                result = new SwitchResult(outcome, targetRes.Message, targetRes.Error, targetRes.ClosedProcesses, targetRes.ReopenFailures);
            }
            else
            {
                result = await Task.Run(() => _switch.SwitchAsync(
                    _profiles.Profiles, item.Id, SwitchExecutionOptions.From(_settings)));
            }
        });

        // A troca realmente saiu da conta anterior: marca-a como usada automaticamente.
        if (from is not null && result?.Outcome is SwitchOutcome.Success or SwitchOutcome.SuccessWithReopenWarning)
            _profiles.MarkUsed(from.Id);

        RebuildList();
        if (result is not null)
            ShowSwitchResult(result, item.DisplayName);
    }

    [RelayCommand]
    private async Task AddApiProviderAsync()
    {
        var descriptors = _catalogService?.CurrentResult.Catalog.Providers ?? (IReadOnlyList<ProviderDescriptor>)Array.Empty<ProviderDescriptor>();
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

        if (_secretStore is not null && !string.IsNullOrWhiteSpace(result.ApiKey))
        {
            _secretStore.SaveApiKey(profile.Id, result.ApiKey);
        }

        _apiProviderStore?.Save(profile);

        if (result.SaveAndSwitch && _targetSwitchService is not null)
        {
            await SwitchToApiProviderInternalAsync(profile);
        }

        RebuildList();
        ShowInfo(_loc.ProviderDialogTitle, $"API Provider '{profile.Nickname}' saved.", InfoBarSeverity.Success);
    }

    [RelayCommand]
    private async Task SwitchToApiProviderAsync(ApiProviderItemViewModel? item)
    {
        if (item is null || !item.CanSwitch) return;

        if (!item.HasSecret)
        {
            ShowInfo(_loc.ErrorTitle, _loc.ApiKeyRequired, InfoBarSeverity.Warning);
            return;
        }

        if (_settings.AlwaysConfirmSwitch && !await _ui.ConfirmSwitchToApiAsync(item.DisplayName, item.SelectedModel, item.SelectedRoute))
            return;

        await SwitchToApiProviderInternalAsync(item.Profile);
        RebuildList();
    }

    private async Task SwitchToApiProviderInternalAsync(ApiProviderProfile profile)
    {
        if (_targetSwitchService is null) return;

        await RunBusy(_loc.BusySwitching(profile.Nickname), async () =>
        {
            var res = await _targetSwitchService.SwitchToApiProviderAsync(
                profile.Id,
                SwitchExecutionOptions.From(_settings));

            if (res.Outcome is TargetSwitchOutcome.Success or TargetSwitchOutcome.SuccessWithReopenWarning)
            {
                ShowInfo(_loc.SwitchedTitle, _loc.SwitchedMsg(profile.Nickname), InfoBarSeverity.Success);
            }
            else
            {
                ShowInfo(_loc.SwitchFailedTitle, _loc.SwitchFailedMsg, InfoBarSeverity.Error);
            }
        });
    }

    [RelayCommand]
    private async Task ToggleRouteAsync(ApiProviderItemViewModel? item)
    {
        if (item is null || item.Routes.Count <= 1 || item.Descriptor is null) return;

        var currentRouteId = item.Profile.SelectedRouteId;
        var routes = item.Descriptor.Routes;
        var currentIndex = routes.FindIndex(r => r.Id.Equals(currentRouteId, StringComparison.OrdinalIgnoreCase));
        var nextIndex = currentIndex >= 0 ? (currentIndex + 1) % routes.Count : 0;
        var nextRoute = routes[nextIndex];

        item.Profile.SelectedRouteId = nextRoute.Id;
        item.Profile.BaseUrl = nextRoute.BaseUrl;

        _apiProviderStore?.Save(item.Profile);

        if (item.IsTargetActive && _targetSwitchService is not null)
        {
            await _targetSwitchService.SwitchApiRouteAsync(
                item.Profile.Id,
                nextRoute.Id,
                nextRoute.BaseUrl,
                SwitchExecutionOptions.From(_settings));
        }

        RebuildList();
    }

    [RelayCommand]
    private async Task SwitchRouteAsync((ApiProviderItemViewModel? Item, string RouteId) args)
    {
        var (item, routeId) = args;
        if (item is null || item.Descriptor is null || string.IsNullOrWhiteSpace(routeId)) return;

        var route = item.Descriptor.Routes.FirstOrDefault(r => r.Id.Equals(routeId, StringComparison.OrdinalIgnoreCase));
        if (route is null || route.Id.Equals(item.Profile.SelectedRouteId, StringComparison.OrdinalIgnoreCase)) return;

        item.Profile.SelectedRouteId = route.Id;
        item.Profile.BaseUrl = route.BaseUrl;

        _apiProviderStore?.Save(item.Profile);

        if (item.IsTargetActive && _targetSwitchService is not null)
        {
            await _targetSwitchService.SwitchApiRouteAsync(
                item.Profile.Id,
                route.Id,
                route.BaseUrl,
                SwitchExecutionOptions.From(_settings));
        }

        RebuildList();
    }

    [RelayCommand]
    private async Task EditApiProviderAsync(ApiProviderItemViewModel? item)
    {
        if (item is null) return;

        var result = await _ui.PromptEditApiProviderAsync(item.Profile, item.Descriptor);
        if (result is null) return;

        item.Profile.Nickname = result.Nickname;
        item.Profile.BaseUrl = result.BaseUrl;
        item.Profile.SelectedRouteId = result.SelectedRouteId;
        item.Profile.SelectedModel = result.SelectedModel;

        _apiProviderStore?.Save(item.Profile);

        if (item.IsTargetActive && _targetSwitchService is not null)
        {
            await _targetSwitchService.SwitchToApiProviderAsync(
                item.Profile.Id,
                SwitchExecutionOptions.From(_settings));
        }

        RebuildList();
        ShowInfo(_loc.EditProviderDialogTitle, "Provider updated.", InfoBarSeverity.Success);
    }

    [RelayCommand]
    private async Task RotateApiKeyAsync(ApiProviderItemViewModel? item)
    {
        if (item is null || _secretStore is null) return;

        var newKey = await _ui.PromptRotateApiKeyAsync(item.DisplayName);
        if (string.IsNullOrWhiteSpace(newKey)) return;

        _secretStore.SaveApiKey(item.Profile.Id, newKey);
        item.Profile.Status = ApiProviderProfileStatus.Active;
        item.Profile.KeyPreview = ApiProviderProfile.ComputeKeyPreview(newKey);
        _apiProviderStore?.Save(item.Profile);

        RebuildList();
        ShowInfo(_loc.RotateKeyDialogTitle, "API key updated successfully.", InfoBarSeverity.Success);
    }

    [RelayCommand]
    private async Task RemoveCredentialAsync(ApiProviderItemViewModel? item)
    {
        if (item is null || _secretStore is null) return;

        bool ok = await _ui.ConfirmAsync(
            _loc.RemoveCredentialConfirmTitle(item.DisplayName),
            _loc.RemoveCredentialConfirmMessage,
            _loc.RemoveCredential,
            destructive: true);

        if (!ok) return;

        _secretStore.DeleteApiKey(item.Profile.Id);
        item.Profile.Status = ApiProviderProfileStatus.CredentialMissing;
        item.Profile.KeyPreview = string.Empty;
        _apiProviderStore?.Save(item.Profile);

        if (item.IsTargetActive && _targetSwitchService is not null)
        {
            await _targetSwitchService.SwitchToChatGptAsync(null, _profiles.Profiles, SwitchExecutionOptions.From(_settings));
        }

        RebuildList();
        ShowInfo(_loc.RemoveCredential, "API key credential removed.", InfoBarSeverity.Informational);
    }

    [RelayCommand]
    private async Task RemoveApiProviderAsync(ApiProviderItemViewModel? item)
    {
        if (item is null) return;

        bool ok = await _ui.ConfirmAsync(
            _loc.RemoveTitle,
            _loc.RemoveConfirm(item.DisplayName),
            _loc.Remove,
            destructive: true);

        if (!ok) return;

        _secretStore?.DeleteApiKey(item.Profile.Id);
        _apiProviderStore?.Delete(item.Profile.Id);

        if (item.IsTargetActive && _targetSwitchService is not null)
        {
            await _targetSwitchService.SwitchToChatGptAsync(null, _profiles.Profiles, SwitchExecutionOptions.From(_settings));
        }

        RebuildList();
        ShowInfo(_loc.RemovedTitle, _loc.RemovedMsg(item.DisplayName), InfoBarSeverity.Informational);
    }

    [RelayCommand]
    private async Task ContinueOnAsync(ApiProviderItemViewModel? item)
    {
        if (item is null || _threadHandoffService is null) return;

        if (!item.HasSecret)
        {
            ShowInfo(_loc.ErrorTitle, _loc.ApiKeyRequired, InfoBarSeverity.Warning);
            return;
        }

        IReadOnlyList<CodexThreadSummary> threads;
        try
        {
            threads = await _threadHandoffService.ListThreadsAsync();
        }
        catch (Exception ex)
        {
            ShowInfo(_loc.ErrorTitle, $"Failed to retrieve threads: {ex.Message}", InfoBarSeverity.Error);
            return;
        }

        if (threads.Count == 0)
        {
            ShowInfo(_loc.ThreadPickerTitle, "No recent conversations found in Codex to continue.", InfoBarSeverity.Informational);
            return;
        }

        var targetModel = !string.IsNullOrWhiteSpace(item.SelectedModel)
            ? item.SelectedModel
            : _modelCache?.GetLatestModels(item.Profile.Id)?.FirstOrDefault()
              ?? item.Descriptor?.Codex.DefaultModel
              ?? "gpt-5.6-sol";

        var selected = await _ui.PromptContinueOnThreadAsync(threads, item.DisplayName, targetModel);
        if (selected is null) return;

        await RunBusy(_loc.ContinueOn, async () =>
        {
            var forkResult = await _threadHandoffService.ForkThreadAsync(
                selected.Id,
                item.Profile.StableCodexProviderId,
                targetModel);

            if (!item.IsTargetActive && _targetSwitchService is not null)
            {
                await _targetSwitchService.SwitchToApiProviderAsync(item.Profile.Id, SwitchExecutionOptions.From(_settings));
            }

            ShowInfo(_loc.ForkSuccessTitle, _loc.ForkSuccessMessage(item.DisplayName, selected.Name ?? selected.Id), InfoBarSeverity.Success);
        });

        RebuildList();
    }

    [RelayCommand]
    private async Task DiscoverModelsAsync(ApiProviderItemViewModel? item)
    {
        if (item is null || _providerInspector is null) return;

        item.IsLoadingModels = true;
        item.ModelsStatusMessage = _loc.DiscoveringModels;

        string? secret = _secretStore?.GetApiKey(item.Profile.Id);
        var desc = item.Descriptor ?? _catalogService?.GetDescriptor(item.Profile.CatalogProviderId);
        var routeKey = !string.IsNullOrWhiteSpace(item.Profile.SelectedRouteId) ? item.Profile.SelectedRouteId : item.Profile.BaseUrl;

        try
        {
            ApiProviderSnapshot snapshot;
            if (desc is not null)
            {
                snapshot = await _providerInspector.InspectAsync(desc, item.Profile.BaseUrl, secret, CancellationToken.None);
            }
            else
            {
                snapshot = await _providerInspector.InspectGenericUnknownAsync(item.Profile.BaseUrl, secret, CancellationToken.None);
            }

            if (snapshot.Models.Count > 0)
            {
                item.SetDiscoveredModels(snapshot.Models);
                _modelCache?.SetModels(item.Profile.Id, routeKey, 1, snapshot.Models);
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
        catch (Exception ex)
        {
            item.ModelsStatusMessage = ex.Message;
        }
        finally
        {
            item.IsLoadingModels = false;
        }
    }

    [RelayCommand]
    private async Task AddAccountAsync()
    {
        byte[]? authJson = null;
        await RunBusy(_loc.BusyWaitingLogin, async () =>
        {
            authJson = await _ui.RunEphemeralLoginAsync();
        });

        if (authJson is null)
        {
            ShowInfo(_loc.LoginCanceledTitle, _loc.LoginCanceledMsg, InfoBarSeverity.Informational);
            return;
        }

        var profile = _profiles.AddFromAuthJson(authJson, null);
        var nick = await _ui.PromptTextAsync(_loc.NicknameTitle, _loc.NicknamePrompt,
            profile.DisplayName, _loc.Save);
        if (!string.IsNullOrWhiteSpace(nick))
            _profiles.Rename(profile.Id, nick);

        RebuildList();
        ShowInfo(_loc.AddedTitle, _loc.AddedMsg(profile.DisplayName), InfoBarSeverity.Success);
        _ = TriggerBackgroundRefreshForProfiles([profile]);
    }

    [RelayCommand]
    private async Task AdoptActiveAsync()
    {
        ProfileMetadata? adopted = null;
        await RunBusy(_loc.BusyImporting, () =>
        {
            adopted = _profiles.AdoptActiveAccount();
            return Task.CompletedTask;
        });
        RebuildList();
        if (adopted is not null)
        {
            ShowInfo(_loc.ImportedTitle, _loc.ImportedMsg(adopted.DisplayName), InfoBarSeverity.Success);
            _ = TriggerBackgroundRefreshForProfiles([adopted]);
        }
    }

    /// <summary>
    /// Relê o slot ativo do Codex sob demanda e importa a conta logada se ela ainda não estiver no
    /// cofre. Necessário porque o login pode acontecer fora do app (ex.: <c>codex login</c>) depois
    /// que a lista já foi carregada, e nesse caso nada dispara a detecção automática.
    /// </summary>
    [RelayCommand]
    private async Task DetectAccountAsync()
    {
        ReconciliationResult? result = null;
        ProfileMetadata? adopted = null;
        await RunBusy(_loc.BusyDetecting, () =>
        {
            result = _profiles.Load();
            if (result is { Match: ActiveMatch.None, ActiveFingerprint: not null })
                adopted = _profiles.AdoptActiveAccount();
            return Task.CompletedTask;
        });
        RebuildList();

        if (result is null) return; // RunBusy já mostrou o erro.

        if (adopted is not null)
        {
            ShowInfo(_loc.ImportedTitle, _loc.ImportedMsg(adopted.DisplayName), InfoBarSeverity.Success);
            _ = TriggerBackgroundRefreshForProfiles([adopted]);
        }
        else if (result.ActiveFingerprint is null)
            ShowInfo(_loc.DetectNoneTitle, _loc.DetectNoneMsg, InfoBarSeverity.Informational);
        else
        {
            var active = _profiles.Profiles.FirstOrDefault(p => p.IsActive);
            ShowInfo(_loc.DetectKnownTitle,
                _loc.DetectKnownMsg(active?.DisplayName ?? _loc.CodexAccount), InfoBarSeverity.Informational);
        }
    }

    [RelayCommand]
    private async Task ImportAccountsAsync()
    {
        var document = await _ui.PickImportFileAsync();
        if (document is null) return;

        var prevProfiles = _profiles.Profiles.ToList();
        var count = 0;
        await RunBusy(_loc.BusyImporting, () =>
        {
            count = _profiles.Import(document);
            return Task.CompletedTask;
        });
        RebuildList();
        if (count > 0)
        {
            ShowInfo(_loc.ImportedTitle, _loc.ImportedAccountsMsg(count), InfoBarSeverity.Success);
            var newProfiles = _profiles.Profiles.Where(p => !prevProfiles.Any(prev => prev.Id == p.Id)).ToList();
            if (newProfiles.Count > 0)
            {
                _ = TriggerBackgroundRefreshForProfiles(newProfiles);
            }
        }
    }

    [RelayCommand]
    private async Task ExportAllAsync()
    {
        if (_all.Count == 0) return;
        if (!await _ui.ConfirmAsync(_loc.ExportTitle, _loc.ExportWarning, _loc.Export, destructive: false)) return;

        var saved = false;
        await RunBusy(_loc.BusyExporting, async () =>
        {
            saved = await _ui.SaveExportFileAsync("codex-switcher-accounts.codexswitcher", _profiles.ExportAll());
        });
        if (saved) ShowInfo(_loc.ExportedTitle, _loc.ExportedAllMsg(_all.Count), InfoBarSeverity.Success);
    }

    [RelayCommand]
    private async Task ExportOneAsync(AccountItemViewModel? item)
    {
        if (item is null) return;
        if (!await _ui.ConfirmAsync(_loc.ExportTitle, _loc.ExportOneWarning(item.DisplayName), _loc.Export, destructive: false)) return;

        var saved = false;
        await RunBusy(_loc.BusyExporting, async () =>
        {
            saved = await _ui.SaveExportFileAsync("codex-switcher-account.codexswitcher", _profiles.ExportOne(item.Id));
        });
        if (saved) ShowInfo(_loc.ExportedTitle, _loc.ExportedOneMsg(item.DisplayName), InfoBarSeverity.Success);
    }

    [RelayCommand]
    private async Task RenameAsync(AccountItemViewModel? item)
    {
        if (item is null) return;
        var nick = await _ui.PromptTextAsync(_loc.RenameTitle, _loc.RenamePrompt, item.DisplayName, _loc.Save);
        if (nick is null) return;
        _profiles.Rename(item.Id, nick);
        RebuildList();
    }

    [RelayCommand]
    private async Task EditSubscriptionTrackingAsync(AccountItemViewModel? item)
    {
        if (item is null) return;
        var updated = await _ui.PromptSubscriptionTrackingAsync(item.DisplayName, item.Profile.SubscriptionTracking, item.Profile.DetectedSubscription);
        if (ReferenceEquals(updated, item.Profile.SubscriptionTracking))
            return;

        _profiles.UpdateSubscriptionTracking(item.Id, updated);
        RebuildList();
    }

    [RelayCommand]
    private async Task RemoveAsync(AccountItemViewModel? item)
    {
        if (item is null) return;
        var ok = await _ui.ConfirmAsync(_loc.RemoveTitle, _loc.RemoveConfirm(item.DisplayName),
            _loc.Remove, destructive: true);
        if (!ok) return;
        _pollingCoordinator.Invalidate(item.Id);
        _usages.Remove(item.Id);
        _profiles.Remove(item.Id);
        if (_settings.CollapsedProfileIds.Remove(item.Id))
        {
            _settingsStore.Save(_settings);
        }
        RebuildList();
        ShowInfo(_loc.RemovedTitle, _loc.RemovedMsg(item.DisplayName), InfoBarSeverity.Informational);
    }

    [RelayCommand]
    private void ToggleAccountCollapse(AccountItemViewModel? item)
    {
        if (item is null) return;
        item.ResetTotpPresentation();
        EnsureTotpPresentationTimer();
        item.IsCompact = !item.IsCompact;
        if (item.IsCompact)
            _settings.CollapsedProfileIds.Add(item.Id);
        else
            _settings.CollapsedProfileIds.Remove(item.Id);

        _settingsStore.Save(_settings);
    }

    [RelayCommand]
    private void MarkNeedsReLogin(AccountItemViewModel? item)
    {
        if (item is null) return;
        _profiles.MarkNeedsReLogin(item.Id);
        RebuildList();
    }

    [RelayCommand]
    private void MarkUsed(AccountItemViewModel? item)
    {
        if (item is null) return;
        _profiles.MarkUsed(item.Id);
        RebuildList();
    }

    [RelayCommand]
    private void UnmarkUsed(AccountItemViewModel? item)
    {
        if (item is null) return;
        _profiles.UnmarkUsed(item.Id);
        RebuildList();
    }

    /// <summary>Persiste a nova ordem (drag-and-drop). Ignorado durante uma busca, onde a ordem visível é ambígua.</summary>
    [RelayCommand]
    private void Reorder(IReadOnlyList<Guid>? orderedIds)
    {
        if (orderedIds is null || orderedIds.Count == 0) return;
        if (!string.IsNullOrEmpty(SearchText)) return;
        _profiles.Reorder(orderedIds);
        RebuildList();
    }

    // Phase 9 & 9.1: Comandos e ciclo de vida do TOTP 2FA por perfil com verificação do Windows
    [RelayCommand]
    private async Task RevealTotpAsync(AccountItemViewModel? item)
    {
        if (item is null || !item.HasTotpConfigured || _totpStore is null) return;

        if (item.IsTotpRevealed)
        {
            item.ResetTotpPresentation();
            EnsureTotpPresentationTimer();
            return;
        }

        // Single visible code policy: hide any other currently revealed profile
        foreach (var other in _all)
        {
            if (other != item && other.IsTotpRevealed)
                other.ResetTotpPresentation();
        }

        bool isDegradedReveal = false;
        // Hard security ordering: verification must succeed or fallback must be authorized before secret decryption
        if (_authService is not null && _authService.IsVerificationRequired())
        {
            var outcome = await _authService.EnsureAuthorizedAsync(_loc.WindowsVerificationPromptMessage);

            if (outcome.IsDegradedFallback)
            {
                isDegradedReveal = true;
                if (!_hasShownDegradedNoticeThisSession)
                {
                    _hasShownDegradedNoticeThisSession = true;
                    ShowInfo(_loc.WarningTitle, _loc.WindowsVerificationDegradedNotice, InfoBarSeverity.Informational);
                }
            }
            else if (outcome.IsTemporarilyUnavailable)
            {
                var choice = await _ui.PromptTransientVerificationFallbackAsync(_loc.WindowsVerificationTransientMessage);
                if (choice == TransientVerificationChoice.TryAgain)
                {
                    await RevealTotpAsync(item);
                    return;
                }
                else if (choice == TransientVerificationChoice.ShowCodeOnce)
                {
                    isDegradedReveal = true;
                }
                else
                {
                    return;
                }
            }
            else if (!outcome.Success)
            {
                if (outcome.Action == TotpAuthorizationAction.Canceled || outcome.Status == WindowsVerificationResult.Canceled || outcome.PasswordStatus == WindowsPasswordVerificationResult.Canceled)
                {
                    ShowInfo(_loc.WarningTitle, _loc.WindowsVerificationCanceled, InfoBarSeverity.Informational);
                }
                else if (outcome.PasswordStatus == WindowsPasswordVerificationResult.InvalidCredentials)
                {
                    ShowInfo(_loc.WarningTitle, _loc.WindowsPasswordIncorrect, InfoBarSeverity.Warning);
                }
                else if (outcome.PasswordStatus == WindowsPasswordVerificationResult.DifferentUser)
                {
                    ShowInfo(_loc.WarningTitle, _loc.WindowsDifferentUser, InfoBarSeverity.Warning);
                }
                else if (outcome.PasswordStatus == WindowsPasswordVerificationResult.AccountLocked)
                {
                    ShowInfo(_loc.WarningTitle, _loc.WindowsAccountLocked, InfoBarSeverity.Warning);
                }
                else if (outcome.PasswordStatus == WindowsPasswordVerificationResult.PasswordExpired)
                {
                    ShowInfo(_loc.WarningTitle, _loc.WindowsPasswordExpired, InfoBarSeverity.Warning);
                }
                else if (outcome.PasswordStatus == WindowsPasswordVerificationResult.AccountRestricted)
                {
                    ShowInfo(_loc.WarningTitle, _loc.WindowsAccountRestricted, InfoBarSeverity.Warning);
                }
                else
                {
                    ShowInfo(_loc.WarningTitle, _loc.WindowsVerificationFailed, InfoBarSeverity.Warning);
                }
                return;
            }
        }

        // Post-verification safety re-checks
        if (_appLifetime.IsStopping) return;
        if (!_profiles.Profiles.Any(p => p.Id == item.Id)) return;
        if (!_totpStore.HasCredential(item.Id))
        {
            item.HasTotpConfigured = false;
            return;
        }

        var now = _clock.UtcNow;
        if (_totpStore.TryComputeCode(item.Id, now, out var code, out var error))
        {
            item.IsTotpRevealed = true;
            item.IsDegradedReveal = isDegradedReveal;
            item.TotpCodeText = code.Formatted;
            item.TotpSecondsRemaining = code.SecondsRemaining;
            item.TotpCountdownText = $"{code.SecondsRemaining}s";

            // Two-timer invariant: min(10s, authSessionRemaining)
            double maxSeconds = 10.0;
            if (!isDegradedReveal && _authService is not null && _authService.IsProtectionEnabled && _authService.IsAuthorized)
            {
                var authRemaining = _authService.RemainingDuration.TotalSeconds;
                if (authRemaining < maxSeconds)
                    maxSeconds = Math.Max(0, authRemaining);
            }

            item.RevealDeadline = now.AddSeconds(maxSeconds);
            item.IsTotpCopied = false;
            EnsureTotpPresentationTimer();
        }
        else
        {
            ShowInfo(_loc.ErrorTitle, error ?? _loc.TotpInvalid, InfoBarSeverity.Warning);
        }
    }

    [RelayCommand]
    private void HideTotp(AccountItemViewModel? item)
    {
        if (item is null) return;
        item.ResetTotpPresentation();
        EnsureTotpPresentationTimer();
    }

    [RelayCommand]
    private void CopyTotpCode(AccountItemViewModel? item)
    {
        if (item is null || !item.IsTotpRevealed) return;
        var rawCode = item.TotpCodeText.Replace(" ", string.Empty);
        if (string.IsNullOrEmpty(rawCode) || rawCode.Contains('•')) return;

        try
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(rawCode);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);

            item.IsTotpCopied = true;
            _dispatcher.Enqueue(async () =>
            {
                await Task.Delay(2000);
                item.IsTotpCopied = false;
            });
        }
        catch
        {
            // Clipboard busy, ignore
        }
    }

    [RelayCommand]
    private async Task AddOrManageTotpAsync(AccountItemViewModel? item)
    {
        if (item is null || _totpStore is null) return;

        var setupResult = await _ui.PromptTotpSetupAsync(item.DisplayName, item.HasTotpConfigured);
        if (setupResult is null) return;

        if (setupResult.Action == TotpSetupAction.SaveNewKey && !string.IsNullOrWhiteSpace(setupResult.ProvisioningKey))
        {
            try
            {
                _totpStore.Save(item.Id, setupResult.ProvisioningKey, _clock.UtcNow);
                item.HasTotpConfigured = true;
                item.ResetTotpPresentation();
                EnsureTotpPresentationTimer();
                ShowInfo(_loc.RefreshAllDoneTitle, _loc.TotpKeySavedLocally, InfoBarSeverity.Success);
            }
            catch (Exception ex)
            {
                ShowInfo(_loc.ErrorTitle, ex.Message, InfoBarSeverity.Error);
            }
        }
        else if (setupResult.Action == TotpSetupAction.RemoveKey)
        {
            try
            {
                _totpStore.Delete(item.Id);
                item.HasTotpConfigured = false;
                item.ResetTotpPresentation();
                EnsureTotpPresentationTimer();
                ShowInfo(_loc.RemovedTitle, _loc.TotpKeySavedLocally, InfoBarSeverity.Informational);
            }
            catch (Exception ex)
            {
                ShowInfo(_loc.ErrorTitle, ex.Message, InfoBarSeverity.Error);
            }
        }
    }

    public void HideAllRevealedTotp()
    {
        foreach (var item in _all)
        {
            if (item.IsTotpRevealed)
                item.ResetTotpPresentation();
        }
        if (_totpPresentationTimer is not null && _totpPresentationTimer.IsEnabled)
            _totpPresentationTimer.Stop();
    }

    private void EnsureTotpPresentationTimer()
    {
        if (_totpPresentationTimer is null)
        {
            _totpPresentationTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _totpPresentationTimer.Tick += (_, _) => OnTotpPresentationTick();
        }

        bool anyRevealed = _all.Any(a => a.IsTotpRevealed);
        if (anyRevealed && !_totpPresentationTimer.IsEnabled)
        {
            _totpPresentationTimer.Start();
        }
        else if (!anyRevealed && _totpPresentationTimer.IsEnabled)
        {
            _totpPresentationTimer.Stop();
        }
    }

    private void OnTotpPresentationTick()
    {
        var now = _clock.UtcNow;
        bool anyRevealed = false;

        foreach (var item in _all)
        {
            if (!item.IsTotpRevealed) continue;

            // If revealed under an authorization session that is no longer authorized, hide immediately
            if (!item.IsDegradedReveal && _authService is not null && _authService.IsProtectionEnabled && !_authService.IsAuthorized)
            {
                item.ResetTotpPresentation();
                continue;
            }

            // 1. Reveal deadline (10s auto-hide)
            if (now >= item.RevealDeadline)
            {
                item.ResetTotpPresentation();
                continue;
            }

            // 2. TOTP period countdown / rollover
            if (item.TotpSecondsRemaining <= 1)
            {
                if (_totpStore is not null && _totpStore.TryComputeCode(item.Id, now, out var nextCode, out _))
                {
                    item.TotpCodeText = nextCode.Formatted;
                    item.TotpSecondsRemaining = nextCode.SecondsRemaining;
                    item.TotpCountdownText = $"{nextCode.SecondsRemaining}s";
                }
                else
                {
                    item.ResetTotpPresentation();
                    continue;
                }
            }
            else
            {
                item.TotpSecondsRemaining--;
                item.TotpCountdownText = $"{item.TotpSecondsRemaining}s";
            }

            anyRevealed = true;
        }

        if (!anyRevealed && _totpPresentationTimer is not null && _totpPresentationTimer.IsEnabled)
        {
            _totpPresentationTimer.Stop();
        }
    }

    private void RebuildList()
    {
        _profiles.Reconcile();
        var now = _clock.UtcNow;

        var activeProfile = _profiles.Profiles.FirstOrDefault(p => p.IsActive);
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

        _all.Clear();
        var ordered = _profiles.Profiles
            .OrderBy(p => p.SortOrder)
            .ThenByDescending(p => p.CreatedAt);
        foreach (var p in ordered)
        {
            var usageVm = GetOrCreateUsageVm(p.Id);
            bool isCompact = _settings.CollapsedProfileIds.Contains(p.Id);
            bool isRoutingActive = !IsRoutingActiveToApi && p.IsActive;
            var item = new AccountItemViewModel(p, now, _settings, usageVm, isCompact, isRoutingActive);
            if (_totpStore is not null)
                item.HasTotpConfigured = _totpStore.HasCredential(p.Id);
            _all.Add(item);
        }

        ShowEmptyState = _all.Count == 0;
        UpdateDetectedAccountState();
        ApplyFilter();

        // Rebuild API Providers collection
        ApiProviders.Clear();
        if (_apiProviderStore is not null)
        {
            var apiProfiles = _apiProviderStore.GetAll();
            foreach (var prof in apiProfiles)
            {
                var desc = _catalogService?.GetDescriptor(prof.CatalogProviderId);
                bool hasSecret = _secretStore?.HasApiKey(prof.Id) ?? false;
                bool isTargetActive = IsRoutingActiveToApi && activeTarget is ActiveTarget.Api a && a.Profile.Id == prof.Id;
                var vm = new ApiProviderItemViewModel(prof, desc, hasSecret, isTargetActive);
                var routeKey = !string.IsNullOrWhiteSpace(prof.SelectedRouteId) ? prof.SelectedRouteId : prof.BaseUrl;
                if (_modelCache?.TryGetModels(prof.Id, routeKey, 1, out var cachedModels) == true && cachedModels.Count > 0)
                {
                    vm.SetDiscoveredModels(cachedModels);
                }
                ApiProviders.Add(vm);
            }
        }
        ShowApiProvidersEmptyState = ApiProviders.Count == 0;

        OnPropertyChanged(nameof(AccountCount));
        OnPropertyChanged(nameof(ShowNormalEmptyState));
        OnPropertyChanged(nameof(ShowDetectedAccountEmptyState));
        EnsureTotpPresentationTimer();
    }

    private void UpdateDetectedAccountState()
    {
        var (detected, email, plan) = _profiles.GetUnmanagedActiveAccountInfo();
        HasDetectedActiveAccount = detected;
        if (detected)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(email)) parts.Add(email);
            if (!string.IsNullOrWhiteSpace(plan)) parts.Add(plan);
            DetectedActiveAccountDetails = parts.Count > 0 ? string.Join(" · ", parts) : string.Empty;
        }
        else
        {
            DetectedActiveAccountDetails = string.Empty;
        }

        ShowAdoptPrompt = detected && !ShowEmptyState;
        OnPropertyChanged(nameof(ShowNormalEmptyState));
        OnPropertyChanged(nameof(ShowDetectedAccountEmptyState));
    }

    public void Cleanup()
    {
        _countdownTimer?.Stop();
        _pollingTimer?.Stop();
        _totpPresentationTimer?.Stop();
        _startupRefreshCts?.Cancel();
        _startupRefreshCts?.Dispose();
        _pollingCoordinator.UsageUpdated -= OnUsageUpdated;
        _pollingCoordinator.RefreshingStateChanged -= OnRefreshingStateChanged;
    }

    private void ApplyFilter()
    {
        var query = SearchText?.Trim();
        IEnumerable<AccountItemViewModel> items = _all;
        if (!string.IsNullOrEmpty(query))
        {
            items = _all.Where(a =>
                a.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                a.Subtitle.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        Accounts.Clear();
        foreach (var a in items)
            Accounts.Add(a);
    }

    private void ShowSwitchResult(SwitchResult result, string toName)
    {
        var (title, message, severity) = result.Outcome switch
        {
            SwitchOutcome.Success => (_loc.SwitchedTitle, _loc.SwitchedMsg(toName), InfoBarSeverity.Success),
            SwitchOutcome.SuccessWithReopenWarning => (_loc.SwitchReopenWarnTitle, _loc.SwitchReopenWarnMsg, InfoBarSeverity.Warning),
            SwitchOutcome.RolledBack => (_loc.SwitchRolledBackTitle, _loc.SwitchRolledBackMsg, InfoBarSeverity.Warning),
            SwitchOutcome.AbortedProcessRemnant => (_loc.SwitchAbortedTitle, _loc.SwitchAbortedMsg, InfoBarSeverity.Warning),
            _ => (_loc.SwitchFailedTitle, _loc.SwitchFailedMsg, InfoBarSeverity.Error),
        };
        ShowInfo(title, message, severity);
    }

    private void ShowInfo(string title, string message, InfoBarSeverity severity)
    {
        InfoTitle = title;
        InfoMessage = message;
        InfoSeverity = severity;
        InfoOpen = true;
    }

    private async Task RunBusy(string text, Func<Task> action)
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
}

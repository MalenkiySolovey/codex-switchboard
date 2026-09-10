using System.Collections.ObjectModel;
using CodexSwitcher.App.Localization;
using CodexSwitcher.App.Services;
using CodexSwitcher.Core.Models;
using CodexSwitcher.Core.Abstractions;
using CodexSwitcher.Core.Services;
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
    private CancellationTokenSource? _startupRefreshCts;

    public ObservableCollection<AccountItemViewModel> Accounts { get; } = [];

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

    public bool ShowNormalEmptyState => ShowEmptyState && !HasDetectedActiveAccount;
    public bool ShowDetectedAccountEmptyState => ShowEmptyState && HasDetectedActiveAccount;

    public MainViewModel(
        ProfileService profiles, SwitchService switchService,
        SettingsStore settingsStore, AppSettings settings, IClock clock, IUiInteraction ui,
        IUsageService usageService, UsagePollingCoordinator pollingCoordinator,
        IUiDispatcher? dispatcher = null, IAppLifetime? appLifetime = null,
        ITotpCredentialStore? totpStore = null,
        ITotpRevealAuthorizationService? authService = null)
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

        _pollingCoordinator.UsageUpdated += OnUsageUpdated;
        _pollingCoordinator.RefreshingStateChanged += OnRefreshingStateChanged;

        SearchText = string.Empty;
        InfoMessage = string.Empty;
        InfoTitle = string.Empty;
        InfoSeverity = InfoBarSeverity.Informational;
        DetectedActiveAccountDetails = string.Empty;
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
            result = await Task.Run(() => _switch.SwitchAsync(
                _profiles.Profiles, item.Id, SwitchExecutionOptions.From(_settings)));
        });

        // A troca realmente saiu da conta anterior: marca-a como usada automaticamente.
        if (from is not null && result?.Outcome is SwitchOutcome.Success or SwitchOutcome.SuccessWithReopenWarning)
            _profiles.MarkUsed(from.Id);

        RebuildList();
        if (result is not null)
            ShowSwitchResult(result, item.DisplayName);
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
        var updated = await _ui.PromptSubscriptionTrackingAsync(item.DisplayName, item.Profile.SubscriptionTracking);
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

        // Hard security ordering: verification must succeed before secret decryption
        if (_authService is not null && _authService.IsVerificationRequired())
        {
            var outcome = await _authService.EnsureAuthorizedAsync(_loc.WindowsVerificationPromptMessage);
            if (!outcome.Success)
            {
                if (outcome.Status == WindowsVerificationResult.Canceled)
                {
                    ShowInfo(_loc.WarningTitle, _loc.WindowsVerificationCanceled, InfoBarSeverity.Informational);
                }
                else if (outcome.Status == WindowsVerificationResult.NotConfigured ||
                         outcome.Status == WindowsVerificationResult.NotAvailable ||
                         outcome.Status == WindowsVerificationResult.UnsupportedOperatingSystem)
                {
                    ShowInfo(_loc.WarningTitle, _loc.WindowsVerificationUnavailableMessage, InfoBarSeverity.Warning);
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
            item.TotpCodeText = code.Formatted;
            item.TotpSecondsRemaining = code.SecondsRemaining;
            item.TotpCountdownText = $"{code.SecondsRemaining}s";

            // Two-timer invariant: min(10s, authSessionRemaining)
            double maxSeconds = 10.0;
            if (_authService is not null && _authService.IsProtectionEnabled && _authService.IsAuthorized)
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
        if (_authService is not null && _authService.IsProtectionEnabled && !_authService.IsAuthorized)
        {
            HideAllRevealedTotp();
            return;
        }

        var now = _clock.UtcNow;
        bool anyRevealed = false;

        foreach (var item in _all)
        {
            if (!item.IsTotpRevealed) continue;

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

        _all.Clear();
        var ordered = _profiles.Profiles
            .OrderBy(p => p.SortOrder)
            .ThenByDescending(p => p.CreatedAt);
        foreach (var p in ordered)
        {
            var usageVm = GetOrCreateUsageVm(p.Id);
            bool isCompact = _settings.CollapsedProfileIds.Contains(p.Id);
            var item = new AccountItemViewModel(p, now, _settings, usageVm, isCompact);
            if (_totpStore is not null)
                item.HasTotpConfigured = _totpStore.HasCredential(p.Id);
            _all.Add(item);
        }

        ShowEmptyState = _all.Count == 0;
        UpdateDetectedAccountState();
        ApplyFilter();
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

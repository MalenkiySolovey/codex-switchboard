using System.Collections.ObjectModel;
using CodexSwitcher.App.Localization;
using CodexSwitcher.App.Services;
using CodexSwitcher.App.ViewModels;
using CodexSwitcher.Core.Abstractions;
using CodexSwitcher.Core.Models;
using CodexSwitcher.Core.Services;
using CodexSwitcher.Infra;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;

namespace CodexSwitcher.App.Features.Accounts;

/// <summary>
/// Dedicated ViewModel orchestrating presentation, commands, filtering, and lifecycle for ChatGPT accounts.
/// Encapsulates account switching, profile reconciliation, TOTP coordination, and quota usage presentation.
/// </summary>
public sealed partial class AccountsViewModel : ObservableObject, IDisposable
{
    private static Strings Loc => Strings.Current;

    private readonly ProfileService _profiles;
    private readonly SwitchService _switch;
    private readonly SettingsStore _settingsStore;
    private readonly AppSettings _settings;
    private readonly IClock _clock;
    private readonly IUiInteraction _ui;
    private readonly TotpPresentationCoordinator _totp;
    private readonly AccountUsageCoordinator _usage;
    private readonly ICodexTargetSwitchService? _targetSwitchService;

    private readonly List<AccountItemViewModel> _all = [];
    private ActiveTarget? _lastActiveTarget;
    private bool _lastIsRoutingActiveToApi;

    public ObservableCollection<AccountItemViewModel> Items { get; } = [];

    [ObservableProperty]
    public partial string SearchText { get; set; }

    [ObservableProperty]
    public partial bool IsRefreshingUsage { get; set; }

    [ObservableProperty]
    public partial bool ShowEmptyState { get; set; }

    [ObservableProperty]
    public partial bool ShowAdoptPrompt { get; set; }

    [ObservableProperty]
    public partial bool HasDetectedActiveAccount { get; set; }

    [ObservableProperty]
    public partial string DetectedActiveAccountDetails { get; set; }

    public bool ShowNormalEmptyState => ShowEmptyState && !HasDetectedActiveAccount;
    public bool ShowDetectedAccountEmptyState => ShowEmptyState && HasDetectedActiveAccount;
    public int AccountCount => _all.Count;

    public ProfileMetadata? ActiveProfile => _profiles.Profiles.FirstOrDefault(p => p.IsActive);
    public IReadOnlyList<ProfileMetadata> Profiles => _profiles.Profiles;

    public event EventHandler? TargetStateChanged;
    public event Action<string, string, InfoBarSeverity>? InfoRequested;
    public event Func<string, Func<Task>, Task>? RunBusyRequested;

    public AccountsViewModel(
        ProfileService profiles,
        SwitchService switchService,
        SettingsStore settingsStore,
        AppSettings settings,
        IClock clock,
        IUiInteraction ui,
        TotpPresentationCoordinator totpCoordinator,
        AccountUsageCoordinator usageCoordinator,
        ICodexTargetSwitchService? targetSwitchService = null)
    {
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _switch = switchService ?? throw new ArgumentNullException(nameof(switchService));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _totp = totpCoordinator ?? throw new ArgumentNullException(nameof(totpCoordinator));
        _usage = usageCoordinator ?? throw new ArgumentNullException(nameof(usageCoordinator));
        _targetSwitchService = targetSwitchService;

        _totp.InfoRequested += (t, m, s) => ShowInfo(t, m, s);
        _usage.InfoRequested += (t, m, s) => ShowInfo(t, m, s);
        _usage.RefreshingStateChanged += r => IsRefreshingUsage = r;

        SearchText = string.Empty;
        DetectedActiveAccountDetails = string.Empty;
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    [RelayCommand]
    private async Task LoadAsync()
    {
        await RunBusy(Loc.BusyLoading, () =>
        {
            _profiles.Load();
            return Task.CompletedTask;
        });

        // Cache-first startup: restore cached usage immediately without network delay
        await _usage.LoadCacheAsync(Loc.Pt);

        Rebuild(_lastActiveTarget, _lastIsRoutingActiveToApi);
        _usage.StartTimers(() => _profiles.Profiles);

        // Background initial refresh across accounts
        if (_profiles.Profiles.Count > 0)
        {
            _ = _usage.TriggerBackgroundRefreshForProfiles(_profiles.Profiles);
        }
    }

    public void Rebuild(ActiveTarget? activeTarget, bool isRoutingActiveToApi)
    {
        _lastActiveTarget = activeTarget;
        _lastIsRoutingActiveToApi = isRoutingActiveToApi;

        _profiles.Reconcile();
        var now = _clock.UtcNow;

        _all.Clear();
        var ordered = _profiles.Profiles
            .OrderBy(p => p.SortOrder)
            .ThenByDescending(p => p.CreatedAt);
        foreach (var p in ordered)
        {
            var usageVm = _usage.GetOrCreateUsageVm(p.Id);
            bool isCompact = _settings.CollapsedProfileIds.Contains(p.Id);
            bool isRoutingActive = !isRoutingActiveToApi && p.IsActive;
            var item = new AccountItemViewModel(p, now, _settings, usageVm, isCompact, isRoutingActive);
            item.HasTotpConfigured = _totp.HasCredential(p.Id);
            _all.Add(item);
        }

        ShowEmptyState = _all.Count == 0;
        UpdateDetectedAccountState();
        ApplyFilter();

        OnPropertyChanged(nameof(AccountCount));
        OnPropertyChanged(nameof(ShowNormalEmptyState));
        OnPropertyChanged(nameof(ShowDetectedAccountEmptyState));
        _totp.EnsureTotpPresentationTimer(_all);
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

        Items.Clear();
        foreach (var a in items)
            Items.Add(a);
    }

    public void SetForegroundActive(bool active)
    {
        _usage.SetForegroundActive(active, _profiles.Profiles);
    }

    public void HideAllRevealedTotp()
    {
        _totp.HideAll(_all);
    }

    [RelayCommand]
    private async Task RefreshAllAsync()
    {
        await _usage.RefreshAllAsync(_profiles.Profiles);
    }

    [RelayCommand]
    private async Task RefreshAccountAsync(AccountItemViewModel? item)
    {
        if (item is null) return;
        await _usage.RefreshAccountAsync(item.Profile);
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
        await RunBusy(Loc.BusySwitching(item.DisplayName), async () =>
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

        // Account transition succeeded: mark previous account as used
        if (from is not null && result?.Outcome is SwitchOutcome.Success or SwitchOutcome.SuccessWithReopenWarning)
            _profiles.MarkUsed(from.Id);

        Rebuild(_lastActiveTarget, _lastIsRoutingActiveToApi);
        TargetStateChanged?.Invoke(this, EventArgs.Empty);

        if (result is not null)
            ShowSwitchResult(result, item.DisplayName);
    }

    [RelayCommand]
    private async Task AddAccountAsync()
    {
        byte[]? authJson = null;
        await RunBusy(Loc.BusyWaitingLogin, async () =>
        {
            authJson = await _ui.RunEphemeralLoginAsync();
        });

        if (authJson is null)
        {
            ShowInfo(Loc.LoginCanceledTitle, Loc.LoginCanceledMsg, InfoBarSeverity.Informational);
            return;
        }

        var profile = _profiles.AddFromAuthJson(authJson, null);
        var nick = await _ui.PromptTextAsync(Loc.NicknameTitle, Loc.NicknamePrompt,
            profile.DisplayName, Loc.Save);
        if (!string.IsNullOrWhiteSpace(nick))
            _profiles.Rename(profile.Id, nick);

        Rebuild(_lastActiveTarget, _lastIsRoutingActiveToApi);
        ShowInfo(Loc.AddedTitle, Loc.AddedMsg(profile.DisplayName), InfoBarSeverity.Success);
        _ = _usage.TriggerBackgroundRefreshForProfiles([profile]);
    }

    [RelayCommand]
    private async Task AdoptActiveAsync()
    {
        ProfileMetadata? adopted = null;
        await RunBusy(Loc.BusyImporting, () =>
        {
            adopted = _profiles.AdoptActiveAccount();
            return Task.CompletedTask;
        });
        Rebuild(_lastActiveTarget, _lastIsRoutingActiveToApi);
        if (adopted is not null)
        {
            ShowInfo(Loc.ImportedTitle, Loc.ImportedMsg(adopted.DisplayName), InfoBarSeverity.Success);
            _ = _usage.TriggerBackgroundRefreshForProfiles([adopted]);
        }
    }

    [RelayCommand]
    private async Task DetectAccountAsync()
    {
        ReconciliationResult? result = null;
        ProfileMetadata? adopted = null;
        await RunBusy(Loc.BusyDetecting, () =>
        {
            result = _profiles.Load();
            if (result is { Match: ActiveMatch.None, ActiveFingerprint: not null })
                adopted = _profiles.AdoptActiveAccount();
            return Task.CompletedTask;
        });
        Rebuild(_lastActiveTarget, _lastIsRoutingActiveToApi);

        if (result is null) return;

        if (adopted is not null)
        {
            ShowInfo(Loc.ImportedTitle, Loc.ImportedMsg(adopted.DisplayName), InfoBarSeverity.Success);
            _ = _usage.TriggerBackgroundRefreshForProfiles([adopted]);
        }
        else if (result.ActiveFingerprint is null)
            ShowInfo(Loc.DetectNoneTitle, Loc.DetectNoneMsg, InfoBarSeverity.Informational);
        else
        {
            var active = _profiles.Profiles.FirstOrDefault(p => p.IsActive);
            ShowInfo(Loc.DetectKnownTitle,
                Loc.DetectKnownMsg(active?.DisplayName ?? Loc.CodexAccount), InfoBarSeverity.Informational);
        }
    }

    [RelayCommand]
    private async Task ImportAccountsAsync()
    {
        var document = await _ui.PickImportFileAsync();
        if (document is null) return;

        var prevProfiles = _profiles.Profiles.ToList();
        var count = 0;
        await RunBusy(Loc.BusyImporting, () =>
        {
            count = _profiles.Import(document);
            return Task.CompletedTask;
        });
        Rebuild(_lastActiveTarget, _lastIsRoutingActiveToApi);
        if (count > 0)
        {
            ShowInfo(Loc.ImportedTitle, Loc.ImportedAccountsMsg(count), InfoBarSeverity.Success);
            var newProfiles = _profiles.Profiles.Where(p => !prevProfiles.Any(prev => prev.Id == p.Id)).ToList();
            if (newProfiles.Count > 0)
            {
                _ = _usage.TriggerBackgroundRefreshForProfiles(newProfiles);
            }
        }
    }

    [RelayCommand]
    private async Task ExportAllAsync()
    {
        if (_all.Count == 0) return;
        if (!await _ui.ConfirmAsync(Loc.ExportTitle, Loc.ExportWarning, Loc.Export, destructive: false)) return;

        var saved = false;
        await RunBusy(Loc.BusyExporting, async () =>
        {
            saved = await _ui.SaveExportFileAsync("codex-switcher-accounts.codexswitcher", _profiles.ExportAll());
        });
        if (saved) ShowInfo(Loc.ExportedTitle, Loc.ExportedAllMsg(_all.Count), InfoBarSeverity.Success);
    }

    [RelayCommand]
    private async Task ExportOneAsync(AccountItemViewModel? item)
    {
        if (item is null) return;
        if (!await _ui.ConfirmAsync(Loc.ExportTitle, Loc.ExportOneWarning(item.DisplayName), Loc.Export, destructive: false)) return;

        var saved = false;
        await RunBusy(Loc.BusyExporting, async () =>
        {
            saved = await _ui.SaveExportFileAsync("codex-switcher-account.codexswitcher", _profiles.ExportOne(item.Id));
        });
        if (saved) ShowInfo(Loc.ExportedTitle, Loc.ExportedOneMsg(item.DisplayName), InfoBarSeverity.Success);
    }

    [RelayCommand]
    private async Task RenameAsync(AccountItemViewModel? item)
    {
        if (item is null) return;
        var nick = await _ui.PromptTextAsync(Loc.RenameTitle, Loc.RenamePrompt, item.DisplayName, Loc.Save);
        if (nick is null) return;
        _profiles.Rename(item.Id, nick);
        Rebuild(_lastActiveTarget, _lastIsRoutingActiveToApi);
    }

    [RelayCommand]
    private async Task EditSubscriptionTrackingAsync(AccountItemViewModel? item)
    {
        if (item is null) return;
        var updated = await _ui.PromptSubscriptionTrackingAsync(item.DisplayName, item.Profile.SubscriptionTracking, item.Profile.DetectedSubscription);
        if (ReferenceEquals(updated, item.Profile.SubscriptionTracking))
            return;

        _profiles.UpdateSubscriptionTracking(item.Id, updated);
        Rebuild(_lastActiveTarget, _lastIsRoutingActiveToApi);
    }

    [RelayCommand]
    private async Task RemoveAsync(AccountItemViewModel? item)
    {
        if (item is null) return;
        var ok = await _ui.ConfirmAsync(Loc.RemoveTitle, Loc.RemoveConfirm(item.DisplayName),
            Loc.Remove, destructive: true);
        if (!ok) return;

        _usage.Invalidate(item.Id);
        _profiles.Remove(item.Id);
        if (_settings.CollapsedProfileIds.Remove(item.Id))
        {
            _settingsStore.Save(_settings);
        }
        Rebuild(_lastActiveTarget, _lastIsRoutingActiveToApi);
        ShowInfo(Loc.RemovedTitle, Loc.RemovedMsg(item.DisplayName), InfoBarSeverity.Informational);
    }

    [RelayCommand]
    private void ToggleAccountCollapse(AccountItemViewModel? item)
    {
        if (item is null) return;
        _totp.ResetOnCollapse(item, _all);
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
        Rebuild(_lastActiveTarget, _lastIsRoutingActiveToApi);
    }

    [RelayCommand]
    private void MarkUsed(AccountItemViewModel? item)
    {
        if (item is null) return;
        _profiles.MarkUsed(item.Id);
        Rebuild(_lastActiveTarget, _lastIsRoutingActiveToApi);
    }

    [RelayCommand]
    private void UnmarkUsed(AccountItemViewModel? item)
    {
        if (item is null) return;
        _profiles.UnmarkUsed(item.Id);
        Rebuild(_lastActiveTarget, _lastIsRoutingActiveToApi);
    }

    [RelayCommand]
    private void Reorder(IReadOnlyList<Guid>? orderedIds)
    {
        if (orderedIds is null || orderedIds.Count == 0) return;
        if (!string.IsNullOrEmpty(SearchText)) return;
        _profiles.Reorder(orderedIds);
        Rebuild(_lastActiveTarget, _lastIsRoutingActiveToApi);
    }

    [RelayCommand]
    private async Task RevealTotpAsync(AccountItemViewModel? item)
    {
        await _totp.RevealTotpAsync(item, _all);
    }

    [RelayCommand]
    private void HideTotp(AccountItemViewModel? item)
    {
        _totp.HideTotp(item, _all);
    }

    [RelayCommand]
    private void CopyTotpCode(AccountItemViewModel? item)
    {
        _totp.CopyTotpCode(item);
    }

    [RelayCommand]
    private async Task AddOrManageTotpAsync(AccountItemViewModel? item)
    {
        await _totp.AddOrManageTotpAsync(item, _all);
    }

    public void Cleanup()
    {
        _usage.Dispose();
        _totp.Dispose();
    }

    public void Dispose()
    {
        Cleanup();
    }

    private void ShowSwitchResult(SwitchResult result, string toName)
    {
        var (title, message, severity) = result.Outcome switch
        {
            SwitchOutcome.Success => (Loc.SwitchedTitle, Loc.SwitchedMsg(toName), InfoBarSeverity.Success),
            SwitchOutcome.SuccessWithReopenWarning => (Loc.SwitchReopenWarnTitle, Loc.SwitchReopenWarnMsg, InfoBarSeverity.Warning),
            SwitchOutcome.RolledBack => (Loc.SwitchRolledBackTitle, Loc.SwitchRolledBackMsg, InfoBarSeverity.Warning),
            SwitchOutcome.AbortedProcessRemnant => (Loc.SwitchAbortedTitle, Loc.SwitchAbortedMsg, InfoBarSeverity.Warning),
            _ => (Loc.SwitchFailedTitle, Loc.SwitchFailedMsg, InfoBarSeverity.Error),
        };
        ShowInfo(title, message, severity);
    }

    private void ShowInfo(string title, string message, InfoBarSeverity severity)
    {
        InfoRequested?.Invoke(title, message, severity);
    }

    private async Task RunBusy(string text, Func<Task> action)
    {
        if (RunBusyRequested != null)
        {
            await RunBusyRequested.Invoke(text, action);
        }
        else
        {
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                ShowInfo(Loc.ErrorTitle, ex.Message, InfoBarSeverity.Error);
            }
        }
    }
}

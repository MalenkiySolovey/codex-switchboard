using System.Collections.ObjectModel;
using CodexSwitcher.App.Localization;
using CodexSwitcher.App.Services;
using CodexSwitcher.Core.Models;
using CodexSwitcher.Core.Abstractions;
using CodexSwitcher.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;

namespace CodexSwitcher.App.ViewModels;

/// <summary>ViewModel principal: lista de contas, switch, refresh, adicionar/adotar, renomear/remover.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly ProfileService _profiles;
    private readonly SwitchService _switch;
    private readonly RefreshService _refresh;
    private readonly SettingsStore _settingsStore;
    private readonly AppSettings _settings;
    private readonly IClock _clock;
    private readonly IUiInteraction _ui;
    private readonly Strings _loc = Strings.Current;

    private readonly List<AccountItemViewModel> _all = [];

    public ObservableCollection<AccountItemViewModel> Accounts { get; } = [];

    [ObservableProperty] public partial string SearchText { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string? BusyText { get; set; }
    [ObservableProperty] public partial bool InfoOpen { get; set; }
    [ObservableProperty] public partial string InfoMessage { get; set; }
    [ObservableProperty] public partial string InfoTitle { get; set; }
    [ObservableProperty] public partial InfoBarSeverity InfoSeverity { get; set; }
    [ObservableProperty] public partial bool ShowEmptyState { get; set; }
    [ObservableProperty] public partial bool ShowAdoptPrompt { get; set; }

    public MainViewModel(
        ProfileService profiles, SwitchService switchService, RefreshService refresh,
        SettingsStore settingsStore, AppSettings settings, IClock clock, IUiInteraction ui)
    {
        _profiles = profiles;
        _switch = switchService;
        _refresh = refresh;
        _settingsStore = settingsStore;
        _settings = settings;
        _clock = clock;
        _ui = ui;

        SearchText = string.Empty;
        InfoMessage = string.Empty;
        InfoTitle = string.Empty;
        InfoSeverity = InfoBarSeverity.Informational;
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
        RebuildList();
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
    private async Task RefreshOneAsync(AccountItemViewModel? item)
    {
        if (item is null) return;
        RefreshResult? result = null;
        await RunBusy(_loc.BusyRefreshingOne(item.DisplayName), async () =>
        {
            result = await Task.Run(() => _refresh.RefreshAsync(item.Profile, TimeSpan.FromMinutes(2)));
            _profiles.Save();
        });
        RebuildList();
        if (result is not null)
            ShowRefreshResult(result, item.DisplayName);
    }

    [RelayCommand]
    private async Task RefreshAllAsync()
    {
        if (_all.Count == 0) return;
        await RunBusy(_loc.BusyRefreshingAll, async () =>
        {
            foreach (var p in _profiles.Profiles.ToList())
            {
                BusyText = _loc.BusyRefreshingOne(p.DisplayName);
                await Task.Run(() => _refresh.RefreshAsync(p, TimeSpan.FromMinutes(2)));
            }
            _profiles.Save();
        });
        RebuildList();
        ShowInfo(_loc.RefreshAllDoneTitle, _loc.RefreshAllDoneMsg, InfoBarSeverity.Success);
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
            ShowInfo(_loc.ImportedTitle, _loc.ImportedMsg(adopted.DisplayName), InfoBarSeverity.Success);
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
            ShowInfo(_loc.ImportedTitle, _loc.ImportedMsg(adopted.DisplayName), InfoBarSeverity.Success);
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
    private async Task RenameAsync(AccountItemViewModel? item)
    {
        if (item is null) return;
        var nick = await _ui.PromptTextAsync(_loc.RenameTitle, _loc.RenamePrompt, item.DisplayName, _loc.Save);
        if (nick is null) return;
        _profiles.Rename(item.Id, nick);
        RebuildList();
    }

    [RelayCommand]
    private async Task RemoveAsync(AccountItemViewModel? item)
    {
        if (item is null) return;
        var ok = await _ui.ConfirmAsync(_loc.RemoveTitle, _loc.RemoveConfirm(item.DisplayName),
            _loc.Remove, destructive: true);
        if (!ok) return;
        _profiles.Remove(item.Id);
        RebuildList();
        ShowInfo(_loc.RemovedTitle, _loc.RemovedMsg(item.DisplayName), InfoBarSeverity.Informational);
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

    private void RebuildList()
    {
        _profiles.Reconcile();
        var now = _clock.UtcNow;

        _all.Clear();
        var ordered = _profiles.Profiles
            .OrderBy(p => p.SortOrder)
            .ThenByDescending(p => p.CreatedAt);
        foreach (var p in ordered)
            _all.Add(new AccountItemViewModel(p, now, _settings));

        ShowEmptyState = _all.Count == 0;
        ShowAdoptPrompt = _profiles.HasUnmanagedActiveAccount();
        ApplyFilter();
        OnPropertyChanged(nameof(AccountCount));
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

    private void ShowRefreshResult(RefreshResult result, string name)
    {
        var (title, message, severity) = result.Outcome switch
        {
            RefreshOutcome.Success => (_loc.RenewedTitle, _loc.RefreshSuccessMsg(name), InfoBarSeverity.Success),
            RefreshOutcome.NeedsReLogin => (_loc.NotRenewedTitle, _loc.RefreshNeedsReLoginMsg(name), InfoBarSeverity.Warning),
            _ => (_loc.NotRenewedTitle, _loc.RefreshTransientMsg, InfoBarSeverity.Warning),
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

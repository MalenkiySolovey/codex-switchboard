using System.Collections.ObjectModel;
using CodexSwitcher.App.Localization;
using CodexSwitcher.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CodexSwitcher.App.ViewModels;

/// <summary>
/// ViewModel representing an account's rate-limit status, quota windows, notices, and badges.
/// </summary>
public sealed partial class AccountUsageViewModel : ObservableObject
{
    private static Strings Loc => Strings.Current;

    public Guid ProfileId { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowStaleBadge))]
    [NotifyPropertyChangedFor(nameof(ShowRateLimitedBadge))]
    [NotifyPropertyChangedFor(nameof(ShowRefreshingBadge))]
    [NotifyPropertyChangedFor(nameof(ShowNeverLoadedText))]
    public partial UsageVisualState VisualState { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowStaleBadge))]
    public partial bool IsStale { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRefreshingBadge))]
    public partial bool IsRefreshing { get; set; }

    [ObservableProperty]
    public partial string? PlanType { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResetCreditsText))]
    [NotifyPropertyChangedFor(nameof(HasResetCredits))]
    public partial int? ResetCreditsAvailable { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    public partial string? NoticeMessage { get; set; }

    [ObservableProperty]
    public partial DateTimeOffset? ObservedAt { get; set; }

    public ObservableCollection<UsageWindowViewModel> Windows { get; } = [];
    public ObservableCollection<UsageWindowViewModel> CompactWindows { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAdditionalWindows))]
    [NotifyPropertyChangedFor(nameof(AdditionalWindowsBadge))]
    public partial int AdditionalWindowsCount { get; set; }

    public bool HasAdditionalWindows => AdditionalWindowsCount > 0;
    public string AdditionalWindowsBadge => $"+{AdditionalWindowsCount}";

    [ObservableProperty]
    public partial string AdditionalWindowsTooltip { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActivity))]
    [NotifyPropertyChangedFor(nameof(ShowActivitySection))]
    public partial AccountActivityViewModel? Activity { get; set; }

    public bool HasActivity => Activity?.HasActivity == true;
    public bool ShowActivitySection => Activity?.ShowActivitySection == true;

    public bool HasWindows => Windows.Count > 0;
    public bool HasNotice => !string.IsNullOrWhiteSpace(NoticeMessage);
    public bool HasResetCredits => ResetCreditsAvailable is { } credits && credits > 0;

    public RateLimitResetCredits? ResetCreditsDetail { get; private set; }
    public ObservableCollection<ResetCreditItemViewModel> DetailedCredits { get; } = [];
    public bool HasDetailedCredits => DetailedCredits.Count > 0;
    public bool HasUnreportedCredits => UnreportedCount > 0;
    public int UnreportedCount => ResetCreditsDetail?.UnreportedCount ?? 0;
    public string ResetCreditsHeader => Loc.ResetCreditsHeader;
    public string UnreportedCreditsText => Loc.ResetCreditsUnreported(UnreportedCount);
    public string NoDetailedCreditsMessage => Loc.ResetCreditsNone;

    public bool ShowStaleBadge => IsStale || VisualState == UsageVisualState.StaleCache;
    public bool ShowRateLimitedBadge => VisualState == UsageVisualState.RateLimited;
    public bool ShowRefreshingBadge => IsRefreshing || VisualState == UsageVisualState.Refreshing;
    public bool ShowNeverLoadedText => VisualState == UsageVisualState.NeverLoaded;

    public string StaleBadgeLabel => Loc.Pt ? "DADOS EM CACHE" : "STALE DATA";
    public string RateLimitedBadgeLabel => Loc.Pt ? "LIMITE ATINGIDO" : "RATE LIMITED";
    public string RefreshingBadgeLabel => Loc.Pt ? "Atualizando…" : "Refreshing…";
    public string NeverLoadedLabel => Loc.Pt ? "Cota ainda não consultada" : "No usage data yet";
    public string RefreshUsageTooltip => Loc.Pt ? "Atualizar cota agora" : "Refresh quota now";
    public string ToggleActivityLabel => Loc.Pt ? "Atividade da conta" : "Account activity";

    public string ResetCreditsText => ResetCreditsAvailable is { } c
        ? $"{c} {(Loc.Pt ? (c == 1 ? "crédito de reinício" : "créditos de reinício") : (c == 1 ? "reset credit" : "reset credits"))}"
        : string.Empty;

    public string CompactResetCreditsText => ResetCreditsAvailable is { } c
        ? Loc.CompactResetCreditsFormat(c)
        : string.Empty;

    public string CompactStatusText => VisualState switch
    {
        UsageVisualState.NeverLoaded => NeverLoadedLabel,
        UsageVisualState.Refreshing => RefreshingBadgeLabel,
        UsageVisualState.StaleCache => StaleBadgeLabel,
        UsageVisualState.RateLimited => RateLimitedBadgeLabel,
        UsageVisualState.AuthRequired => Loc.Pt ? "Reconectar" : "Auth required",
        UsageVisualState.ProcessDown => Loc.Pt ? "Codex indisponível" : "Codex unavailable",
        UsageVisualState.UnsupportedAccountType => Loc.Pt ? "Não suportado" : "Unsupported",
        _ => Loc.Pt ? "Sem cota" : "No usage data",
    };

    public AccountUsageViewModel(Guid profileId)
    {
        ProfileId = profileId;
        VisualState = UsageVisualState.NeverLoaded;
        Activity = new AccountActivityViewModel(null, AccountActivityAvailability.Unknown);
    }

    public void ApplyState(AccountUsageState state, DateTimeOffset now)
    {
        VisualState = state.VisualState;
        IsStale = state.IsStale;
        IsRefreshing = state.IsRefreshing;
        PlanType = state.PlanType;
        ResetCreditsAvailable = state.ResetCreditsAvailable;
        ResetCreditsDetail = state.ResetCreditsDetail;
        NoticeMessage = state.NoticeMessage;
        ObservedAt = state.ObservedAt;

        Windows.Clear();
        foreach (var winModel in state.Windows)
        {
            Windows.Add(new UsageWindowViewModel(winModel, now));
        }

        UpdateCompactWindows(now);

        DetailedCredits.Clear();
        if (state.ResetCreditsDetail?.Credits is { Count: > 0 })
        {
            foreach (var cred in state.ResetCreditsDetail.SortedCredits)
            {
                DetailedCredits.Add(new ResetCreditItemViewModel(cred, now));
            }
        }

        var wasExpanded = Activity?.IsExpanded ?? false;
        Activity = new AccountActivityViewModel(state.Activity, state.ActivityAvailability) { IsExpanded = wasExpanded };

        OnPropertyChanged(nameof(HasWindows));
        OnPropertyChanged(nameof(HasActivity));
        OnPropertyChanged(nameof(ShowActivitySection));
        OnPropertyChanged(nameof(HasDetailedCredits));
        OnPropertyChanged(nameof(HasUnreportedCredits));
        OnPropertyChanged(nameof(UnreportedCount));
        OnPropertyChanged(nameof(UnreportedCreditsText));
        OnPropertyChanged(nameof(CompactResetCreditsText));
        OnPropertyChanged(nameof(CompactStatusText));
    }

    public void UpdateCountdowns(DateTimeOffset now)
    {
        foreach (var win in Windows)
        {
            win.UpdateCountdown(now);
        }

        UpdateCompactWindows(now);
    }

    private void UpdateCompactWindows(DateTimeOffset now)
    {
        CompactWindows.Clear();
        if (Windows.Count == 0)
        {
            AdditionalWindowsCount = 0;
            AdditionalWindowsTooltip = string.Empty;
            return;
        }

        if (Windows.Count <= 2)
        {
            foreach (var w in Windows)
                CompactWindows.Add(w);
            AdditionalWindowsCount = 0;
            AdditionalWindowsTooltip = string.Empty;
            return;
        }

        // >2 windows: deterministic selection rule
        // 1. IsExhausted (descending)
        // 2. IsLowQuota (descending)
        // 3. DurationMinutes ?? int.MaxValue (ascending)
        // 4. Index (ascending)
        var indexed = Windows.Select((w, idx) => (Window: w, Index: idx)).ToList();
        var top2 = indexed
            .OrderByDescending(x => x.Window.IsExhausted)
            .ThenByDescending(x => x.Window.IsLowQuota)
            .ThenBy(x => x.Window.DurationMinutes ?? int.MaxValue)
            .ThenBy(x => x.Index)
            .Take(2)
            .Select(x => x.Window)
            .OrderBy(w => w.DurationMinutes ?? int.MaxValue)
            .ToList();

        foreach (var w in top2)
            CompactWindows.Add(w);

        var remaining = Windows.Except(top2).ToList();
        AdditionalWindowsCount = remaining.Count;

        var lines = new List<string> { Loc.AdditionalQuotaWindowsHeader };
        foreach (var rw in remaining)
        {
            lines.Add($"• {rw.DisplayLabel}: {rw.RemainingText} · {rw.ResetCountdownText}");
        }
        AdditionalWindowsTooltip = string.Join(Environment.NewLine, lines);
    }
}

using System.Globalization;
using CodexSwitcher.App.Localization;
using CodexSwitcher.Core.Models;
using CodexSwitcher.Core.Support;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CodexSwitcher.App.ViewModels;

/// <summary>
/// ViewModel representing historical token usage and daily activity for an account card.
/// Provides compact summary metrics and lightweight daily activity bars.
/// </summary>
public sealed partial class AccountActivityViewModel : ObservableObject
{
    private static Strings Loc => Strings.Current;

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    public bool HasActivity { get; }

    public AccountActivityAvailability Availability { get; }
    public string? StatusNotice { get; }
    public bool HasStatusNotice => !string.IsNullOrWhiteSpace(StatusNotice);
    public bool ShowActivitySection => HasActivity || HasStatusNotice;

    public bool IsUnsupportedRuntime => Availability == AccountActivityAvailability.UnsupportedRuntime;
    public bool IsTemporarilyUnavailable => Availability == AccountActivityAvailability.TemporarilyUnavailable;
    public bool IsLoading => Availability == AccountActivityAvailability.Loading;
    public bool IsAvailable => Availability == AccountActivityAvailability.Available;

    public string LifetimeTokens { get; }
    public string LifetimeTokensTooltip { get; }

    public string PeakDailyTokens { get; }
    public string PeakDailyTokensTooltip { get; }

    public string LongestTurn { get; }
    public string LongestTurnTooltip { get; }

    public string CurrentStreak { get; }
    public string LongestStreak { get; }

    public string? LatestActivityDate { get; }

    public IReadOnlyList<ActivityBarItemViewModel> Bars { get; }

    public string ActivitySectionTitle => Loc.Pt ? "ATIVIDADE HISTÓRICA" : "ACCOUNT ACTIVITY";
    public string LifetimeLabel => Loc.Pt ? "Total de tokens" : "Lifetime tokens";
    public string PeakDayLabel => Loc.Pt ? "Pico diário" : "Peak daily tokens";
    public string LongestTurnLabel => Loc.Pt ? "Maior turno" : "Longest turn";
    public string CurrentStreakLabel => Loc.Pt ? "Sequência atual" : "Current streak";
    public string LongestStreakLabel => Loc.Pt ? "Maior sequência" : "Longest streak";
    public string NoActivityLabel => Loc.Pt ? "Nenhuma atividade registrada" : "No activity reported";
    public string DisclaimerText => Loc.Pt ? "Atividade reportada pelo servidor • Fuso não convertido" : "Server-reported activity • Dates not converted";

    public AccountActivityViewModel(AccountActivitySnapshot? snapshot, AccountActivityAvailability availability = AccountActivityAvailability.Unknown)
    {
        Availability = availability;

        if (snapshot is null || (snapshot.Summary is null && snapshot.DailyBuckets.Count == 0))
        {
            HasActivity = false;
            LifetimeTokens = "—";
            LifetimeTokensTooltip = string.Empty;
            PeakDailyTokens = "—";
            PeakDailyTokensTooltip = string.Empty;
            LongestTurn = "—";
            LongestTurnTooltip = string.Empty;
            CurrentStreak = "—";
            LongestStreak = "—";
            Bars = Array.Empty<ActivityBarItemViewModel>();

            if (availability == AccountActivityAvailability.UnsupportedRuntime)
            {
                StatusNotice = Loc.Pt
                    ? "A atividade da conta não é suportada pela versão atual do Codex."
                    : "Account Activity is not supported by the selected Codex runtime.";
            }
            else if (availability == AccountActivityAvailability.TemporarilyUnavailable)
            {
                StatusNotice = Loc.Pt
                    ? "A atividade da conta está temporariamente indisponível. Os limites de taxa continuam ativos."
                    : "Account Activity is temporarily unavailable. Rate limits remain active.";
            }
            else
            {
                StatusNotice = null;
            }
            return;
        }

        HasActivity = true;
        if (availability == AccountActivityAvailability.UnsupportedRuntime)
        {
            StatusNotice = Loc.Pt
                ? "Exibindo atividade em cache. A versão atual do Codex não suporta atualizações ao vivo da atividade."
                : "Showing cached activity. Current Codex runtime does not support live activity updates.";
        }
        else if (availability == AccountActivityAvailability.TemporarilyUnavailable)
        {
            StatusNotice = Loc.Pt
                ? "A atividade da conta está temporariamente indisponível. Os limites de taxa continuam ativos."
                : "Account Activity is temporarily unavailable. Rate limits remain active.";
        }
        else
        {
            StatusNotice = null;
        }
        var summary = snapshot.Summary;

        LifetimeTokens = UsageMetricFormatter.FormatTokenCount(summary?.LifetimeTokens);
        LifetimeTokensTooltip = summary?.LifetimeTokens is { } lt ? $"{lt:N0} tokens" : string.Empty;

        PeakDailyTokens = UsageMetricFormatter.FormatTokenCount(summary?.PeakDailyTokens);
        PeakDailyTokensTooltip = summary?.PeakDailyTokens is { } pt ? $"{pt:N0} tokens" : string.Empty;

        LongestTurn = UsageMetricFormatter.FormatTurnDuration(summary?.LongestRunningTurnSeconds);
        LongestTurnTooltip = summary?.LongestRunningTurnSeconds is { } ls ? $"{ls:N0} seconds" : string.Empty;

        CurrentStreak = summary?.CurrentStreakDays is { } cs
            ? $"{cs} {(Loc.Pt ? (cs == 1 ? "dia" : "dias") : (cs == 1 ? "day" : "days"))}"
            : "—";

        LongestStreak = summary?.LongestStreakDays is { } lsd
            ? $"{lsd} {(Loc.Pt ? (lsd == 1 ? "dia" : "dias") : (lsd == 1 ? "day" : "days"))}"
            : "—";

        var buckets = snapshot.DailyBuckets;
        if (buckets.Count > 0)
        {
            LatestActivityDate = buckets[^1].StartDateRaw;

            long maxTokens = 0;
            foreach (var b in buckets)
            {
                if (b.Tokens > maxTokens)
                    maxTokens = b.Tokens;
            }

            const double maxBarHeight = 36.0;
            const double minBarHeight = 4.0;
            var barsList = new List<ActivityBarItemViewModel>(buckets.Count);

            foreach (var b in buckets)
            {
                double height = 0.0;
                if (b.Tokens > 0)
                {
                    height = maxTokens > 0
                        ? Math.Max(minBarHeight, ((double)b.Tokens / maxTokens) * maxBarHeight)
                        : minBarHeight;
                }

                string displayDate = b.ParsedDate.HasValue
                    ? b.ParsedDate.Value.ToString("MM/dd", CultureInfo.InvariantCulture)
                    : b.StartDateRaw;

                string formattedTokens = UsageMetricFormatter.FormatTokenCount(b.Tokens);
                string tooltip = $"{b.StartDateRaw}: {b.Tokens:N0} tokens";

                barsList.Add(new ActivityBarItemViewModel(
                    startDateRaw: b.StartDateRaw,
                    displayDate: displayDate,
                    tokens: b.Tokens,
                    formattedTokens: formattedTokens,
                    barHeight: height,
                    tooltipText: tooltip));
            }

            Bars = barsList;
        }
        else
        {
            Bars = Array.Empty<ActivityBarItemViewModel>();
        }
    }

    [RelayCommand]
    private void ToggleExpand()
    {
        IsExpanded = !IsExpanded;
    }
}

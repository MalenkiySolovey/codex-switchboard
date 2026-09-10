using CodexSwitcher.App.Localization;
using CodexSwitcher.Core.Models;
using CodexSwitcher.Core.Support;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CodexSwitcher.App.ViewModels;

/// <summary>
/// ViewModel representing an individual quota window dynamically bound to the UI.
/// Supports arbitrary durations (45m, 120m, 300m, 10080m) without hardcoded assumptions.
/// </summary>
public sealed partial class UsageWindowViewModel : ObservableObject
{
    private static Strings Loc => Strings.Current;

    public string Slot { get; }
    public int? DurationMinutes { get; }
    public string DisplayLabel { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsedText))]
    public partial double? UsedPercent { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RemainingText))]
    [NotifyPropertyChangedFor(nameof(ProgressValue))]
    public partial double? RemainingPercent { get; set; }

    [ObservableProperty]
    public partial DateTimeOffset? ResetsAt { get; set; }

    [ObservableProperty]
    public partial string ResetCountdownText { get; set; } = "-";

    [ObservableProperty]
    public partial string ResetTooltipText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsExhausted { get; set; }

    [ObservableProperty]
    public partial bool IsLowQuota { get; set; }

    public double ProgressValue => RemainingPercent is { } r ? Math.Clamp(r, 0.0, 100.0) : 0.0;

    public string RemainingText => RemainingPercent is { } rem
        ? $"{Math.Round(rem)}% {(Loc.Pt ? "disponível" : "remaining")}"
        : (Loc.Pt ? "Cota desconhecida" : "Unknown quota");

    public string UsedText => UsedPercent is { } used
        ? $"{(Loc.Pt ? "Usado" : "Used")} {Math.Round(used)}%"
        : $"{(Loc.Pt ? "Usado: desconhecido" : "Used: unknown")}";

    public string LimitReachedLabel => Loc.Pt ? "LIMITE ATINGIDO" : "LIMIT REACHED";

    public string CompactRemainingText => RemainingPercent is { } rem
        ? Loc.CompactRemainingFormat(rem)
        : (Loc.Pt ? "desc." : "unk.");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CompactSummaryText))]
    public partial string CompactResetText { get; set; } = "-";

    public string CompactSummaryText => $"{DisplayLabel} · {CompactRemainingText} · {CompactResetText}";

    public string CompactTooltipText => $"{DisplayLabel}: {RemainingText}, {ResetCountdownText}\n{ResetTooltipText}";

    public UsageWindowViewModel(UsageWindowModel model, DateTimeOffset now)
    {
        Slot = model.Slot;
        DurationMinutes = model.DurationMinutes;
        DisplayLabel = model.DisplayLabel;
        UsedPercent = model.UsedPercent;
        RemainingPercent = model.RemainingPercent;
        ResetsAt = model.ResetsAt;
        IsExhausted = model.IsExhausted;
        IsLowQuota = model.IsLowQuota;

        UpdateCountdown(now);
    }

    public void UpdateCountdown(DateTimeOffset now)
    {
        ResetCountdownText = UsageCountdownFormatter.FormatCountdownWithAbsolute(ResetsAt, now, culture: null, pt: Loc.Pt);
        ResetTooltipText = UsageCountdownFormatter.FormatFullResetTooltip(ResetsAt, culture: null, pt: Loc.Pt);
        CompactResetText = UsageCountdownFormatter.FormatCompactCountdown(ResetsAt, now, pt: Loc.Pt);
        OnPropertyChanged(nameof(CompactRemainingText));
        OnPropertyChanged(nameof(CompactSummaryText));
        OnPropertyChanged(nameof(CompactTooltipText));
    }
}

using CodexSwitcher.Core.Accounts.Contracts;
using CodexSwitcher.Core.Accounts.Formatting;
using CodexSwitcher.Core.Accounts.Models;
using CodexSwitcher.Core.Accounts.Services;
using CodexSwitcher.Core.Common.Dispatcher;
using CodexSwitcher.Core.Common.Enums;
using CodexSwitcher.Core.Common.Environment;
using CodexSwitcher.Core.Common.Errors;
using CodexSwitcher.Core.Common.Lifecycle;
using CodexSwitcher.Core.Common.Logging;
using CodexSwitcher.Core.Common.Storage;
using CodexSwitcher.Core.Common.Time;
using CodexSwitcher.Core.Providers.Catalog;
using CodexSwitcher.Core.Providers.Contracts;
using CodexSwitcher.Core.Providers.Models;
using CodexSwitcher.Core.Providers.Services;
using CodexSwitcher.Core.Routing.Contracts;
using CodexSwitcher.Core.Routing.Models;
using CodexSwitcher.Core.Routing.Services;
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
using CodexSwitcher.Core.Usage.Formatting;
using CodexSwitcher.Core.Usage.Models;
using CodexSwitcher.App.Localization;
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

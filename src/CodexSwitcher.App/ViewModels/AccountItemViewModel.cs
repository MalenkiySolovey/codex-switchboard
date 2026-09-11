using CodexSwitcher.App.Localization;
using CodexSwitcher.Core.Models;
using CodexSwitcher.Core.Support;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CodexSwitcher.App.ViewModels;

/// <summary>Selo visual derivado do estado do perfil. Ver BUSINESS_RULES.md §3.4.</summary>
public enum AccountBadge
{
    ActiveNow,
    Healthy,
    NeedsReLogin,
    Error,
    Unavailable,
}

/// <summary>
/// Snapshot de exibição de um perfil (a lista é reconstruída após cada operação).
/// Formata datas relativas e o selo de saúde conforme §3.4 e §8, no idioma detectado.
/// </summary>
public sealed partial class AccountItemViewModel : ObservableObject
{
    private static Strings Loc => Strings.Current;

    public AccountItemViewModel(ProfileMetadata profile, DateTimeOffset now, AppSettings settings, AccountUsageViewModel? usage = null, bool isCompact = false)
    {
        Profile = profile;
        Id = profile.Id;
        DisplayName = profile.DisplayName;
        IsActive = profile.IsActive;
        Usage = usage ?? new AccountUsageViewModel(profile.Id);
        IsCompact = isCompact;

        Subtitle = !string.IsNullOrWhiteSpace(profile.AccountEmail)
            ? profile.AccountEmail!
            : profile.AuthMode is { Length: > 0 } mode ? Loc.ModeFormat(mode) : Loc.CodexAccount;

        Initials = ComputeInitials(profile);
        Badge = ComputeBadge(profile);
        PlanText = profile.PlanType;

        LastSwitchedText = profile.LastSwitchedAt is null
            ? Loc.NeverUsedHere
            : Loc.SwitchedFormat(RelativeTime.Humanize(profile.LastSwitchedAt, now, Loc.Pt));
        LastSwitchedTooltip = profile.LastSwitchedAt?.ToLocalTime().ToString("dd/MM/yyyy HH:mm") ?? "-";

        HealthText = ComputeHealthText(profile, now, settings);
        NeedsAttention = Badge is AccountBadge.NeedsReLogin or AccountBadge.Error;
        IsMarkedUsed = profile.MarkedUsedAt is { } markedAt && (now - markedAt) < TimeSpan.FromHours(24);

        var today = DateOnly.FromDateTime(now.LocalDateTime);
        var subPres = SubscriptionPresentationResolver.Resolve(
            profile.SubscriptionTracking,
            profile.DetectedSubscription,
            profile.PlanType,
            today,
            culture: null,
            pt: Loc.Pt);
        SubscriptionDisplayText = subPres.DisplayText;
        SubscriptionTooltipText = subPres.TooltipText;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsExpanded))]
    [NotifyPropertyChangedFor(nameof(ExpandCollapseTooltip))]
    [NotifyPropertyChangedFor(nameof(ExpandCollapseAutomationName))]
    [NotifyPropertyChangedFor(nameof(ShowCompactResetCredits))]
    public partial bool IsCompact { get; set; }

    public bool IsExpanded => !IsCompact;
    public bool ShowCompactResetCredits => IsCompact && Usage.HasResetCredits;
    public string ExpandCollapseTooltip => IsCompact ? Loc.ExpandAccountDetails : Loc.CollapseAccountDetails;
    public string ExpandCollapseAutomationName => ExpandCollapseTooltip;

    public ProfileMetadata Profile { get; }
    public Guid Id { get; }
    public AccountUsageViewModel Usage { get; }
    public string DisplayName { get; }
    public string Subtitle { get; }
    public string Initials { get; }
    public bool IsActive { get; }
    public AccountBadge Badge { get; }
    public string? PlanText { get; }
    public string? SubscriptionDisplayText { get; }
    public string? SubscriptionTooltipText { get; }
    public bool HasSubscriptionText => !string.IsNullOrWhiteSpace(SubscriptionDisplayText);
    public bool HasPlanOrSubscription => !string.IsNullOrWhiteSpace(PlanText) || HasSubscriptionText;
    public bool ShowSubscriptionSeparator => !string.IsNullOrWhiteSpace(PlanText) && HasSubscriptionText;
    public string LastSwitchedText { get; }
    public string LastSwitchedTooltip { get; }
    public string HealthText { get; }
    public bool NeedsAttention { get; }

    /// <summary>Marcado como "usado" nas últimas 24h. Ver <see cref="ProfileMetadata.MarkedUsedAt"/>.</summary>
    public bool IsMarkedUsed { get; }

    public bool CanSwitch => !IsActive && Badge != AccountBadge.Unavailable;
    // Rótulos localizados usados dentro do DataTemplate do card.
    public string SwitchLabel => Loc.Switch;
    public string InUseLabel => Loc.InUse;
    public string ExportLabel => Loc.Export;
    public string RenameLabel => Loc.Rename;
    public string SubscriptionTrackingLabel => Loc.SubscriptionTrackingLabel;
    public string MarkReLoginLabel => Loc.MarkNeedsReLogin;
    public string MarkUsedLabel => Loc.MarkUsed;
    public string UnmarkUsedLabel => Loc.UnmarkUsed;
    public string RemoveLabel => Loc.Remove;
    public string MoreActionsLabel => Loc.MoreActions;

    // Phase 9: Propriedades de apresentação do TOTP 2FA do perfil
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotpMenuLabel))]
    public partial bool HasTotpConfigured { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotpRevealButtonAutomationName))]
    [NotifyPropertyChangedFor(nameof(TotpAccessibleText))]
    public partial bool IsTotpRevealed { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotpAccessibleText))]
    public partial string TotpCodeText { get; set; } = Loc.TotpHiddenPlaceholder;

    [ObservableProperty]
    public partial int TotpSecondsRemaining { get; set; }

    [ObservableProperty]
    public partial string TotpCountdownText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotpCopyTooltip))]
    public partial bool IsTotpCopied { get; set; }

    public DateTimeOffset RevealDeadline { get; set; } = DateTimeOffset.MinValue;
    public bool IsDegradedReveal { get; set; }

    public string TotpMenuLabel => HasTotpConfigured ? Loc.ManageTotpKey : Loc.AddTotpKey;
    public string TotpRevealButtonAutomationName => IsTotpRevealed ? Loc.HideTotpCode : Loc.RevealTotpCode;
    public string TotpCopyButtonAutomationName => Loc.CopyTotpCode;
    public string TotpAccessibleText => IsTotpRevealed ? TotpCodeText : Loc.TotpHiddenPlaceholder;
    public string TotpRevealTooltip => Loc.RevealTotpCode;
    public string TotpHideTooltip => Loc.HideTotpCode;
    public string TotpCopyTooltip => IsTotpCopied ? Loc.TotpCopied : Loc.CopyTotpCode;

    public void ResetTotpPresentation()
    {
        IsTotpRevealed = false;
        TotpCodeText = Loc.TotpHiddenPlaceholder;
        TotpSecondsRemaining = 0;
        TotpCountdownText = string.Empty;
        IsTotpCopied = false;
        RevealDeadline = DateTimeOffset.MinValue;
        IsDegradedReveal = false;
    }

    private static AccountBadge ComputeBadge(ProfileMetadata p) => p.HealthStatus switch
    {
        HealthStatus.Unknown => AccountBadge.Unavailable,
        HealthStatus.Error => AccountBadge.Error,
        HealthStatus.NeedsReLogin => AccountBadge.NeedsReLogin,
        _ => p.IsActive ? AccountBadge.ActiveNow : AccountBadge.Healthy,
    };

    private static string ComputeHealthText(ProfileMetadata p, DateTimeOffset now, AppSettings settings)
    {
        if (p.HealthStatus == HealthStatus.NeedsReLogin) return Loc.HealthNeedsReLogin;
        if (p.HealthStatus == HealthStatus.Error) return p.LastError?.Message ?? Loc.HealthError;
        if (p.HealthStatus == HealthStatus.Unknown) return Loc.HealthUnavailable;
        return Loc.HealthSaved;
    }

    private static string ComputeInitials(ProfileMetadata p)
    {
        var source = !string.IsNullOrWhiteSpace(p.Nickname) ? p.Nickname
            : !string.IsNullOrWhiteSpace(p.AccountEmail) ? p.AccountEmail! : "?";
        source = source.Trim();
        if (source.Length == 0) return "?";
        var parts = source.Split([' ', '.', '@', '-', '_'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
            return (char.ToUpperInvariant(parts[0][0]).ToString() + char.ToUpperInvariant(parts[1][0])).Trim();
        return char.ToUpperInvariant(source[0]).ToString();
    }
}

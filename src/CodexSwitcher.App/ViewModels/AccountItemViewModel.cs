using CodexSwitcher.Core.Accounts.Contracts;
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
using CodexSwitcher.Core.Accounts.Formatting;
using CodexSwitcher.Core.Accounts.Models;
using CodexSwitcher.App.Localization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CodexSwitcher.App.ViewModels;

/// <summary>Selo visual derivado do estado do perfil. Ver BUSINESS_RULES.md §3.4.</summary>
public enum AccountBadge
{
    ActiveNow,
    CredentialActive,
    Healthy,
    NeedsReLogin,
    Error,
    Unavailable,
}

/// <summary>Semantic visual state of an account card. Controls border and background styling.</summary>
public enum AccountCardVisualState
{
    /// <summary>Account is active in auth.json and OpenAI routing is currently active.</summary>
    ActiveRouting,
    /// <summary>Account is active in auth.json, but routing is currently pointing to an API provider.</summary>
    ActiveCredential,
    /// <summary>Normal inactive account card.</summary>
    Normal,
}

/// <summary>
/// Snapshot de exibição de um perfil (a lista é reconstruída após cada operação).
/// Formata datas relativas e o selo de saúde conforme §3.4 e §8, no idioma detectado.
/// </summary>
public sealed partial class AccountItemViewModel : ObservableObject
{
    private static Strings Loc => Strings.Current;

    public AccountItemViewModel(
        ProfileMetadata profile,
        DateTimeOffset now,
        AppSettings settings,
        AccountUsageViewModel? usage = null,
        bool isCompact = false,
        bool isRoutingActive = true)
    {
        Profile = profile;
        Id = profile.Id;
        DisplayName = profile.DisplayName;
        IsActive = profile.IsActive;
        IsRoutingActive = isRoutingActive;
        Usage = usage ?? new AccountUsageViewModel(profile.Id);
        IsCompact = isCompact;

        Subtitle = !string.IsNullOrWhiteSpace(profile.AccountEmail)
            ? profile.AccountEmail!
            : profile.AuthMode is { Length: > 0 } mode ? Loc.ModeFormat(mode) : Loc.CodexAccount;

        Initials = ComputeInitials(profile);
        Badge = ComputeBadge(profile, isRoutingActive);
        PlanText = profile.PlanType;

        LastSwitchedText = profile.LastSwitchedAt is null
            ? Loc.NeverUsedHere
            : Loc.SwitchedFormat(RelativeTime.Humanize(profile.LastSwitchedAt, now, Loc.Pt));
        LastSwitchedTooltip = profile.LastSwitchedAt?.ToLocalTime().ToString("dd/MM/yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture) ?? "-";

        HealthText = ComputeHealthText(profile, now, settings);
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

    public ProfileMetadata Profile { get; private set; }
    public Guid Id { get; }
    public AccountUsageViewModel Usage { get; }

    [ObservableProperty]
    public partial string DisplayName { get; set; }

    [ObservableProperty]
    public partial string Subtitle { get; set; }

    [ObservableProperty]
    public partial string Initials { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInUse))]
    [NotifyPropertyChangedFor(nameof(CanSwitch))]
    [NotifyPropertyChangedFor(nameof(CardVisualState))]
    public partial bool IsActive { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInUse))]
    [NotifyPropertyChangedFor(nameof(CanSwitch))]
    [NotifyPropertyChangedFor(nameof(CardVisualState))]
    public partial bool IsRoutingActive { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSwitch))]
    [NotifyPropertyChangedFor(nameof(NeedsAttention))]
    public partial AccountBadge Badge { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlanOrSubscription))]
    [NotifyPropertyChangedFor(nameof(ShowSubscriptionSeparator))]
    public partial string? PlanText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSubscriptionText))]
    [NotifyPropertyChangedFor(nameof(HasPlanOrSubscription))]
    [NotifyPropertyChangedFor(nameof(ShowSubscriptionSeparator))]
    public partial string? SubscriptionDisplayText { get; set; }

    [ObservableProperty]
    public partial string? SubscriptionTooltipText { get; set; }

    public bool HasSubscriptionText => !string.IsNullOrWhiteSpace(SubscriptionDisplayText);
    public bool HasPlanOrSubscription => !string.IsNullOrWhiteSpace(PlanText) || HasSubscriptionText;
    public bool ShowSubscriptionSeparator => !string.IsNullOrWhiteSpace(PlanText) && HasSubscriptionText;

    [ObservableProperty]
    public partial string LastSwitchedText { get; set; }

    [ObservableProperty]
    public partial string LastSwitchedTooltip { get; set; }

    [ObservableProperty]
    public partial string HealthText { get; set; }

    public bool NeedsAttention => Badge is AccountBadge.NeedsReLogin or AccountBadge.Error;

    /// <summary>Marcado como "usado" nas últimas 24h. Ver <see cref="ProfileMetadata.MarkedUsedAt"/>.</summary>
    [ObservableProperty]
    public partial bool IsMarkedUsed { get; set; }

    /// <summary>Fully in use: active in auth.json AND routing in config.toml is set to openai.</summary>
    public bool IsInUse => IsActive && IsRoutingActive;

    public bool CanSwitch => !IsInUse && Badge != AccountBadge.Unavailable;

    /// <summary>
    /// Current semantic visual state for the full card border and background.
    /// Invariant: ONLY active/current routing or credential states receive special borders.
    /// Marked-used or inactive accounts ALWAYS receive neutral Normal state.
    /// </summary>
    public AccountCardVisualState CardVisualState =>
        IsActive && IsRoutingActive ? AccountCardVisualState.ActiveRouting :
        IsActive && !IsRoutingActive ? AccountCardVisualState.ActiveCredential :
        AccountCardVisualState.Normal;

    public void UpdateState(
        ProfileMetadata profile,
        bool isRoutingActive,
        DateTimeOffset now,
        AppSettings settings)
    {
        Profile = profile;
        DisplayName = profile.DisplayName;
        Subtitle = !string.IsNullOrWhiteSpace(profile.AccountEmail)
            ? profile.AccountEmail!
            : profile.AuthMode is { Length: > 0 } mode ? Loc.ModeFormat(mode) : Loc.CodexAccount;

        Initials = ComputeInitials(profile);
        IsActive = profile.IsActive;
        IsRoutingActive = isRoutingActive;
        Badge = ComputeBadge(profile, isRoutingActive);
        PlanText = profile.PlanType;

        LastSwitchedText = profile.LastSwitchedAt is null
            ? Loc.NeverUsedHere
            : Loc.SwitchedFormat(RelativeTime.Humanize(profile.LastSwitchedAt, now, Loc.Pt));
        LastSwitchedTooltip = profile.LastSwitchedAt?.ToLocalTime().ToString("dd/MM/yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture) ?? "-";

        HealthText = ComputeHealthText(profile, now, settings);
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
    // Rótulos localizados usados dentro do DataTemplate do card.
    public string SwitchLabel => Loc.Switch;
    public string InUseLabel => Loc.InUse;
    public string ExportLabel => Loc.Export;
    public string RenameLabel => Loc.Rename;
    public string ReauthenticateLabel => Loc.Reauthenticate;
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

    private static AccountBadge ComputeBadge(ProfileMetadata p, bool isRoutingActive) => p.HealthStatus switch
    {
        HealthStatus.Unknown => AccountBadge.Unavailable,
        HealthStatus.Error => AccountBadge.Error,
        HealthStatus.NeedsReLogin => AccountBadge.NeedsReLogin,
        _ => p.IsActive
            ? (isRoutingActive ? AccountBadge.ActiveNow : AccountBadge.CredentialActive)
            : AccountBadge.Healthy,
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

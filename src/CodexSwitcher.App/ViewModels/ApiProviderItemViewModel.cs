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
using CodexSwitcher.Core.Providers.Contracts;
using CodexSwitcher.Core.Providers.Models;
using CodexSwitcher.Core.Providers.Services;
using CodexSwitcher.Core.Routing.Contracts;
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
using CodexSwitcher.Core.Routing.Models;
using CodexSwitcher.Core.Providers.Catalog;
using CodexSwitcher.App.Localization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CodexSwitcher.App.ViewModels;

/// <summary>Semantic visual state of an API provider card. Controls border and background styling.</summary>
public enum ApiProviderCardVisualState
{
    /// <summary>Provider is the active routing target.</summary>
    ActiveRouting,
    /// <summary>Provider has no API key configured.</summary>
    KeyRequired,
    /// <summary>Normal inactive provider card.</summary>
    Normal,
}

/// <summary>
/// Presentation ViewModel for an API Provider card in the UI.
/// Exposes safe key previews, capability tri-state facts, and routing state.
/// </summary>
public sealed partial class ApiProviderItemViewModel : ObservableObject
{
    private long _latestRefreshGeneration;

    private static Strings Loc => Strings.Current;

    public ApiProviderProfile Profile { get; private set; }
    public ProviderDescriptor? Descriptor { get; private set; }

    public Guid Id => Profile.Id;
    public string StableCodexProviderId => Profile.StableCodexProviderId;
    public string? CatalogProviderId => Profile.CatalogProviderId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSwitch))]
    [NotifyPropertyChangedFor(nameof(CardVisualState))]
    public partial bool IsTargetActive { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSwitch))]
    [NotifyPropertyChangedFor(nameof(CardVisualState))]
    public partial bool HasSecret { get; set; }

    /// <summary>
    /// Current semantic visual state for the full provider card border and background.
    /// </summary>
    public ApiProviderCardVisualState CardVisualState =>
        IsTargetActive ? ApiProviderCardVisualState.ActiveRouting :
        !HasSecret ? ApiProviderCardVisualState.KeyRequired :
        ApiProviderCardVisualState.Normal;

    public void UpdateRoutingState(bool isTargetActive)
    {
        IsTargetActive = isTargetActive;
    }

    [ObservableProperty]
    public partial string DisplayName { get; set; }

    [ObservableProperty]
    public partial string CatalogProviderName { get; set; }

    [ObservableProperty]
    public partial string BaseUrl { get; set; }

    [ObservableProperty]
    public partial string SelectedRoute { get; set; }

    [ObservableProperty]
    public partial string SelectedModel { get; set; }

    [ObservableProperty]
    public partial string KeyPreview { get; set; }

    [ObservableProperty]
    public partial ApiProviderProfileStatus Status { get; set; }

    [ObservableProperty]
    public partial bool IsLoadingModels { get; set; }

    [ObservableProperty]
    public partial string ModelsStatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModelsSummary))]
    public partial int ModelsReportedCount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModelsSummary))]
    public partial int ModelsEnabledCount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModelsSummary))]
    public partial DateTimeOffset? LastModelsRefreshAt { get; set; }

    /// <summary>Compact, persisted-inventory based model summary for the card.</summary>
    public string ModelsSummary => ModelsReportedCount > 0
        ? $"{ModelsReportedCount} reported · {ModelsEnabledCount} enabled"
        : ModelsEnabledCount > 0
            ? $"{ModelsEnabledCount} enabled"
            : "No models discovered";

    [ObservableProperty]
    public partial string BalanceSummary { get; set; } = "Unknown";

    [ObservableProperty]
    public partial string UsageSummary { get; set; } = "Unknown";

    [ObservableProperty]
    public partial string Initials { get; set; } = "AP";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRoutePoolLabel))]
    public partial string? RoutePoolLabel { get; set; }

    public bool HasRoutePoolLabel => !string.IsNullOrWhiteSpace(RoutePoolLabel);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCompatibilityBadge))]
    [NotifyPropertyChangedFor(nameof(CompatibilityBadgeText))]
    [NotifyPropertyChangedFor(nameof(CompatibilityBadgeBackground))]
    public partial CodexCompatibilityLevel CompatibilityLevel { get; set; } = CodexCompatibilityLevel.Unknown;

    public bool HasCompatibilityBadge => CompatibilityLevel != CodexCompatibilityLevel.Unknown;

    public string CompatibilityBadgeText => CompatibilityLevel switch
    {
        CodexCompatibilityLevel.CodexCompatible => "Codex Verified",
        CodexCompatibilityLevel.PartiallyCompatible => "Partial Compatibility",
        CodexCompatibilityLevel.NotCompatible => "Not Compatible",
        _ => string.Empty
    };

    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush SuccessBrush = new(Windows.UI.Color.FromArgb(255, 16, 124, 65));
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush WarningBrush = new(Windows.UI.Color.FromArgb(255, 216, 59, 1));
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush DangerBrush = new(Windows.UI.Color.FromArgb(255, 168, 0, 0));

    public Microsoft.UI.Xaml.Media.Brush CompatibilityBadgeBackground => CompatibilityLevel switch
    {
        CodexCompatibilityLevel.CodexCompatible => SuccessBrush,
        CodexCompatibilityLevel.PartiallyCompatible => WarningBrush,
        _ => DangerBrush
    };

    public IReadOnlyList<ProviderRoute> Routes => Descriptor?.Routes ?? [];
    public bool CanToggleRoute => Routes.Count > 1;
    public bool CanSwitch => !IsTargetActive && HasSecret;

    public string ActiveBadgeLabel => Loc.BadgeActiveNow;
    public string MissingCredentialLabel => Loc.ApiKeyRequired;
    public string InUseLabel => Loc.InUse;
    public string SwitchLabel => Loc.Switch;
    public string ContinueOnLabel => Loc.ContinueOn;
    public string ContinueOnTooltip => Loc.ContinueOnTooltip;
    public string RefreshLabel => Loc.RefreshAll;
    public string MoreActionsLabel => Loc.MoreActions;
    public string EditLabel => Loc.Rename;
    public string RotateKeyLabel => Loc.RotateKey;
    public string RemoveLabel => Loc.RemoveCredential;
    public string MoveUpLabel => Loc.MoveUp;
    public string MoveDownLabel => Loc.MoveDown;

    public ApiProviderItemViewModel(
        ApiProviderProfile profile,
        ProviderDescriptor? descriptor,
        bool hasSecret = false,
        bool isTargetActive = false)
    {
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
        Descriptor = descriptor;
        HasSecret = hasSecret;
        IsTargetActive = isTargetActive;

        DisplayName = profile.DisplayName;
        BaseUrl = profile.BaseUrl;
        SelectedModel = profile.GetEffectiveSelectedModel() ?? string.Empty;
        KeyPreview = profile.KeyPreview;
        Status = profile.Status;
        RoutePoolLabel = profile.RoutePoolLabel;
        CompatibilityLevel = profile.CompatibilityLevel;

        CatalogProviderName = descriptor?.DisplayName ?? (string.IsNullOrWhiteSpace(profile.CatalogProviderId) ? "Custom Provider" : profile.CatalogProviderId);
        SelectedRoute = ResolveRouteName(profile, descriptor);
        Initials = ComputeInitials(CatalogProviderName, DisplayName);

        ApplyTruthfulCapabilityFacts(descriptor);
        RefreshInventoryPresentation(profile);
    }

    public void UpdateProfile(ApiProviderProfile updated, ProviderDescriptor? descriptor, bool hasSecret, bool isTargetActive)
    {
        Profile = updated ?? throw new ArgumentNullException(nameof(updated));
        Descriptor = descriptor;
        HasSecret = hasSecret;
        IsTargetActive = isTargetActive;

        DisplayName = updated.DisplayName;
        BaseUrl = updated.BaseUrl;
        SelectedModel = updated.GetEffectiveSelectedModel() ?? string.Empty;
        KeyPreview = updated.KeyPreview;
        Status = updated.Status;
        RoutePoolLabel = updated.RoutePoolLabel;
        CompatibilityLevel = updated.CompatibilityLevel;

        CatalogProviderName = descriptor?.DisplayName ?? (string.IsNullOrWhiteSpace(updated.CatalogProviderId) ? "Custom Provider" : updated.CatalogProviderId);
        SelectedRoute = ResolveRouteName(updated, descriptor);
        Initials = ComputeInitials(CatalogProviderName, DisplayName);

        ApplyTruthfulCapabilityFacts(descriptor);
        RefreshInventoryPresentation(updated);
    }

    public void SetDiscoveredModels(IReadOnlyList<string> models)
    {
        if (models.Count > 0 && ModelsReportedCount == 0)
        {
            ModelsReportedCount = models.Count;
            ModelsStatusMessage = $"{models.Count} models available";
        }
    }

    /// <summary>
    /// Applies the one authoritative refresh result. A newer service generation
    /// is the only state allowed to replace a card's previous error text.
    /// </summary>
    public void ApplyRefreshResult(ModelInventoryRefreshResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.RequestGeneration < _latestRefreshGeneration)
        {
            return;
        }

        _latestRefreshGeneration = result.RequestGeneration;

        switch (result.Outcome)
        {
            case ModelInventoryRefreshOutcome.Refreshing:
                IsLoadingModels = true;
                ModelsStatusMessage = result.ProgressMessage ?? "Refreshing models...";
                break;
            case ModelInventoryRefreshOutcome.Succeeded:
                IsLoadingModels = false;
                ModelsReportedCount = result.ModelsReportedCount;
                ModelsEnabledCount = Profile.ModelInventory?.GetEnabledModels().Count() ?? 0;
                LastModelsRefreshAt = result.ObservedAt;
                ModelsStatusMessage = $"{result.ModelsReportedCount} models reported · Updated just now";
                break;
            case ModelInventoryRefreshOutcome.Superseded:
                // The latest generation will publish its own projection. Do not
                // replace a newer success with a stale terminal observation.
                break;
            default:
                IsLoadingModels = false;
                ModelsStatusMessage = result.ErrorMessageSanitized
                    ?? "Model refresh did not return a usable inventory.";
                break;
        }
    }

    private void RefreshInventoryPresentation(ApiProviderProfile profile)
    {
        var inventory = profile.ModelInventory;
        ModelsEnabledCount = inventory?.GetEnabledModels().Count() ?? 0;
        ModelsReportedCount = inventory?.Models.Count(item => item.Availability == ModelAvailability.Reported)
            ?? profile.DiscoveredModels?.Count
            ?? 0;
        LastModelsRefreshAt = inventory?.LastDiscoveryAt;

        if (ModelsReportedCount > 0 && string.IsNullOrWhiteSpace(ModelsStatusMessage))
        {
            ModelsStatusMessage = $"{ModelsReportedCount} models reported";
        }
    }

    private void ApplyTruthfulCapabilityFacts(ProviderDescriptor? descriptor)
    {
        if (descriptor?.Capabilities?.Balance?.Status == CapabilityStatus.Unknown)
        {
            BalanceSummary = "Unknown";
        }
        else if (descriptor?.Capabilities?.Balance?.Status == CapabilityStatus.Unsupported)
        {
            BalanceSummary = "Not supported";
        }

        if (descriptor?.Capabilities?.Usage?.Status == CapabilityStatus.Unknown)
        {
            UsageSummary = "Unknown";
        }
        else if (descriptor?.Capabilities?.Usage?.Status == CapabilityStatus.Unsupported)
        {
            UsageSummary = "Not supported";
        }
    }

    public string RouteTooltip
    {
        get
        {
            if (Descriptor != null && !string.IsNullOrWhiteSpace(Profile.SelectedRouteId))
            {
                var route = Descriptor.Routes.FirstOrDefault(r => string.Equals(r.Id, Profile.SelectedRouteId, StringComparison.OrdinalIgnoreCase));
                if (route != null)
                {
                    var regionPart = !string.IsNullOrWhiteSpace(route.Region) ? $" ({route.Region})" : string.Empty;
                    return CanToggleRoute
                        ? $"Route: {route.DisplayName}{regionPart} - Click to cycle route"
                        : $"Route: {route.DisplayName}{regionPart}";
                }
            }
            return CanToggleRoute ? "Click to switch route" : "Route";
        }
    }

    private static string ResolveRouteName(ApiProviderProfile profile, ProviderDescriptor? descriptor)
    {
        if (descriptor != null && !string.IsNullOrWhiteSpace(profile.SelectedRouteId))
        {
            var route = descriptor.Routes.FirstOrDefault(r => string.Equals(r.Id, profile.SelectedRouteId, StringComparison.OrdinalIgnoreCase));
            if (route != null)
                return route.DisplayName;
        }

        if (descriptor != null && descriptor.Routes.Count > 0)
        {
            var defaultRoute = descriptor.Routes.FirstOrDefault(r => r.IsDefault) ?? descriptor.Routes[0];
            return defaultRoute.DisplayName;
        }

        return !string.IsNullOrWhiteSpace(profile.SelectedRouteId)
            ? profile.SelectedRouteId
            : "Primary";
    }

    private static string ComputeInitials(string providerName, string displayName)
    {
        var src = !string.IsNullOrWhiteSpace(providerName) ? providerName : displayName;
        if (src.StartsWith("Router.Cheap", StringComparison.OrdinalIgnoreCase)) return "RC";
        if (src.StartsWith("OpenRouter", StringComparison.OrdinalIgnoreCase)) return "OR";

        var parts = src.Split([' ', '.', '-', '_'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
            return (char.ToUpperInvariant(parts[0][0]).ToString() + char.ToUpperInvariant(parts[1][0])).Trim();
        if (parts.Length == 1 && parts[0].Length >= 2)
            return parts[0][..2].ToUpperInvariant();
        return "AP";
    }
}

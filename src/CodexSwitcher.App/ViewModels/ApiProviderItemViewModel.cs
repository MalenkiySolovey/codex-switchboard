using CodexSwitcher.App.Localization;
using CodexSwitcher.Core.Catalog;
using CodexSwitcher.Core.Models;
using CodexSwitcher.Core.Support;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CodexSwitcher.App.ViewModels;

/// <summary>
/// Presentation ViewModel for an API Provider card in the UI.
/// Exposes safe key previews, capability tri-state facts, and routing state.
/// </summary>
public sealed partial class ApiProviderItemViewModel : ObservableObject
{
    private static Strings Loc => Strings.Current;

    public ApiProviderProfile Profile { get; private set; }
    public ProviderDescriptor? Descriptor { get; private set; }

    public Guid Id => Profile.Id;
    public string StableCodexProviderId => Profile.StableCodexProviderId;
    public string? CatalogProviderId => Profile.CatalogProviderId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSwitch))]
    public partial bool IsTargetActive { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSwitch))]
    public partial bool HasSecret { get; set; }

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
    public partial string BalanceSummary { get; set; } = "Unknown";

    [ObservableProperty]
    public partial string UsageSummary { get; set; } = "Unknown";

    [ObservableProperty]
    public partial string Initials { get; set; } = "AP";

    public IReadOnlyList<ProviderRoute> Routes => Descriptor?.Routes ?? [];
    public bool CanToggleRoute => Routes.Count > 1;
    public bool CanSwitch => !IsTargetActive && HasSecret;

    public string ActiveBadgeLabel => Loc.BadgeActiveNow;
    public string MissingCredentialLabel => Loc.ApiKeyRequired;
    public string InUseLabel => Loc.InUse;
    public string SwitchLabel => Loc.Switch;
    public string ContinueOnLabel => Loc.ContinueOn;
    public string RefreshLabel => Loc.RefreshAll;
    public string MoreActionsLabel => Loc.MoreActions;
    public string EditLabel => Loc.Rename;
    public string RotateKeyLabel => Loc.RotateKey;
    public string RemoveLabel => Loc.RemoveCredential;

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
        SelectedModel = profile.SelectedModel ?? string.Empty;
        KeyPreview = profile.KeyPreview;
        Status = profile.Status;

        CatalogProviderName = descriptor?.DisplayName ?? (string.IsNullOrWhiteSpace(profile.CatalogProviderId) ? "Custom Provider" : profile.CatalogProviderId);
        SelectedRoute = ResolveRouteName(profile, descriptor);
        Initials = ComputeInitials(CatalogProviderName, DisplayName);

        ApplyTruthfulCapabilityFacts(descriptor);
    }

    public void UpdateProfile(ApiProviderProfile updated, ProviderDescriptor? descriptor, bool hasSecret, bool isTargetActive)
    {
        Profile = updated ?? throw new ArgumentNullException(nameof(updated));
        Descriptor = descriptor;
        HasSecret = hasSecret;
        IsTargetActive = isTargetActive;

        DisplayName = updated.DisplayName;
        BaseUrl = updated.BaseUrl;
        SelectedModel = updated.SelectedModel ?? string.Empty;
        KeyPreview = updated.KeyPreview;
        Status = updated.Status;

        CatalogProviderName = descriptor?.DisplayName ?? (string.IsNullOrWhiteSpace(updated.CatalogProviderId) ? "Custom Provider" : updated.CatalogProviderId);
        SelectedRoute = ResolveRouteName(updated, descriptor);
        Initials = ComputeInitials(CatalogProviderName, DisplayName);

        ApplyTruthfulCapabilityFacts(descriptor);
    }

    public void SetDiscoveredModels(IReadOnlyList<string> models)
    {
        ModelsStatusMessage = $"{models.Count} models available";
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

    private static string ResolveRouteName(ApiProviderProfile profile, ProviderDescriptor? descriptor)
    {
        if (descriptor != null && !string.IsNullOrWhiteSpace(profile.SelectedRouteId))
        {
            var route = descriptor.Routes.FirstOrDefault(r => string.Equals(r.Id, profile.SelectedRouteId, StringComparison.OrdinalIgnoreCase));
            if (route != null)
                return route.DisplayName;
        }

        return string.Equals(profile.SelectedRouteId, "reserve", StringComparison.OrdinalIgnoreCase)
            ? "Reserve"
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

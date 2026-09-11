using CodexSwitcher.App.Features.Accounts;
using CodexSwitcher.App.Localization;
using CodexSwitcher.App.ViewModels;
using CodexSwitcher.Core.Abstractions;
using CodexSwitcher.Core.Models;
using CodexSwitcher.Infra;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CodexSwitcher.App.Shell.Routing;

/// <summary>
/// Presentation coordinator for active routing status.
/// Maps domain <see cref="ICodexActiveTargetResolver"/> results into UI-friendly summary properties.
/// Contains zero config.toml or auth.json mutation logic (pure read-only mapper).
/// </summary>
public sealed partial class ActiveTargetPresentationCoordinator : ObservableObject
{
    private readonly ICodexActiveTargetResolver? _activeTargetResolver;
    private readonly AppPaths? _paths;
    private readonly Strings _loc = Strings.Current;

    [ObservableProperty] public partial string ActiveTargetSummary { get; set; } = string.Empty;
    [ObservableProperty] public partial string ActiveTargetCredentialSlot { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsRoutingActiveToApi { get; set; }
    public ActiveTarget CurrentTarget { get; private set; }

    public ActiveTargetPresentationCoordinator(
        ICodexActiveTargetResolver? activeTargetResolver = null,
        AppPaths? paths = null)
    {
        _activeTargetResolver = activeTargetResolver;
        _paths = paths;
        CurrentTarget = new ActiveTarget.ChatGpt(null, null);
    }

    public ActiveTarget RefreshRouting(ProfileMetadata? activeProfile)
    {
        var configTomlPath = _paths?.Codex.ConfigTomlPath
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "config.toml");

        var activeTarget = _activeTargetResolver?.ResolveActiveTarget(configTomlPath, activeProfile?.Id, activeProfile?.AccountEmail)
            ?? new ActiveTarget.ChatGpt(activeProfile?.Id, activeProfile?.AccountEmail);

        CurrentTarget = activeTarget;

        if (activeTarget is ActiveTarget.Api apiTarget)
        {
            IsRoutingActiveToApi = true;
            ActiveTargetSummary = $"{apiTarget.Profile.Nickname} ({apiTarget.Profile.SelectedModel})";
            ActiveTargetCredentialSlot = activeProfile?.DisplayName ?? _loc.NoManaged;
        }
        else
        {
            IsRoutingActiveToApi = false;
            ActiveTargetSummary = activeProfile?.DisplayName ?? _loc.NoManaged;
            ActiveTargetCredentialSlot = activeProfile?.DisplayName ?? _loc.NoManaged;
        }

        return activeTarget;
    }
}

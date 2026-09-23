using System;
using CodexSwitcher.Core.Providers.Models;

namespace CodexSwitcher.Core.Routing.Models;

public enum TargetKind
{
    ChatGptAccount = 1,
    ApiProvider = 2,
}

public enum CatalogMode
{
    BuiltInOpenAi = 1,
    ManagedApiProfileCatalog = 2,
}

/// <summary>
/// Immutable semantic definition of a complete desired Codex runtime environment.
/// Combines provider, model, catalog mode, model overrides, and tool policy
/// into a single atomic target specification.
/// </summary>
public sealed record CodexTargetEnvironment(
    TargetKind TargetKind,
    string ProviderId,
    string? SelectedModel,
    CatalogMode CatalogMode,
    string? CatalogPath = null,
    CodexModelOverrides? ModelOverrides = null,
    EffectiveToolPolicy? ToolPolicy = null,
    bool RequiresRuntimeRestart = false,
    string? SourceProfileId = null)
{
    /// <summary>
    /// Creates a target environment for an official ChatGPT / OpenAI account.
    /// Invariant: CatalogMode is strictly BuiltInOpenAi with no custom catalog override.
    /// </summary>
    public static CodexTargetEnvironment ForChatGpt(
        string? sourceProfileId = null,
        string? preferredModel = null,
        bool requiresRuntimeRestart = true)
    {
        return new CodexTargetEnvironment(
            TargetKind: TargetKind.ChatGptAccount,
            ProviderId: "openai",
            SelectedModel: preferredModel,
            CatalogMode: CatalogMode.BuiltInOpenAi,
            CatalogPath: null,
            ModelOverrides: null,
            ToolPolicy: null,
            RequiresRuntimeRestart: requiresRuntimeRestart,
            SourceProfileId: sourceProfileId);
    }

    /// <summary>
    /// Creates a target environment for a configured API provider profile.
    /// Invariant: Scoped to profile-specific enabled catalog and exact model slug.
    /// </summary>
    public static CodexTargetEnvironment ForApiProvider(
        ApiProviderProfile profile,
        string exactModel,
        string? catalogPath,
        EffectiveToolPolicy? toolPolicy = null,
        bool requiresRuntimeRestart = true)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(exactModel);

        return new CodexTargetEnvironment(
            TargetKind: TargetKind.ApiProvider,
            ProviderId: profile.StableCodexProviderId,
            SelectedModel: exactModel,
            CatalogMode: !string.IsNullOrWhiteSpace(catalogPath)
                ? CatalogMode.ManagedApiProfileCatalog
                : CatalogMode.BuiltInOpenAi,
            CatalogPath: catalogPath,
            ModelOverrides: profile.ModelOverrides,
            ToolPolicy: toolPolicy,
            RequiresRuntimeRestart: requiresRuntimeRestart,
            SourceProfileId: profile.Id.ToString("D"));
    }
}

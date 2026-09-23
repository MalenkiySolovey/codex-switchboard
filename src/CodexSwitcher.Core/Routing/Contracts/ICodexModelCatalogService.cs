using System;
using CodexSwitcher.Core.Providers.Models;

namespace CodexSwitcher.Core.Routing.Contracts;

/// <summary>
/// Service responsible for producing version-compatible custom model catalog files
/// for external models whose context window exceeds Codex's fallback ceiling (~272k tokens)
/// or requiring route-specific tool capability adaptations.
/// Catalogs are strictly profile-scoped and contain ONLY models enabled for the target profile.
/// </summary>
public interface ICodexModelCatalogService
{
    /// <summary>
    /// Ensures a valid, version-compatible minimal Codex model catalog exists for the specified model slug.
    /// </summary>
    string? EnsureModelCatalog(
        string modelSlug,
        long? contextWindowTokens,
        CodexModelOverrides? modelOverrides = null,
        EffectiveToolPolicy? toolPolicy = null);

    /// <summary>
    /// Ensures a valid, profile-scoped model catalog exists under
    /// %LOCALAPPDATA%\CodexSwitchboard\catalogs\&lt;profile-guid&gt;\&lt;runtime-fingerprint&gt;\models.json
    /// containing strictly the enabled models for that profile.
    /// </summary>
    string? EnsureProfileModelCatalog(
        ApiProviderProfile profile,
        string? modelSlug = null,
        long? contextWindowTokens = null,
        CodexModelOverrides? modelOverrides = null,
        EffectiveToolPolicy? toolPolicy = null);
}

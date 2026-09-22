using System;
using CodexSwitcher.Core.Providers.Models;

namespace CodexSwitcher.Core.Routing.Contracts;

/// <summary>
/// Service responsible for producing version-compatible custom model catalog files
/// for external models whose context window exceeds Codex's fallback ceiling (~272k tokens)
/// or requiring route-specific tool capability adaptations.
/// </summary>
public interface ICodexModelCatalogService
{
    /// <summary>
    /// Ensures a valid, version-compatible minimal Codex model catalog exists for the specified model slug
    /// when custom context window tokens exceed the Codex fallback ceiling (272,000 tokens), custom model overrides are supplied,
    /// or route tool policy requires disabling unsupported tools (e.g. apply_patch, tool_search).
    /// Returns the absolute path to the generated catalog file, or null if no catalog is needed.
    /// </summary>
    string? EnsureModelCatalog(
        string modelSlug,
        long? contextWindowTokens,
        CodexModelOverrides? modelOverrides = null,
        EffectiveToolPolicy? toolPolicy = null);
}

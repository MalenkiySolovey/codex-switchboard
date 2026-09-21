using System;

namespace CodexSwitcher.Core.Routing.Contracts;

/// <summary>
/// Service responsible for producing version-compatible custom model catalog files
/// for external models whose context window exceeds Codex's fallback ceiling (~272k tokens).
/// </summary>
public interface ICodexModelCatalogService
{
    /// <summary>
    /// Ensures a valid, version-compatible minimal Codex model catalog exists for the specified model slug
    /// when custom context window tokens exceed the Codex fallback ceiling (272,000 tokens).
    /// Returns the absolute path to the generated catalog file, or null if no catalog is needed.
    /// </summary>
    string? EnsureModelCatalog(string modelSlug, long? contextWindowTokens);
}

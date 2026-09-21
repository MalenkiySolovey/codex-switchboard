namespace CodexSwitcher.Core.Providers.Models;

/// <summary>
/// Semantic intent of an API provider persistence operation.
/// Used by <see cref="CodexSwitcher.Core.Providers.Contracts.IApiProviderStore"/> to enforce
/// safety invariants against silent destructive truncation.
/// </summary>
public enum ApiProviderSaveIntent
{
    /// <summary>
    /// Routine metadata update (reordering, endpoint toggle, model selection, status update, etc.).
    /// Strictly forbidden from reducing the profile count relative to disk.
    /// </summary>
    NormalUpdate = 0,

    /// <summary>
    /// Explicit user action to remove an API provider profile.
    /// Permits the profile count to decrease.
    /// </summary>
    ExplicitDelete = 1,

    /// <summary>
    /// Intentional user import or full replacement/restoration of profiles.
    /// </summary>
    BatchImport = 2,
}

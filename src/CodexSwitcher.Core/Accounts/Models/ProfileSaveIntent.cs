namespace CodexSwitcher.Core.Accounts.Models;

/// <summary>
/// Semantic intent of a profile persistence operation.
/// Used by <see cref="CodexSwitcher.Core.Accounts.Contracts.IProfileStore"/> to enforce
/// safety invariants against silent destructive truncation.
/// </summary>
public enum ProfileSaveIntent
{
    /// <summary>
    /// Routine metadata update (reconciliation, quota, rename, usage, mark-used, etc.).
    /// Strictly forbidden from reducing the profile count relative to disk.
    /// </summary>
    NormalUpdate = 0,

    /// <summary>
    /// Explicit user action to remove an account profile.
    /// Permits the profile count to decrease.
    /// </summary>
    ExplicitDelete = 1,

    /// <summary>
    /// Intentional user import or full replacement/restoration of profiles.
    /// </summary>
    ImportReplace = 2,
}

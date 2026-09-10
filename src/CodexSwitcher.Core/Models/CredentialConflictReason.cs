namespace CodexSwitcher.Core.Models;

/// <summary>
/// Specifies the specific reason a profile credential conflict was detected.
/// </summary>
public enum CredentialConflictReason
{
    /// <summary>No conflict detected.</summary>
    None = 0,

    /// <summary>An inactive profile's vault blob was concurrently modified during token rotation writeback.</summary>
    InactiveGenerationChanged = 1,

    /// <summary>The active profile credentials rotated in the sandbox; writeback to %USERPROFILE%\.codex\auth.json was suppressed.</summary>
    ActiveCredentialMutation = 2,

    /// <summary>A generic or legacy conflict state.</summary>
    Unknown = 3,
}

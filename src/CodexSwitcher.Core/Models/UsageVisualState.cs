namespace CodexSwitcher.Core.Models;

/// <summary>
/// The 9 visual presentation states for account quota and rate-limit display.
/// </summary>
public enum UsageVisualState
{
    /// <summary>Fresh authoritative data fetched from the live Codex app-server in the current session.</summary>
    Fresh = 0,

    /// <summary>Restored from cache on startup or stale after TTL ("STALE DATA" badge).</summary>
    StaleCache = 1,

    /// <summary>In-flight refresh operation in progress ("Refreshing..." badge).</summary>
    Refreshing = 2,

    /// <summary>Account has no cached usage data and has not been queried yet.</summary>
    NeverLoaded = 3,

    /// <summary>Account has reached rate limit or 100% quota exhausted ("Rate limited" badge).</summary>
    RateLimited = 4,

    /// <summary>Authentication expired or login required (notice banner).</summary>
    AuthRequired = 5,

    /// <summary>Token or API key type does not support rate-limit queries (notice banner).</summary>
    UnsupportedAccountType = 6,

    /// <summary>Codex app-server process unavailable, backing off, or error (notice banner).</summary>
    ProcessDown = 7,

    /// <summary>Credential conflict: active account does not match vault profile (notice banner).</summary>
    CredentialConflict = 8,
}

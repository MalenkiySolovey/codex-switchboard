namespace CodexSwitcher.Core.Models;

/// <summary>
/// Granular availability state for an account's historical token activity.
/// Distinguishes between data not loaded yet, unsupported runtime, temporary failure, and available data.
/// </summary>
public enum AccountActivityAvailability
{
    /// <summary>Activity has not been queried yet (e.g. cold start).</summary>
    Unknown = 0,

    /// <summary>Query is actively in-flight.</summary>
    Loading = 1,

    /// <summary>Activity snapshot is available and fresh or restored from cache.</summary>
    Available = 2,

    /// <summary>Query succeeded but the server reported no activity data for this account.</summary>
    NotReported = 3,

    /// <summary>The selected Codex app-server runtime does not support account/usage/read.</summary>
    UnsupportedRuntime = 4,

    /// <summary>Temporary failure (e.g. transient timeout, server 500/overload) while quota succeeded.</summary>
    TemporarilyUnavailable = 5,
}

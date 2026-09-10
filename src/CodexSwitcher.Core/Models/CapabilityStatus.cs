namespace CodexSwitcher.Core.Models;

/// <summary>
/// Status of a specific Codex app-server capability or method.
/// </summary>
public enum CapabilityStatus
{
    /// <summary>Capability has not been probed or checked yet.</summary>
    Unknown = 0,

    /// <summary>Capability is verified as supported by the runtime.</summary>
    Supported = 1,

    /// <summary>Capability is verified as unsupported by the runtime (e.g. unknown variant or missing from schema).</summary>
    Unsupported = 2,
}

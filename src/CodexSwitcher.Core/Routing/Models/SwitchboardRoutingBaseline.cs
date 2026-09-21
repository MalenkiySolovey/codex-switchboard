using System.Collections.Generic;

namespace CodexSwitcher.Core.Routing.Models;

/// <summary>
/// State tracking for Switchboard-owned root keys in config.toml.
/// Allows Switchboard to restore user-defined baselines and cleanly remove
/// provider-specific overrides (e.g. context window, model catalog) when returning
/// to OpenAI or switching between API targets.
/// </summary>
public sealed class SwitchboardRoutingBaseline
{
    /// <summary>Active provider ID currently routed to, or null if OpenAI.</summary>
    public string? ActiveProviderId { get; set; }

    /// <summary>Root keys currently injected and managed by Switchboard in config.toml.</summary>
    public List<string> ManagedRootKeys { get; set; } = [];

    /// <summary>
    /// User's baseline raw line values prior to Switchboard management.
    /// If a key maps to null, it means the key did not exist prior to Switchboard.
    /// </summary>
    public Dictionary<string, string?> BaselineValues { get; set; } = [];
}

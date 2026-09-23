using System;
using System.Collections.Generic;

namespace CodexSwitcher.Core.Providers.Models;

public enum ModelDiscoverySource
{
    CatalogKnown = 0,
    Endpoint = 1,
    Manual = 2,
}

public enum ModelDiscoveryStatus
{
    Unknown = 0,
    Discovered = 1,
    Manual = 2,
    Mixed = 3,
}

/// <summary>
/// Represents a single model item in an API provider's inventory.
/// </summary>
public sealed class ApiProviderModelItem
{
    public string Slug { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public bool Enabled { get; set; } = true;
    public ModelDiscoverySource DiscoverySource { get; set; } = ModelDiscoverySource.CatalogKnown;
    public long? ContextWindow { get; set; }
    public string? ContextEvidence { get; set; }
    public ModelCapabilities? Capabilities { get; set; }
}

/// <summary>
/// Model inventory associated with an <see cref="ApiProviderProfile"/>.
/// Stores discovered and manually registered models for this profile.
/// Strictly persisted in Switchboard app data, NOT in Codex config.toml.
/// </summary>
public sealed class ApiProviderModelInventory
{
    public List<ApiProviderModelItem> Models { get; set; } = new();
    public string? SelectedModel { get; set; }
    public ModelDiscoveryStatus DiscoveryStatus { get; set; } = ModelDiscoveryStatus.Unknown;
    public DateTimeOffset? LastDiscoveryAt { get; set; }

    /// <summary>
    /// Gets all models currently enabled for active routing catalog generation.
    /// </summary>
    public IEnumerable<ApiProviderModelItem> GetEnabledModels()
    {
        return Models.FindAll(m => m.Enabled);
    }
}

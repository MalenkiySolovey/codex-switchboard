using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using CodexSwitcher.Core.Routing.Models;

namespace CodexSwitcher.Core.Providers.Models;

public enum ModelDiscoverySource
{
    CatalogKnown = 0,
    Discovered = 1,
    Manual = 2,
    Mixed = 3,
}

public enum ModelAvailability
{
    Unknown = 0,
    Reported = 1,
    NotReported = 2,
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
    public ModelAvailability Availability { get; set; } = ModelAvailability.Unknown;
    public DateTimeOffset? LastSeenAt { get; set; }
    public long? ContextWindow { get; set; }
    public string? ContextEvidence { get; set; }
    public ModelCapabilities? Capabilities { get; set; }
    public CodexModelOverrides? UserOverrides { get; set; }
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

    /// <summary>
    /// Merges an incoming list of discovered model slugs from the API provider.
    /// Preserves existing manual models and existing configurations, marking missing discovered models as NotReported.
    /// </summary>
    public void MergeDiscoveredModels(IEnumerable<string>? discoveredSlugs)
    {
        if (discoveredSlugs == null) return;
        DiscoveryStatus = ModelDiscoveryStatus.Discovered;
        LastDiscoveryAt = DateTimeOffset.UtcNow;

        var existingMap = Models.ToDictionary(m => m.Slug, StringComparer.OrdinalIgnoreCase);
        var discoveredSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var slug in discoveredSlugs)
        {
            if (string.IsNullOrWhiteSpace(slug)) continue;
            var cleanSlug = slug.Trim();
            discoveredSet.Add(cleanSlug);

            if (existingMap.TryGetValue(cleanSlug, out var existing))
            {
                existing.Availability = ModelAvailability.Reported;
                existing.LastSeenAt = DateTimeOffset.UtcNow;
                if (existing.DiscoverySource == ModelDiscoverySource.Manual)
                {
                    existing.DiscoverySource = ModelDiscoverySource.Mixed;
                }
            }
            else
            {
                Models.Add(new ApiProviderModelItem
                {
                    Slug = cleanSlug,
                    DisplayName = cleanSlug,
                    Enabled = true,
                    DiscoverySource = ModelDiscoverySource.Discovered,
                    Availability = ModelAvailability.Reported,
                    LastSeenAt = DateTimeOffset.UtcNow
                });
            }
        }

        // Mark previously discovered models that were not reported in this pass
        foreach (var m in Models)
        {
            if (!discoveredSet.Contains(m.Slug) && m.DiscoverySource != ModelDiscoverySource.Manual)
            {
                m.Availability = ModelAvailability.NotReported;
            }
        }
    }

    /// <summary>
    /// Ensures that the profile's active or selected model exists as an enabled item in the inventory.
    /// Performs backward-compatible migration for existing profiles that lack an explicit inventory.
    /// </summary>
    public void EnsureSelectedModelMigrated(
        string? selectedModel,
        string? nickname = null,
        long? contextWindow = null,
        CodexModelOverrides? overrides = null)
    {
        if (string.IsNullOrWhiteSpace(selectedModel)) return;

        SelectedModel ??= selectedModel;

        var existing = Models.FirstOrDefault(m => string.Equals(m.Slug, selectedModel, StringComparison.OrdinalIgnoreCase));
        if (existing == null)
        {
            Models.Insert(0, new ApiProviderModelItem
            {
                Slug = selectedModel,
                DisplayName = nickname ?? selectedModel,
                Enabled = true,
                DiscoverySource = ModelDiscoverySource.Manual,
                Availability = ModelAvailability.Reported,
                ContextWindow = contextWindow,
                UserOverrides = overrides != null ? new CodexModelOverrides
                {
                    ContextWindowTokens = overrides.ContextWindowTokens,
                    ReasoningEffort = overrides.ReasoningEffort,
                    Verbosity = overrides.Verbosity,
                } : null,
                LastSeenAt = DateTimeOffset.UtcNow
            });
        }
        else
        {
            existing.Enabled = true;
            if (existing.ContextWindow == null && contextWindow.HasValue)
            {
                existing.ContextWindow = contextWindow;
            }
            if (existing.UserOverrides == null && overrides != null)
            {
                existing.UserOverrides = new CodexModelOverrides
                {
                    ContextWindowTokens = overrides.ContextWindowTokens,
                    ReasoningEffort = overrides.ReasoningEffort,
                    Verbosity = overrides.Verbosity,
                };
            }
        }
    }

    /// <summary>
    /// Computes a deterministic 16-character SHA-256 fingerprint representing the current set of enabled models
    /// and their effective overrides.
    /// </summary>
    public string ComputeInventoryHash()
    {
        var sorted = Models
            .Where(m => m.Enabled)
            .OrderBy(m => m.Slug, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (sorted.Count == 0 && !string.IsNullOrWhiteSpace(SelectedModel))
        {
            var bytes = Encoding.UTF8.GetBytes(SelectedModel.Trim().ToLowerInvariant());
            return Convert.ToHexString(SHA256.HashData(bytes))[..16].ToLowerInvariant();
        }

        var sb = new StringBuilder();
        foreach (var m in sorted)
        {
            sb.Append(m.Slug).Append('|')
              .Append(m.DisplayName ?? string.Empty).Append('|')
              .Append(m.ContextWindow?.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append('|')
              .Append(m.UserOverrides?.ContextWindowTokens?.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append('|')
              .Append(m.UserOverrides?.ReasoningEffort.ToString() ?? string.Empty).Append('|')
              .Append(m.UserOverrides?.Verbosity.ToString() ?? string.Empty).Append('|')
              .Append(m.Capabilities?.Vision.State.ToString() ?? string.Empty).Append(';');
        }

        var raw = sb.ToString();
        if (string.IsNullOrEmpty(raw))
        {
            return "empty";
        }

        var hashBytes = Encoding.UTF8.GetBytes(raw);
        return Convert.ToHexString(SHA256.HashData(hashBytes))[..16].ToLowerInvariant();
    }
}

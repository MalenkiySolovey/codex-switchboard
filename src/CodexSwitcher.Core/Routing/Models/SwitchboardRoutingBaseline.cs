using System;
using System.Collections.Generic;

namespace CodexSwitcher.Core.Routing.Models;

/// <summary>
/// Fine-grained provenance record for a configuration key managed by Switchboard.
/// Tracks whether the user had a baseline value prior to Switchboard management,
/// what value Switchboard last applied, and the owning target/profile identity.
/// </summary>
public sealed class ManagedKeyProvenance
{
    public string Key { get; set; } = string.Empty;
    public bool BaselineWasPresent { get; set; }
    public string? BaselineValue { get; set; }
    public bool LastAppliedWasPresent { get; set; }
    public string? LastAppliedValue { get; set; }
    public string? OwnerTargetKind { get; set; }
    public string? OwnerProfileId { get; set; }
    public long Generation { get; set; }
}

/// <summary>
/// State tracking for Switchboard-owned root keys and feature table keys in config.toml.
/// Allows Switchboard to restore user-defined baselines and cleanly remove
/// provider-specific overrides (e.g. context window, model catalog, web_search, tool_search)
/// when returning to OpenAI or switching between API targets.
/// Also stores the last applied target environment, generation counter, and applied values
/// to detect external user edits without magic-value heuristics.
/// </summary>
public sealed class SwitchboardRoutingBaseline
{
    /// <summary>Active provider ID currently routed to, or null if OpenAI.</summary>
    public string? ActiveProviderId { get; set; }

    /// <summary>Last applied target kind (ChatGptAccount or ApiProvider).</summary>
    public string? LastAppliedTargetKind { get; set; }

    /// <summary>Last applied profile ID (if API provider).</summary>
    public string? LastAppliedProfileId { get; set; }

    /// <summary>Timestamp of last applied target projection.</summary>
    public DateTimeOffset? LastAppliedAt { get; set; }

    /// <summary>Monotonically increasing generation counter for target applications.</summary>
    public long Generation { get; set; }

    /// <summary>Config fingerprint after last target application.</summary>
    public string? LastAppliedConfigFingerprint { get; set; }

    /// <summary>Detailed provenance tracking for root keys managed by Switchboard.</summary>
    public Dictionary<string, ManagedKeyProvenance> ManagedKeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Detailed provenance tracking for [features] table keys managed by Switchboard.</summary>
    public Dictionary<string, ManagedKeyProvenance> ManagedFeatures { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Root keys currently injected and managed by Switchboard in config.toml.</summary>
    public List<string> ManagedRootKeys { get; set; } = [];

    /// <summary>
    /// User's baseline raw line values prior to Switchboard management.
    /// If a key maps to null, it means the key did not exist prior to Switchboard.
    /// </summary>
    public Dictionary<string, string?> BaselineValues { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Feature table keys currently injected and managed by Switchboard under [features] in config.toml.</summary>
    public List<string> ManagedFeatureKeys { get; set; } = [];

    /// <summary>
    /// User's baseline raw feature values prior to Switchboard management.
    /// If a key maps to null, it means the key did not exist prior to Switchboard.
    /// </summary>
    public Dictionary<string, string?> BaselineFeatureValues { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Last applied raw values by Switchboard for managed keys.</summary>
    public Dictionary<string, string?> LastAppliedValues { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Semantic owner mapping for tracked keys (e.g. "Switchboard", "User").</summary>
    public Dictionary<string, string> SemanticOwners { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Synchronizes provenance records into legacy flat collections for backward compatibility.
    /// </summary>
    public void SyncLegacyCollections()
    {
        ManagedRootKeys = [.. ManagedKeys.Keys];
        BaselineValues = new(StringComparer.OrdinalIgnoreCase);
        LastAppliedValues = new(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, prov) in ManagedKeys)
        {
            if (prov.BaselineWasPresent)
            {
                BaselineValues[k] = prov.BaselineValue;
            }
            if (prov.LastAppliedWasPresent)
            {
                LastAppliedValues[k] = prov.LastAppliedValue;
            }
        }

        ManagedFeatureKeys = [.. ManagedFeatures.Keys];
        BaselineFeatureValues = new(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, prov) in ManagedFeatures)
        {
            if (prov.BaselineWasPresent)
            {
                BaselineFeatureValues[k] = prov.BaselineValue;
            }
        }
    }

    /// <summary>
    /// Detects and purges poisoned baselines where third-party API/Grok values
    /// were erroneously recorded into BaselineValues as user defaults during the legacy incident.
    /// Uses strictly physical evidence: ONLY purges if model_catalog_json in baseline
    /// proves Switchboard catalog was recorded as user baseline.
    /// Never purges user-owned values (500000, xhigh, custom models) in absence of catalog evidence.
    /// </summary>
    public bool PurgePoisonedBaselines(Func<string, bool>? isKnownSwitchboardModel = null)
    {
        var modified = false;

        // Evidence requirement: catalog path pointing to Switchboard-managed catalogs
        var hasSwitchboardCatalog = false;
        if (BaselineValues.TryGetValue("model_catalog_json", out var catVal) &&
            !string.IsNullOrWhiteSpace(catVal) &&
            (catVal.Contains("CodexSwitchboard", StringComparison.OrdinalIgnoreCase) ||
             catVal.Contains("catalog-grok", StringComparison.OrdinalIgnoreCase)))
        {
            hasSwitchboardCatalog = true;
            BaselineValues.Remove("model_catalog_json");
            ManagedKeys.Remove("model_catalog_json");
            modified = true;
        }

        // Only when Switchboard catalog contamination is proven do we clean associated contaminated baseline items
        if (hasSwitchboardCatalog)
        {
            if (BaselineValues.TryGetValue("model", out var modelVal) && !string.IsNullOrWhiteSpace(modelVal))
            {
                var unquoted = modelVal.Trim().Trim('"');
                if (isKnownSwitchboardModel?.Invoke(unquoted) == true ||
                    unquoted.StartsWith("deepseek-", StringComparison.OrdinalIgnoreCase) ||
                    unquoted.StartsWith("grok-", StringComparison.OrdinalIgnoreCase))
                {
                    BaselineValues.Remove("model");
                    ManagedKeys.Remove("model");
                    modified = true;
                }
            }

            if (BaselineValues.Remove("model_context_window"))
            {
                ManagedKeys.Remove("model_context_window");
                modified = true;
            }

            if (BaselineValues.Remove("model_reasoning_effort"))
            {
                ManagedKeys.Remove("model_reasoning_effort");
                modified = true;
            }
        }

        return modified;
    }
}

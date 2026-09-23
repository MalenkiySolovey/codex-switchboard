using System;
using System.Collections.Generic;
using CodexSwitcher.Core.Common.Enums;
using CodexSwitcher.Core.Providers.Models;
using CodexSwitcher.Core.Routing.Models;

namespace CodexSwitcher.Core.Routing.Services;

/// <summary>
/// Result of projecting a <see cref="CodexTargetEnvironment"/> onto atomic config mutations.
/// </summary>
public sealed record TargetProjectionPlan(
    Dictionary<string, string> RootKeysToSet,
    HashSet<string> RootKeysToRemove,
    Dictionary<string, string> FeatureKeysToSet,
    HashSet<string> FeatureKeysToRemove,
    List<string> ExternalEditConflicts);

/// <summary>
/// Single semantic owner responsible for projecting a <see cref="CodexTargetEnvironment"/>
/// into atomic configuration mutations for Codex's config.toml.
/// Enforces strict separation between USER-OWNED and SWITCHBOARD-OWNED keys.
/// </summary>
public static class CodexTargetEnvironmentProjector
{
    public static readonly IReadOnlyList<string> KnownManagedRootKeys = new[]
    {
        "model_provider",
        "model",
        "model_catalog_json",
        "model_context_window",
        "model_auto_compact_token_limit",
        "model_auto_compact_token_limit_scope",
        "model_reasoning_effort",
        "model_reasoning_summary",
        "model_verbosity",
        "tool_output_token_limit",
        "web_search"
    };

    public static readonly IReadOnlyList<string> KnownManagedFeatureKeys = new[]
    {
        "tool_search",
        "multi_agent"
    };

    public static TargetProjectionPlan Project(
        CodexTargetEnvironment target,
        IReadOnlyDictionary<string, string?> currentRawConfigKeys,
        SwitchboardRoutingBaseline ledger,
        bool failOnExternalEdit = false)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(currentRawConfigKeys);
        ArgumentNullException.ThrowIfNull(ledger);

        var managedKeys = ledger.ManagedKeys ?? new(StringComparer.OrdinalIgnoreCase);
        var managedFeatures = ledger.ManagedFeatures ?? new(StringComparer.OrdinalIgnoreCase);
        var baselineValues = ledger.BaselineValues ?? new(StringComparer.OrdinalIgnoreCase);
        var baselineFeatureValues = ledger.BaselineFeatureValues ?? new(StringComparer.OrdinalIgnoreCase);
        var lastAppliedValues = ledger.LastAppliedValues ?? new(StringComparer.OrdinalIgnoreCase);

        var rootToSet = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var rootToRemove = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var featToSet = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var featToRemove = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var conflicts = new List<string>();

        // 1. External edit conflict detection
        if (managedKeys.Count > 0)
        {
            foreach (var (key, prov) in managedKeys)
            {
                if (prov != null && prov.LastAppliedWasPresent && prov.LastAppliedValue is not null)
                {
                    if (currentRawConfigKeys.TryGetValue(key, out var currentVal))
                    {
                        var normCurrent = NormalizeValue(currentVal);
                        var normLast = NormalizeValue(prov.LastAppliedValue);
                        if (!string.Equals(normCurrent, normLast, StringComparison.Ordinal))
                        {
                            conflicts.Add($"Key '{key}' was modified externally (Current: {normCurrent}, Last Applied: {normLast})");
                        }
                    }
                    else
                    {
                        conflicts.Add($"Key '{key}' was deleted externally (Last Applied: {prov.LastAppliedValue})");
                    }
                }
            }
        }
        else if (lastAppliedValues.Count > 0)
        {
            foreach (var (key, lastVal) in lastAppliedValues)
            {
                if (currentRawConfigKeys.TryGetValue(key, out var currentVal))
                {
                    var normCurrent = NormalizeValue(currentVal);
                    var normLast = NormalizeValue(lastVal);
                    if (!string.Equals(normCurrent, normLast, StringComparison.Ordinal))
                    {
                        conflicts.Add($"Key '{key}' was modified externally (Current: {normCurrent}, Last Applied: {normLast})");
                    }
                }
                else if (lastVal is not null)
                {
                    conflicts.Add($"Key '{key}' was deleted externally (Last Applied: {lastVal})");
                }
            }
        }

        if (failOnExternalEdit && conflicts.Count > 0)
        {
            throw new InvalidOperationException($"External configuration conflict detected: {string.Join("; ", conflicts)}");
        }

        // 2. Project target environment
        if (target.TargetKind == TargetKind.ChatGptAccount)
        {
            // Requirement: ChatGPT account target
            rootToSet["model_provider"] = "\"openai\"";

            // If a specific preferred model was requested, set it;
            // else if user had a valid baseline model prior to Switchboard management, restore it;
            // else REMOVE model key so Codex uses official server default.
            if (!string.IsNullOrWhiteSpace(target.SelectedModel))
            {
                rootToSet["model"] = $"\"{target.SelectedModel}\"";
            }
            else if (managedKeys.TryGetValue("model", out var modelProv) && modelProv is not null)
            {
                if (modelProv.BaselineWasPresent && !string.IsNullOrWhiteSpace(modelProv.BaselineValue))
                {
                    rootToSet["model"] = modelProv.BaselineValue.StartsWith('"') ? modelProv.BaselineValue : $"\"{modelProv.BaselineValue}\"";
                }
                else
                {
                    rootToRemove.Add("model");
                }
            }
            else if (baselineValues.TryGetValue("model", out var userModel) && !string.IsNullOrWhiteSpace(userModel))
            {
                rootToSet["model"] = userModel.StartsWith('"') ? userModel : $"\"{userModel}\"";
            }
            else
            {
                rootToRemove.Add("model");
            }

            // model_catalog_json is strictly ABSENT for ChatGPT
            rootToRemove.Add("model_catalog_json");

            // Restore or remove other Switchboard-managed root keys purely by provenance
            foreach (var k in KnownManagedRootKeys)
            {
                if (k.Equals("model_provider", StringComparison.OrdinalIgnoreCase) ||
                    k.Equals("model", StringComparison.OrdinalIgnoreCase) ||
                    k.Equals("model_catalog_json", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (managedKeys.TryGetValue(k, out var prov) && prov is not null)
                {
                    if (prov.BaselineWasPresent && !string.IsNullOrWhiteSpace(prov.BaselineValue))
                    {
                        rootToSet[k] = prov.BaselineValue;
                    }
                    else
                    {
                        rootToRemove.Add(k);
                    }
                }
                else if (baselineValues.TryGetValue(k, out var userBaseline) && !string.IsNullOrWhiteSpace(userBaseline))
                {
                    rootToSet[k] = userBaseline;
                }
                else
                {
                    rootToRemove.Add(k);
                }
            }

            // Restore or remove tool policy feature keys purely by provenance
            foreach (var fk in KnownManagedFeatureKeys)
            {
                if (managedFeatures.TryGetValue(fk, out var fProv) && fProv is not null)
                {
                    if (fProv.BaselineWasPresent && !string.IsNullOrWhiteSpace(fProv.BaselineValue))
                    {
                        featToSet[fk] = fProv.BaselineValue;
                    }
                    else
                    {
                        featToRemove.Add(fk);
                    }
                }
                else if (baselineFeatureValues.TryGetValue(fk, out var userFeat) && !string.IsNullOrWhiteSpace(userFeat))
                {
                    featToSet[fk] = userFeat;
                }
                else
                {
                    featToRemove.Add(fk);
                }
            }
        }
        else if (target.TargetKind == TargetKind.ApiProvider)
        {
            // Requirement 10: API Target
            rootToSet["model_provider"] = $"\"{target.ProviderId}\"";

            if (!string.IsNullOrWhiteSpace(target.SelectedModel))
            {
                rootToSet["model"] = $"\"{target.SelectedModel}\"";
            }

            // Requirement 11: Profile-scoped model catalog
            if (!string.IsNullOrWhiteSpace(target.CatalogPath))
            {
                var escapedPath = target.CatalogPath.Replace(@"\", @"\\");
                rootToSet["model_catalog_json"] = $"\"{escapedPath}\"";
            }
            else
            {
                rootToRemove.Add("model_catalog_json");
            }

            // Model overrides
            var overrides = target.ModelOverrides;
            if (overrides?.ContextWindowTokens.HasValue == true && overrides.ContextWindowTokens.Value > 0)
            {
                rootToSet["model_context_window"] = overrides.ContextWindowTokens.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            else
            {
                rootToRemove.Add("model_context_window");
            }

            if (overrides?.AutoCompactTokenLimit.HasValue == true && overrides.AutoCompactTokenLimit.Value > 0)
            {
                rootToSet["model_auto_compact_token_limit"] = overrides.AutoCompactTokenLimit.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            else
            {
                rootToRemove.Add("model_auto_compact_token_limit");
            }

            if (overrides?.AutoCompactTokenLimitScope == CompactLimitScope.Total)
            {
                rootToSet["model_auto_compact_token_limit_scope"] = "\"total\"";
            }
            else if (overrides?.AutoCompactTokenLimitScope == CompactLimitScope.BodyAfterPrefix)
            {
                rootToSet["model_auto_compact_token_limit_scope"] = "\"body_after_prefix\"";
            }
            else
            {
                rootToRemove.Add("model_auto_compact_token_limit_scope");
            }

            if (overrides?.ReasoningEffort != null && overrides.ReasoningEffort != CodexReasoningEffort.Default)
            {
                rootToSet["model_reasoning_effort"] = $"\"{overrides.ReasoningEffort.ToString().ToLowerInvariant()}\"";
            }
            else
            {
                rootToRemove.Add("model_reasoning_effort");
            }

            if (overrides?.ReasoningSummary != null && overrides.ReasoningSummary != CodexReasoningSummary.Default)
            {
                rootToSet["model_reasoning_summary"] = $"\"{overrides.ReasoningSummary.ToString().ToLowerInvariant()}\"";
            }
            else
            {
                rootToRemove.Add("model_reasoning_summary");
            }

            if (overrides?.Verbosity != null && overrides.Verbosity != CodexVerbosity.Default)
            {
                rootToSet["model_verbosity"] = $"\"{overrides.Verbosity.ToString().ToLowerInvariant()}\"";
            }
            else
            {
                rootToRemove.Add("model_verbosity");
            }

            if (overrides?.ToolOutputTokenLimit.HasValue == true && overrides.ToolOutputTokenLimit.Value > 0)
            {
                rootToSet["tool_output_token_limit"] = overrides.ToolOutputTokenLimit.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            else
            {
                rootToRemove.Add("tool_output_token_limit");
            }

            // Tool policy
            if (target.ToolPolicy != null && !target.ToolPolicy.AllowHostedWebSearch)
            {
                rootToSet["web_search"] = "\"disabled\"";
            }
            else
            {
                rootToRemove.Add("web_search");
            }

            if (target.ToolPolicy != null && !target.ToolPolicy.AllowToolSearch)
            {
                featToSet["tool_search"] = "false";
            }
            else
            {
                featToRemove.Add("tool_search");
            }

            if (target.ToolPolicy != null && !target.ToolPolicy.AllowMultiAgent)
            {
                featToSet["multi_agent"] = "false";
            }
            else
            {
                featToRemove.Add("multi_agent");
            }
        }

        return new TargetProjectionPlan(rootToSet, rootToRemove, featToSet, featToRemove, conflicts);
    }

    private static string NormalizeValue(string? val)
    {
        if (string.IsNullOrWhiteSpace(val)) return string.Empty;
        var trimmed = val.Trim();
        if (trimmed.StartsWith('"') && trimmed.EndsWith('"') && trimmed.Length >= 2)
        {
            return trimmed[1..^1];
        }
        return trimmed;
    }
}

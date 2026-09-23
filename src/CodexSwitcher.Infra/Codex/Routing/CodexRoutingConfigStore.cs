using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodexSwitcher.Core.Common.Storage;
using CodexSwitcher.Core.Providers.Models;
using CodexSwitcher.Core.Routing.Contracts;
using CodexSwitcher.Core.Routing.Models;
using CodexSwitcher.Infra.Common.Paths;

namespace CodexSwitcher.Infra.Codex.Routing;

/// <summary>
/// Production implementation of <see cref="ICodexRoutingConfigStore"/> providing narrow,
/// lossless mutations of config.toml for Switchboard provider routing and model configuration.
/// Never mutates external/unmanaged provider tables, MCP servers, or user comments.
/// </summary>
public sealed partial class CodexRoutingConfigStore : ICodexRoutingConfigStore
{
    private readonly IFileSystem _fs;
    private readonly AppPaths _paths;
    private readonly object _sync = new();

    private static readonly JsonSerializerOptions BaselineJsonOptions = new()
    {
        WriteIndented = true
    };

    public CodexRoutingConfigStore(IFileSystem fs, AppPaths paths)
    {
        _fs = fs ?? throw new ArgumentNullException(nameof(fs));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    [GeneratedRegex(@"^\s*model_provider\s*=\s*""(?<val>[^""]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex ModelProviderRegex();

    [GeneratedRegex(@"^\s*model\s*=\s*""(?<val>[^""]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex ModelRegex();

    [GeneratedRegex(@"^\s*model_catalog_json\s*=\s*""(?<val>[^""]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex ModelCatalogJsonRegex();

    [GeneratedRegex(@"^\s*\[")]
    private static partial Regex TableHeaderRegex();

    [GeneratedRegex(@"^\s*\[model_providers\.(?<id>[a-zA-Z0-9_-]+)\]\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ProviderTableHeaderRegex();

    public string ComputeFingerprint(string configTomlPath)
    {
        if (!_fs.FileExists(configTomlPath))
            return string.Empty;

        var bytes = _fs.ReadAllBytes(configTomlPath);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    public CodexRoutingState ReadRoutingState(string configTomlPath)
    {
        if (!_fs.FileExists(configTomlPath))
            return new CodexRoutingState(null, null, new Dictionary<string, CodexProviderBlock>(), string.Empty);

        var bytes = _fs.ReadAllBytes(configTomlPath);
        var fingerprint = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var raw = Encoding.UTF8.GetString(bytes);
        var lines = raw.Split('\n');

        string? modelProvider = null;
        string? model = null;
        string? modelCatalogJson = null;

        var firstTable = FirstTableIndex(lines);
        for (var i = 0; i < firstTable; i++)
        {
            var mProv = ModelProviderRegex().Match(lines[i]);
            if (mProv.Success) modelProvider = mProv.Groups["val"].Value;

            var mMod = ModelRegex().Match(lines[i]);
            if (mMod.Success) model = mMod.Groups["val"].Value;

            var mCat = ModelCatalogJsonRegex().Match(lines[i]);
            if (mCat.Success) modelCatalogJson = TomlUnescape(mCat.Groups["val"].Value);
        }

        var switchboardProviders = ParseSwitchboardProviders(lines);
        return new CodexRoutingState(modelProvider, model, switchboardProviders, fingerprint, modelCatalogJson);
    }

    public string ApplySwitchboardRouting(
        string configTomlPath,
        CodexProviderBlock providerBlock,
        string model,
        CodexModelOverrides? modelOverrides = null,
        string? modelCatalogJson = null,
        string? expectedFingerprint = null)
    {
        ArgumentNullException.ThrowIfNull(providerBlock);
        if (!providerBlock.ProviderId.StartsWith("switchboard_", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Provider ID '{providerBlock.ProviderId}' is not a Switchboard-owned provider.", nameof(providerBlock));
        }

        lock (_sync)
        {
            EnsureCasAndBackup(configTomlPath, expectedFingerprint);

            var raw = _fs.FileExists(configTomlPath) ? _fs.ReadAllText(configTomlPath) : string.Empty;
            var newline = raw.Contains("\r\n") ? "\r\n" : "\n";
            var lines = raw.Length == 0 ? new List<string>() : new List<string>(raw.Split('\n').Select(l => l.TrimEnd('\r')));

            // 1. Update root model_provider & model
            SetRootKey(lines, "model_provider", providerBlock.ProviderId);
            if (!string.IsNullOrWhiteSpace(model))
            {
                SetRootKey(lines, "model", model);
            }

            // 2. Manage top-level model overrides and cleanup stale overrides
            var baseline = LoadBaseline();
            var newTargetManagedKeys = new List<string> { "model_provider", "model" };
            var keysToApply = new Dictionary<string, (string Value, bool IsRaw)>();

            if (modelOverrides is not null)
            {
                if (modelOverrides.ContextWindowTokens.HasValue)
                {
                    keysToApply["model_context_window"] = (modelOverrides.ContextWindowTokens.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), true);
                }
                if (modelOverrides.AutoCompactTokenLimit.HasValue)
                {
                    keysToApply["model_auto_compact_token_limit"] = (modelOverrides.AutoCompactTokenLimit.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), true);
                }
                if (modelOverrides.AutoCompactTokenLimitScope != CompactLimitScope.Default)
                {
                    var scopeStr = modelOverrides.AutoCompactTokenLimitScope switch
                    {
                        CompactLimitScope.Total => "total",
                        CompactLimitScope.BodyAfterPrefix => "body_after_prefix",
                        _ => null
                    };
                    if (scopeStr is not null) keysToApply["model_auto_compact_token_limit_scope"] = (scopeStr, false);
                }
                if (modelOverrides.ReasoningEffort != CodexReasoningEffort.Default)
                {
                    var effortStr = modelOverrides.ReasoningEffort switch
                    {
                        CodexReasoningEffort.None => "none",
                        CodexReasoningEffort.Minimal => "minimal",
                        CodexReasoningEffort.Low => "low",
                        CodexReasoningEffort.Medium => "medium",
                        CodexReasoningEffort.High => "high",
                        CodexReasoningEffort.XHigh => "xhigh",
                        _ => null
                    };
                    if (effortStr is not null) keysToApply["model_reasoning_effort"] = (effortStr, false);
                }
                if (modelOverrides.ReasoningSummary != CodexReasoningSummary.Default)
                {
                    var summaryStr = modelOverrides.ReasoningSummary switch
                    {
                        CodexReasoningSummary.Auto => "auto",
                        CodexReasoningSummary.Concise => "concise",
                        CodexReasoningSummary.Detailed => "detailed",
                        CodexReasoningSummary.None => "none",
                        _ => null
                    };
                    if (summaryStr is not null) keysToApply["model_reasoning_summary"] = (summaryStr, false);
                }
                if (modelOverrides.Verbosity != CodexVerbosity.Default)
                {
                    var verbosityStr = modelOverrides.Verbosity switch
                    {
                        CodexVerbosity.Low => "low",
                        CodexVerbosity.Medium => "medium",
                        CodexVerbosity.High => "high",
                        _ => null
                    };
                    if (verbosityStr is not null) keysToApply["model_verbosity"] = (verbosityStr, false);
                }
                if (modelOverrides.ToolOutputTokenLimit.HasValue)
                {
                    keysToApply["tool_output_token_limit"] = (modelOverrides.ToolOutputTokenLimit.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), true);
                }
            }

            if (!string.IsNullOrWhiteSpace(modelCatalogJson))
            {
                keysToApply["model_catalog_json"] = (modelCatalogJson, false);
            }

            if (providerBlock.ToolPolicy is not null && !providerBlock.ToolPolicy.AllowHostedWebSearch)
            {
                keysToApply["web_search"] = ("disabled", false);
            }

            var knownSwitchboardManagedRootKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "model_context_window",
                "model_auto_compact_token_limit",
                "model_auto_compact_token_limit_scope",
                "model_reasoning_effort",
                "model_reasoning_summary",
                "model_verbosity",
                "tool_output_token_limit",
                "model_catalog_json",
                "web_search"
            };

            // Clean up previously managed keys that this target doesn't specify
            baseline.Generation++;
            baseline.ActiveProviderId = providerBlock.ProviderId;
            baseline.LastAppliedTargetKind = TargetKind.ApiProvider.ToString();
            baseline.LastAppliedProfileId = providerBlock.ProviderId;
            baseline.LastAppliedAt = DateTimeOffset.UtcNow;

            // Clean up previously managed keys that this target doesn't specify
            foreach (var oldKey in baseline.ManagedRootKeys)
            {
                if (knownSwitchboardManagedRootKeys.Contains(oldKey) && !keysToApply.ContainsKey(oldKey))
                {
                    if (baseline.ManagedKeys.TryGetValue(oldKey, out var prov) && prov.BaselineWasPresent && prov.BaselineValue is not null)
                    {
                        SetRootRawKey(lines, oldKey, prov.BaselineValue);
                    }
                    else if (baseline.BaselineValues.TryGetValue(oldKey, out var userBaseline) && userBaseline is not null)
                    {
                        SetRootRawKey(lines, oldKey, userBaseline);
                    }
                    else
                    {
                        RemoveRootKey(lines, oldKey);
                    }
                }
            }

            // Apply new keys and record provenance
            foreach (var (k, v) in keysToApply)
            {
                newTargetManagedKeys.Add(k);
                if (!baseline.ManagedKeys.TryGetValue(k, out var prov))
                {
                    var currentVal = GetRootKeyRaw(lines, k);
                    prov = new ManagedKeyProvenance
                    {
                        Key = k,
                        BaselineWasPresent = currentVal is not null,
                        BaselineValue = currentVal
                    };
                    baseline.ManagedKeys[k] = prov;
                }

                var appliedVal = v.IsRaw ? v.Value : $"\"{v.Value}\"";
                prov.LastAppliedWasPresent = true;
                prov.LastAppliedValue = appliedVal;
                prov.OwnerTargetKind = TargetKind.ApiProvider.ToString();
                prov.OwnerProfileId = providerBlock.ProviderId;
                prov.Generation = baseline.Generation;

                if (v.IsRaw)
                {
                    SetRootRawKey(lines, k, v.Value);
                }
                else
                {
                    SetRootKey(lines, k, v.Value);
                }
            }

            // Transactionally manage [features] table keys (multi_agent)
            // Note: tool_search = false in [features] is a legacy no-op flag in modern Codex;
            // effective tool_search suppression is driven by supports_search_tool = false in model_catalog_json.
            // tool_search remains in knownSwitchboardManagedFeatureKeys so any legacy baseline keys are cleaned up.
            var knownSwitchboardManagedFeatureKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "tool_search",
                "multi_agent"
            };
            var featureKeysToApply = new Dictionary<string, string>();
            if (providerBlock.ToolPolicy is not null)
            {
                if (!providerBlock.ToolPolicy.AllowMultiAgent)
                {
                    featureKeysToApply["multi_agent"] = "false";
                }
            }

            // Clean up previously managed feature keys that this target doesn't specify
            foreach (var oldKey in baseline.ManagedFeatureKeys)
            {
                if (knownSwitchboardManagedFeatureKeys.Contains(oldKey) && !featureKeysToApply.ContainsKey(oldKey))
                {
                    if (baseline.ManagedFeatures.TryGetValue(oldKey, out var fProv) && fProv.BaselineWasPresent && fProv.BaselineValue is not null)
                    {
                        SetFeatureKeyRaw(lines, oldKey, fProv.BaselineValue);
                    }
                    else if (baseline.BaselineFeatureValues.TryGetValue(oldKey, out var userBaseline) && userBaseline is not null)
                    {
                        SetFeatureKeyRaw(lines, oldKey, userBaseline);
                    }
                    else
                    {
                        RemoveFeatureKey(lines, oldKey);
                    }
                }
            }

            // Apply new feature keys and record provenance
            var newTargetManagedFeatureKeys = new List<string>();
            foreach (var (k, v) in featureKeysToApply)
            {
                newTargetManagedFeatureKeys.Add(k);
                if (!baseline.ManagedFeatures.TryGetValue(k, out var fProv))
                {
                    var currentVal = GetFeatureKeyRaw(lines, k);
                    fProv = new ManagedKeyProvenance
                    {
                        Key = k,
                        BaselineWasPresent = currentVal is not null,
                        BaselineValue = currentVal
                    };
                    baseline.ManagedFeatures[k] = fProv;
                }

                fProv.LastAppliedWasPresent = true;
                fProv.LastAppliedValue = v;
                fProv.OwnerTargetKind = TargetKind.ApiProvider.ToString();
                fProv.OwnerProfileId = providerBlock.ProviderId;
                fProv.Generation = baseline.Generation;

                SetFeatureKeyRaw(lines, k, v);
            }

            baseline.ManagedRootKeys = newTargetManagedKeys;
            baseline.ManagedFeatureKeys = newTargetManagedFeatureKeys;
            baseline.SyncLegacyCollections();
            SaveBaseline(baseline);

            // 3. Provider block hygiene: Ensure only the active Switchboard provider block exists
            CleanSwitchboardProviderBlocks(lines, keepProviderId: providerBlock.ProviderId);

            // 4. Format provider block
            var blockLines = FormatProviderBlockLines(providerBlock);

            // 5. Find and replace or append provider block
            var (startIdx, endIdx) = FindProviderTableRange(lines, providerBlock.ProviderId);
            if (startIdx >= 0)
            {
                lines.RemoveRange(startIdx, endIdx - startIdx);
                lines.InsertRange(startIdx, blockLines);
            }
            else
            {
                if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1]))
                {
                    lines.Add(string.Empty);
                }
                lines.AddRange(blockLines);
            }

            var updated = string.Join(newline, lines);
            if (raw.EndsWith(newline, StringComparison.Ordinal) && !updated.EndsWith(newline, StringComparison.Ordinal))
            {
                updated += newline;
            }

            _fs.WriteAllTextAtomic(configTomlPath, updated);
            return ComputeFingerprint(configTomlPath);
        }
    }

    public string UpdateProviderRoute(
        string configTomlPath,
        string providerId,
        string newBaseUrl,
        string? expectedFingerprint = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(newBaseUrl);

        if (!providerId.StartsWith("switchboard_", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Provider ID '{providerId}' is not a Switchboard-owned provider.", nameof(providerId));
        }

        lock (_sync)
        {
            EnsureCasAndBackup(configTomlPath, expectedFingerprint);

            var raw = _fs.FileExists(configTomlPath) ? _fs.ReadAllText(configTomlPath) : string.Empty;
            var newline = raw.Contains("\r\n") ? "\r\n" : "\n";
            var lines = new List<string>(raw.Split('\n').Select(l => l.TrimEnd('\r')));

            var (startIdx, endIdx) = FindProviderTableRange(lines, providerId);
            if (startIdx < 0)
            {
                throw new InvalidOperationException($"Switchboard provider '{providerId}' not found in configuration.");
            }

            var updatedBaseUrl = false;
            var baseUrlRegex = new Regex(@"^\s*base_url\s*=.*$", RegexOptions.IgnoreCase);

            for (var i = startIdx; i < endIdx; i++)
            {
                // Do not enter subtables like .auth or .http_headers
                if (i > startIdx && TableHeaderRegex().IsMatch(lines[i]))
                    break;

                if (baseUrlRegex.IsMatch(lines[i]))
                {
                    lines[i] = $"base_url = \"{TomlEscape(newBaseUrl)}\"";
                    updatedBaseUrl = true;
                    break;
                }
            }

            if (!updatedBaseUrl)
            {
                lines.Insert(startIdx + 1, $"base_url = \"{TomlEscape(newBaseUrl)}\"");
            }

            var updated = string.Join(newline, lines);
            if (raw.EndsWith(newline, StringComparison.Ordinal) && !updated.EndsWith(newline, StringComparison.Ordinal))
            {
                updated += newline;
            }

            _fs.WriteAllTextAtomic(configTomlPath, updated);
            return ComputeFingerprint(configTomlPath);
        }
    }

    public string ReturnToOpenAi(
        string configTomlPath,
        string? model = null,
        string? expectedFingerprint = null)
    {
        lock (_sync)
        {
            EnsureCasAndBackup(configTomlPath, expectedFingerprint);

            var raw = _fs.FileExists(configTomlPath) ? _fs.ReadAllText(configTomlPath) : string.Empty;
            var newline = raw.Contains("\r\n") ? "\r\n" : "\n";
            var lines = raw.Length == 0 ? new List<string>() : new List<string>(raw.Split('\n').Select(l => l.TrimEnd('\r')));

            var baseline = LoadBaseline();
            baseline.PurgePoisonedBaselines();

            SetRootKey(lines, "model_provider", "openai");
            if (!string.IsNullOrWhiteSpace(model))
            {
                SetRootKey(lines, "model", model);
            }
            else if (baseline.ManagedKeys.TryGetValue("model", out var modelProv))
            {
                if (modelProv.BaselineWasPresent && !string.IsNullOrWhiteSpace(modelProv.BaselineValue))
                {
                    SetRootRawKey(lines, "model", modelProv.BaselineValue);
                }
                else
                {
                    RemoveRootKey(lines, "model");
                }
            }
            else if (baseline.BaselineValues.TryGetValue("model", out var userModel) && !string.IsNullOrWhiteSpace(userModel))
            {
                SetRootRawKey(lines, "model", userModel);
            }
            else
            {
                RemoveRootKey(lines, "model");
            }

            var knownSwitchboardManagedRootKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "model_context_window",
                "model_auto_compact_token_limit",
                "model_auto_compact_token_limit_scope",
                "model_reasoning_effort",
                "model_reasoning_summary",
                "model_verbosity",
                "tool_output_token_limit",
                "model_catalog_json",
                "web_search"
            };

            foreach (var oldKey in knownSwitchboardManagedRootKeys)
            {
                if (oldKey.Equals("model_catalog_json", StringComparison.OrdinalIgnoreCase))
                {
                    RemoveRootKey(lines, oldKey);
                    continue;
                }

                if (baseline.ManagedKeys.TryGetValue(oldKey, out var prov))
                {
                    if (prov.BaselineWasPresent && prov.BaselineValue is not null)
                    {
                        SetRootRawKey(lines, oldKey, prov.BaselineValue);
                    }
                    else
                    {
                        RemoveRootKey(lines, oldKey);
                    }
                }
                else if (baseline.BaselineValues.TryGetValue(oldKey, out var userBaseline) && userBaseline is not null)
                {
                    SetRootRawKey(lines, oldKey, userBaseline);
                }
                else
                {
                    RemoveRootKey(lines, oldKey);
                }
            }

            var knownSwitchboardManagedFeatureKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "tool_search",
                "multi_agent"
            };

            foreach (var oldKey in knownSwitchboardManagedFeatureKeys)
            {
                if (baseline.ManagedFeatures.TryGetValue(oldKey, out var fProv))
                {
                    if (fProv.BaselineWasPresent && fProv.BaselineValue is not null)
                    {
                        SetFeatureKeyRaw(lines, oldKey, fProv.BaselineValue);
                    }
                    else
                    {
                        RemoveFeatureKey(lines, oldKey);
                    }
                }
                else if (baseline.BaselineFeatureValues.TryGetValue(oldKey, out var userBaseline) && userBaseline is not null)
                {
                    SetFeatureKeyRaw(lines, oldKey, userBaseline);
                }
                else
                {
                    RemoveFeatureKey(lines, oldKey);
                }
            }

            // Preferred hygiene: ZERO Switchboard provider blocks when ChatGPT is active
            CleanSwitchboardProviderBlocks(lines, keepProviderId: null);

            baseline.ActiveProviderId = null;
            baseline.LastAppliedTargetKind = TargetKind.ChatGptAccount.ToString();
            baseline.LastAppliedProfileId = null;
            baseline.LastAppliedAt = DateTimeOffset.UtcNow;
            baseline.Generation++;
            baseline.ManagedRootKeys.Clear();
            baseline.ManagedFeatureKeys.Clear();
            baseline.BaselineFeatureValues.Clear();
            baseline.LastAppliedValues.Clear();
            baseline.ManagedKeys.Clear();
            baseline.ManagedFeatures.Clear();
            SaveBaseline(baseline);

            var updated = string.Join(newline, lines);
            if (raw.EndsWith(newline, StringComparison.Ordinal) && !updated.EndsWith(newline, StringComparison.Ordinal))
            {
                updated += newline;
            }

            _fs.WriteAllTextAtomic(configTomlPath, updated);
            return ComputeFingerprint(configTomlPath);
        }
    }

    public void CleanOrphanProviderBlocks(string configTomlPath, IReadOnlySet<string> knownProviderIds)
    {
        ArgumentNullException.ThrowIfNull(knownProviderIds);

        lock (_sync)
        {
            if (!_fs.FileExists(configTomlPath)) return;

            var raw = _fs.ReadAllText(configTomlPath);
            var newline = raw.Contains("\r\n") ? "\r\n" : "\n";
            var lines = new List<string>(raw.Split('\n').Select(l => l.TrimEnd('\r')));

            var modified = false;
            var i = 0;
            while (i < lines.Count)
            {
                var match = ProviderTableHeaderRegex().Match(lines[i]);
                if (match.Success)
                {
                    var id = match.Groups["id"].Value;
                    if (id.StartsWith("switchboard_", StringComparison.OrdinalIgnoreCase) && !knownProviderIds.Contains(id))
                    {
                        var (startIdx, endIdx) = FindProviderTableRange(lines, id);
                        if (startIdx >= 0 && endIdx > startIdx)
                        {
                            lines.RemoveRange(startIdx, endIdx - startIdx);
                            if (startIdx < lines.Count && string.IsNullOrWhiteSpace(lines[startIdx]))
                            {
                                lines.RemoveAt(startIdx);
                            }
                            modified = true;
                            i = startIdx;
                            continue;
                        }
                    }
                }
                i++;
            }

            if (modified)
            {
                var updated = string.Join(newline, lines);
                if (raw.EndsWith(newline, StringComparison.Ordinal) && !updated.EndsWith(newline, StringComparison.Ordinal))
                {
                    updated += newline;
                }
                _fs.WriteAllTextAtomic(configTomlPath, updated);
            }
        }
    }

    private static bool CleanSwitchboardProviderBlocks(List<string> lines, string? keepProviderId = null)
    {
        var modified = false;
        var i = 0;
        while (i < lines.Count)
        {
            var match = ProviderTableHeaderRegex().Match(lines[i]);
            if (match.Success)
            {
                var id = match.Groups["id"].Value;
                if (id.StartsWith("switchboard_", StringComparison.OrdinalIgnoreCase))
                {
                    if (keepProviderId == null || !string.Equals(id, keepProviderId, StringComparison.OrdinalIgnoreCase))
                    {
                        var (startIdx, endIdx) = FindProviderTableRange(lines, id);
                        if (startIdx >= 0 && endIdx > startIdx)
                        {
                            lines.RemoveRange(startIdx, endIdx - startIdx);
                            if (startIdx < lines.Count && string.IsNullOrWhiteSpace(lines[startIdx]))
                            {
                                lines.RemoveAt(startIdx);
                            }
                            modified = true;
                            i = startIdx;
                            continue;
                        }
                    }
                }
            }
            i++;
        }
        return modified;
    }

    public void ReconcileAndRepairContaminatedConfig(string configTomlPath, IReadOnlySet<string> knownProviderIds)
    {
        lock (_sync)
        {
            CleanOrphanProviderBlocks(configTomlPath, knownProviderIds);

            var baseline = LoadBaseline();
            var poisoned = baseline.PurgePoisonedBaselines();
            if (poisoned)
            {
                SaveBaseline(baseline);
            }

            if (!_fs.FileExists(configTomlPath)) return;

            var routing = ReadRoutingState(configTomlPath);

            var isChatGpt = string.IsNullOrWhiteSpace(routing.ModelProvider) ||
                            string.Equals(routing.ModelProvider, "openai", StringComparison.OrdinalIgnoreCase);

            // Legacy contamination evidence: Switchboard catalog in config while active target is ChatGPT
            var hasSwitchboardCatalog = !string.IsNullOrWhiteSpace(routing.ModelCatalogJson) &&
                (routing.ModelCatalogJson.Contains("CodexSwitchboard", StringComparison.OrdinalIgnoreCase) ||
                 routing.ModelCatalogJson.Contains("catalogs", StringComparison.OrdinalIgnoreCase));

            if (isChatGpt && hasSwitchboardCatalog)
            {
                ReturnToOpenAi(configTomlPath);
            }
        }
    }

    public void RestoreExactBytes(string configTomlPath, byte[] exactBytes)
    {
        ArgumentNullException.ThrowIfNull(exactBytes);
        lock (_sync)
        {
            _fs.WriteAllBytesAtomic(configTomlPath, exactBytes);
        }
    }

    private SwitchboardRoutingBaseline LoadBaseline()
    {
        if (!_fs.FileExists(_paths.RoutingStatePath))
            return new SwitchboardRoutingBaseline();

        try
        {
            var json = _fs.ReadAllText(_paths.RoutingStatePath);
            return JsonSerializer.Deserialize<SwitchboardRoutingBaseline>(json, BaselineJsonOptions) ?? new SwitchboardRoutingBaseline();
        }
        catch
        {
            return new SwitchboardRoutingBaseline();
        }
    }

    private void SaveBaseline(SwitchboardRoutingBaseline baseline)
    {
        try
        {
            _paths.EnsureDirectories();
            var json = JsonSerializer.Serialize(baseline, BaselineJsonOptions);
            _fs.WriteAllTextAtomic(_paths.RoutingStatePath, json);
        }
        catch
        {
            // Best effort
        }
    }

    private void EnsureCasAndBackup(string configTomlPath, string? expectedFingerprint)
    {
        if (_fs.FileExists(configTomlPath))
        {
            var currentBytes = _fs.ReadAllBytes(configTomlPath);
            var currentFingerprint = Convert.ToHexString(SHA256.HashData(currentBytes)).ToLowerInvariant();

            if (!string.IsNullOrWhiteSpace(expectedFingerprint) &&
                !string.Equals(expectedFingerprint, currentFingerprint, StringComparison.OrdinalIgnoreCase))
            {
                throw new ConcurrentModificationException(
                    $"config.toml was modified concurrently. Expected fingerprint: {expectedFingerprint}, actual: {currentFingerprint}");
            }

            try
            {
                _paths.EnsureDirectories();
                var backupFile = Path.Combine(_paths.BackupsDir, $"config.toml.{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.bak");
                _fs.WriteAllBytesAtomic(backupFile, currentBytes);
            }
            catch
            {
                // Backup creation failure should not silently corrupt or crash, but best effort
            }
        }
    }

    private static int FirstTableIndex(IReadOnlyList<string> lines)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (TableHeaderRegex().IsMatch(lines[i]))
                return i;
        }
        return lines.Count;
    }

    private static void SetRootKey(List<string> lines, string key, string value)
    {
        var firstTable = FirstTableIndex(lines);
        var pattern = new Regex($@"^\s*{Regex.Escape(key)}\s*=.*$", RegexOptions.IgnoreCase);

        for (var i = 0; i < firstTable; i++)
        {
            if (pattern.IsMatch(lines[i]))
            {
                lines[i] = $"{key} = \"{TomlEscape(value)}\"";
                return;
            }
        }

        lines.Insert(firstTable, $"{key} = \"{TomlEscape(value)}\"");
    }

    private static void SetRootRawKey(List<string> lines, string key, string rawValue)
    {
        var firstTable = FirstTableIndex(lines);
        var pattern = new Regex($@"^\s*{Regex.Escape(key)}\s*=.*$", RegexOptions.IgnoreCase);

        for (var i = 0; i < firstTable; i++)
        {
            if (pattern.IsMatch(lines[i]))
            {
                lines[i] = $"{key} = {rawValue}";
                return;
            }
        }

        lines.Insert(firstTable, $"{key} = {rawValue}");
    }

    private static void RemoveRootKey(List<string> lines, string key)
    {
        var firstTable = FirstTableIndex(lines);
        var pattern = new Regex($@"^\s*{Regex.Escape(key)}\s*=.*$", RegexOptions.IgnoreCase);

        for (var i = 0; i < firstTable; i++)
        {
            if (pattern.IsMatch(lines[i]))
            {
                lines.RemoveAt(i);
                return;
            }
        }
    }

    private static string? GetRootKeyRaw(List<string> lines, string key)
    {
        var firstTable = FirstTableIndex(lines);
        var pattern = new Regex($@"^\s*{Regex.Escape(key)}\s*=\s*(?<val>.*)$", RegexOptions.IgnoreCase);

        for (var i = 0; i < firstTable; i++)
        {
            var match = pattern.Match(lines[i]);
            if (match.Success)
            {
                return match.Groups["val"].Value.Trim();
            }
        }

        return null;
    }

    private static (int StartIdx, int EndIdx) FindTableRange(List<string> lines, string tableName)
    {
        var headerRegex = new Regex($@"^\s*\[{Regex.Escape(tableName)}\]\s*$", RegexOptions.IgnoreCase);
        var startIdx = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            if (headerRegex.IsMatch(lines[i]))
            {
                startIdx = i;
                break;
            }
        }

        if (startIdx < 0) return (-1, -1);

        var endIdx = lines.Count;
        for (var j = startIdx + 1; j < lines.Count; j++)
        {
            if (TableHeaderRegex().IsMatch(lines[j]))
            {
                endIdx = j;
                break;
            }
        }

        return (startIdx, endIdx);
    }

    private static string? GetFeatureKeyRaw(List<string> lines, string key)
    {
        var (startIdx, endIdx) = FindTableRange(lines, "features");
        if (startIdx < 0) return null;

        var pattern = new Regex($@"^\s*{Regex.Escape(key)}\s*=\s*(?<val>.*)$", RegexOptions.IgnoreCase);
        for (var i = startIdx + 1; i < endIdx; i++)
        {
            var match = pattern.Match(lines[i]);
            if (match.Success)
            {
                return match.Groups["val"].Value.Trim();
            }
        }

        return null;
    }

    private static void SetFeatureKeyRaw(List<string> lines, string key, string rawValue)
    {
        var (startIdx, endIdx) = FindTableRange(lines, "features");
        var pattern = new Regex($@"^\s*{Regex.Escape(key)}\s*=.*$", RegexOptions.IgnoreCase);

        if (startIdx >= 0)
        {
            for (var i = startIdx + 1; i < endIdx; i++)
            {
                if (pattern.IsMatch(lines[i]))
                {
                    lines[i] = $"{key} = {rawValue}";
                    return;
                }
            }
            lines.Insert(startIdx + 1, $"{key} = {rawValue}");
        }
        else
        {
            var insertIdx = FirstTableIndex(lines);
            if (insertIdx < lines.Count && insertIdx > 0 && !string.IsNullOrWhiteSpace(lines[insertIdx - 1]))
            {
                lines.Insert(insertIdx++, string.Empty);
            }
            lines.Insert(insertIdx++, "[features]");
            lines.Insert(insertIdx, $"{key} = {rawValue}");
        }
    }

    private static void RemoveFeatureKey(List<string> lines, string key)
    {
        var (startIdx, endIdx) = FindTableRange(lines, "features");
        if (startIdx < 0) return;

        var pattern = new Regex($@"^\s*{Regex.Escape(key)}\s*=.*$", RegexOptions.IgnoreCase);
        for (var i = startIdx + 1; i < endIdx; i++)
        {
            if (pattern.IsMatch(lines[i]))
            {
                lines.RemoveAt(i);
                endIdx--;
                break;
            }
        }

        // If [features] table is now completely empty of non-empty lines, remove [features]
        var hasEntries = false;
        for (var i = startIdx + 1; i < endIdx; i++)
        {
            if (!string.IsNullOrWhiteSpace(lines[i]))
            {
                hasEntries = true;
                break;
            }
        }

        if (!hasEntries)
        {
            lines.RemoveAt(startIdx);
            if (startIdx < lines.Count && string.IsNullOrWhiteSpace(lines[startIdx]))
            {
                lines.RemoveAt(startIdx);
            }
        }
    }

    private static (int StartIdx, int EndIdx) FindProviderTableRange(List<string> lines, string providerId)
    {
        var headerRegex = new Regex($@"^\s*\[model_providers\.[""']?{Regex.Escape(providerId)}[""']?\]\s*$", RegexOptions.IgnoreCase);
        var subtablePrefix = $"model_providers.{providerId}.";

        var startIdx = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            if (headerRegex.IsMatch(lines[i]))
            {
                startIdx = i;
                break;
            }
        }

        if (startIdx < 0) return (-1, -1);

        var endIdx = lines.Count;
        for (var j = startIdx + 1; j < lines.Count; j++)
        {
            if (TableHeaderRegex().IsMatch(lines[j]))
            {
                var trimmed = lines[j].Trim().TrimStart('[').TrimEnd(']').Trim();
                if (!trimmed.StartsWith(subtablePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    endIdx = j;
                    // Back up past comments or blank lines that precede the next table header,
                    // as those comments belong to the next section.
                    while (endIdx > startIdx + 1)
                    {
                        var prevLine = lines[endIdx - 1].Trim();
                        if (prevLine.StartsWith('#') || string.IsNullOrEmpty(prevLine))
                        {
                            endIdx--;
                        }
                        else
                        {
                            break;
                        }
                    }
                    break;
                }
            }
        }

        return (startIdx, endIdx);
    }

    private static List<string> FormatProviderBlockLines(CodexProviderBlock b)
    {
        var escapedName = TomlEscape(b.Name);
        var escapedBaseUrl = TomlEscape(b.BaseUrl);
        var escapedWireApi = TomlEscape(b.WireApi);
        var escapedCmd = TomlEscape(b.BrokerCommand);

        var formattedArgs = string.Join(", ", b.BrokerArgs.Select(a => $"\"{TomlEscape(a)}\""));

        var lines = new List<string>
        {
            $"[model_providers.{b.ProviderId}]",
            $"name = \"{escapedName}\"",
            $"base_url = \"{escapedBaseUrl}\"",
            $"wire_api = \"{escapedWireApi}\""
        };

        if (b.RequestMaxRetries.HasValue)
        {
            lines.Add($"request_max_retries = {b.RequestMaxRetries.Value}");
        }

        if (b.StreamMaxRetries.HasValue)
        {
            lines.Add($"stream_max_retries = {b.StreamMaxRetries.Value}");
        }

        if (b.StreamIdleTimeoutMs.HasValue)
        {
            lines.Add($"stream_idle_timeout_ms = {b.StreamIdleTimeoutMs.Value}");
        }

        if (b.WebSocketConnectTimeoutMs.HasValue)
        {
            lines.Add($"websocket_connect_timeout_ms = {b.WebSocketConnectTimeoutMs.Value}");
        }

        if (b.SupportsWebSockets.HasValue)
        {
            lines.Add($"supports_websockets = {(b.SupportsWebSockets.Value ? "true" : "false")}");
        }

        if (b.SupportsStandaloneWebSearch.HasValue)
        {
            lines.Add($"supports_standalone_web_search = {(b.SupportsStandaloneWebSearch.Value ? "true" : "false")}");
        }

        if (b.QueryParams is { Count: > 0 })
        {
            lines.Add(string.Empty);
            lines.Add($"[model_providers.{b.ProviderId}.query_params]");
            foreach (var (k, v) in b.QueryParams)
            {
                lines.Add($"{k} = \"{TomlEscape(v)}\"");
            }
        }

        if (b.HttpHeaders is { Count: > 0 })
        {
            lines.Add(string.Empty);
            lines.Add($"[model_providers.{b.ProviderId}.http_headers]");
            foreach (var (k, v) in b.HttpHeaders)
            {
                lines.Add($"{k} = \"{TomlEscape(v)}\"");
            }
        }

        if (b.EnvHttpHeaders is { Count: > 0 })
        {
            lines.Add(string.Empty);
            lines.Add($"[model_providers.{b.ProviderId}.env_http_headers]");
            foreach (var (k, v) in b.EnvHttpHeaders)
            {
                lines.Add($"{k} = \"{TomlEscape(v)}\"");
            }
        }

        lines.Add(string.Empty);
        lines.Add($"[model_providers.{b.ProviderId}.auth]");
        lines.Add($"command = \"{escapedCmd}\"");
        lines.Add($"args = [{formattedArgs}]");
        lines.Add($"timeout_ms = {b.TimeoutMs}");

        return lines;
    }

    public static string TomlEscape(string value)
    {
        if (value is null) return string.Empty;
        var sb = new StringBuilder(value.Length + 16);
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': sb.Append(@"\\"); break;
                case '"': sb.Append(@"\"""); break;
                case '\b': sb.Append(@"\b"); break;
                case '\f': sb.Append(@"\f"); break;
                case '\n': sb.Append(@"\n"); break;
                case '\r': sb.Append(@"\r"); break;
                case '\t': sb.Append(@"\t"); break;
                default:
                    if (char.IsControl(c))
                    {
                        sb.Append(System.Globalization.CultureInfo.InvariantCulture, $@"\u{(int)c:X4}");
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
        return sb.ToString();
    }

    public static string TomlUnescape(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var sb = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                i++;
                switch (value[i])
                {
                    case '\\': sb.Append('\\'); break;
                    case '"': sb.Append('"'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    default: sb.Append('\\').Append(value[i]); break;
                }
            }
            else
            {
                sb.Append(value[i]);
            }
        }
        return sb.ToString();
    }

    private static Dictionary<string, CodexProviderBlock> ParseSwitchboardProviders(string[] lines)
    {
        var map = new Dictionary<string, CodexProviderBlock>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < lines.Length; i++)
        {
            var match = ProviderTableHeaderRegex().Match(lines[i]);
            if (match.Success)
            {
                var id = match.Groups["id"].Value;
                if (!id.StartsWith("switchboard_", StringComparison.OrdinalIgnoreCase))
                    continue;

                string name = id;
                string baseUrl = string.Empty;
                string wireApi = "responses";
                string command = string.Empty;
                var args = new List<string>();
                int timeoutMs = 5000;
                ulong? reqRetries = null;
                ulong? streamRetries = null;
                ulong? streamIdleTimeout = null;
                ulong? wsTimeout = null;
                bool? supportsWs = null;
                bool? supportsSearch = null;
                var queryParams = new Dictionary<string, string>();
                var httpHeaders = new Dictionary<string, string>();
                var envHttpHeaders = new Dictionary<string, string>();

                string? currentSubtable = null;
                for (var j = i + 1; j < lines.Length; j++)
                {
                    if (TableHeaderRegex().IsMatch(lines[j]))
                    {
                        var trimmedHeader = lines[j].Trim().TrimStart('[').TrimEnd(']').Trim();
                        if (trimmedHeader.Equals($"model_providers.{id}.auth", StringComparison.OrdinalIgnoreCase))
                        {
                            currentSubtable = "auth";
                            continue;
                        }
                        if (trimmedHeader.Equals($"model_providers.{id}.query_params", StringComparison.OrdinalIgnoreCase))
                        {
                            currentSubtable = "query_params";
                            continue;
                        }
                        if (trimmedHeader.Equals($"model_providers.{id}.http_headers", StringComparison.OrdinalIgnoreCase))
                        {
                            currentSubtable = "http_headers";
                            continue;
                        }
                        if (trimmedHeader.Equals($"model_providers.{id}.env_http_headers", StringComparison.OrdinalIgnoreCase))
                        {
                            currentSubtable = "env_http_headers";
                            continue;
                        }
                        break;
                    }

                    var line = lines[j].Trim();
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
                        continue;

                    if (currentSubtable == "auth")
                    {
                        if (line.StartsWith("command", StringComparison.OrdinalIgnoreCase))
                        {
                            var eq = line.IndexOf('=');
                            if (eq >= 0) command = line[(eq + 1)..].Trim().Trim('"');
                        }
                        else if (line.StartsWith("timeout_ms", StringComparison.OrdinalIgnoreCase))
                        {
                            var eq = line.IndexOf('=');
                            if (eq >= 0 && int.TryParse(line[(eq + 1)..].Trim(), out var parsed)) timeoutMs = parsed;
                        }
                    }
                    else if (currentSubtable == "query_params")
                    {
                        var eq = line.IndexOf('=');
                        if (eq > 0)
                        {
                            var k = line[..eq].Trim();
                            var v = line[(eq + 1)..].Trim().Trim('"');
                            queryParams[k] = v;
                        }
                    }
                    else if (currentSubtable == "http_headers")
                    {
                        var eq = line.IndexOf('=');
                        if (eq > 0)
                        {
                            var k = line[..eq].Trim();
                            var v = line[(eq + 1)..].Trim().Trim('"');
                            httpHeaders[k] = v;
                        }
                    }
                    else if (currentSubtable == "env_http_headers")
                    {
                        var eq = line.IndexOf('=');
                        if (eq > 0)
                        {
                            var k = line[..eq].Trim();
                            var v = line[(eq + 1)..].Trim().Trim('"');
                            envHttpHeaders[k] = v;
                        }
                    }
                    else
                    {
                        if (line.StartsWith("name", StringComparison.OrdinalIgnoreCase))
                        {
                            var eq = line.IndexOf('=');
                            if (eq >= 0) name = line[(eq + 1)..].Trim().Trim('"');
                        }
                        else if (line.StartsWith("base_url", StringComparison.OrdinalIgnoreCase))
                        {
                            var eq = line.IndexOf('=');
                            if (eq >= 0) baseUrl = line[(eq + 1)..].Trim().Trim('"');
                        }
                        else if (line.StartsWith("wire_api", StringComparison.OrdinalIgnoreCase))
                        {
                            var eq = line.IndexOf('=');
                            if (eq >= 0) wireApi = line[(eq + 1)..].Trim().Trim('"');
                        }
                        else if (line.StartsWith("request_max_retries", StringComparison.OrdinalIgnoreCase))
                        {
                            var eq = line.IndexOf('=');
                            if (eq >= 0 && ulong.TryParse(line[(eq + 1)..].Trim(), out var val)) reqRetries = val;
                        }
                        else if (line.StartsWith("stream_max_retries", StringComparison.OrdinalIgnoreCase))
                        {
                            var eq = line.IndexOf('=');
                            if (eq >= 0 && ulong.TryParse(line[(eq + 1)..].Trim(), out var val)) streamRetries = val;
                        }
                        else if (line.StartsWith("stream_idle_timeout_ms", StringComparison.OrdinalIgnoreCase))
                        {
                            var eq = line.IndexOf('=');
                            if (eq >= 0 && ulong.TryParse(line[(eq + 1)..].Trim(), out var val)) streamIdleTimeout = val;
                        }
                        else if (line.StartsWith("websocket_connect_timeout_ms", StringComparison.OrdinalIgnoreCase))
                        {
                            var eq = line.IndexOf('=');
                            if (eq >= 0 && ulong.TryParse(line[(eq + 1)..].Trim(), out var val)) wsTimeout = val;
                        }
                        else if (line.StartsWith("supports_websockets", StringComparison.OrdinalIgnoreCase))
                        {
                            var eq = line.IndexOf('=');
                            if (eq >= 0 && bool.TryParse(line[(eq + 1)..].Trim(), out var val)) supportsWs = val;
                        }
                        else if (line.StartsWith("supports_standalone_web_search", StringComparison.OrdinalIgnoreCase))
                        {
                            var eq = line.IndexOf('=');
                            if (eq >= 0 && bool.TryParse(line[(eq + 1)..].Trim(), out var val)) supportsSearch = val;
                        }
                    }
                }

                map[id] = new CodexProviderBlock(
                    id, name, baseUrl, wireApi, command, args, timeoutMs,
                    reqRetries, streamRetries, streamIdleTimeout, wsTimeout, supportsWs, supportsSearch,
                    queryParams.Count > 0 ? queryParams : null,
                    httpHeaders.Count > 0 ? httpHeaders : null,
                    envHttpHeaders.Count > 0 ? envHttpHeaders : null);
            }
        }

        return map;
    }
}

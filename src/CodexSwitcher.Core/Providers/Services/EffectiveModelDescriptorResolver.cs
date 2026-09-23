using System;
using System.Collections.Generic;
using CodexSwitcher.Core.Providers.Catalog;
using CodexSwitcher.Core.Providers.Contracts;
using CodexSwitcher.Core.Providers.Models;
using CodexSwitcher.Core.Routing.Models;

namespace CodexSwitcher.Core.Providers.Services;

/// <summary>
/// Domain service that determines the effective model descriptor and builds compliant catalog dictionary entries.
/// Intersects model facts, provider route capabilities, runtime capabilities, and user overrides.
/// Strictly omits unsupported tools (e.g. freeform patch, tool search, hosted search on Modelflare Grok).
/// </summary>
public sealed class EffectiveModelDescriptorResolver : IEffectiveModelDescriptorResolver
{
    public const long FallbackContextCeiling = 272_000;

    private readonly ICodexModelMetadataResolver _metadataResolver;

    public EffectiveModelDescriptorResolver(ICodexModelMetadataResolver? metadataResolver = null)
    {
        _metadataResolver = metadataResolver ?? new CodexModelMetadataResolver();
    }

    public EffectiveModelDescriptor ResolveDescriptor(
        ApiProviderModelItem modelItem,
        ApiProviderProfile profile,
        ProviderDescriptor? catalogDescriptor = null,
        EffectiveToolPolicy? toolPolicy = null,
        bool isLegacyRuntime = false,
        bool isSelectedModel = false)
    {
        ArgumentNullException.ThrowIfNull(modelItem);
        ArgumentNullException.ThrowIfNull(profile);

        var slug = !string.IsNullOrWhiteSpace(modelItem.Slug)
            ? modelItem.Slug
            : (!string.IsNullOrWhiteSpace(profile.SelectedModel) ? profile.SelectedModel : "default-model");

        var displayName = !string.IsNullOrWhiteSpace(modelItem.DisplayName)
            ? modelItem.DisplayName
            : (!string.IsNullOrWhiteSpace(profile.Nickname) && isSelectedModel ? profile.Nickname : slug);

        // 1. Resolve Context Window & Limits
        var requestedContext = modelItem.UserOverrides?.ContextWindowTokens
            ?? (isSelectedModel ? profile.ModelOverrides?.ContextWindowTokens : null)
            ?? modelItem.ContextWindow
            ?? FallbackContextCeiling;

        var limitFact = _metadataResolver.ResolveLimitFact(slug);
        var maxContext = limitFact.IsKnown && limitFact.DocumentedMaxContext.HasValue
            ? limitFact.DocumentedMaxContext.Value
            : Math.Max(requestedContext, FallbackContextCeiling);

        var targetContext = Math.Min(requestedContext, maxContext);

        // 2. Resolve Reasoning Effort & Verbosity
        var reasoningEffort = modelItem.UserOverrides?.ReasoningEffort
            ?? (isSelectedModel ? profile.ModelOverrides?.ReasoningEffort : null)
            ?? CodexReasoningEffort.Default;

        var verbosity = modelItem.UserOverrides?.Verbosity
            ?? (isSelectedModel ? profile.ModelOverrides?.Verbosity : null)
            ?? CodexVerbosity.Default;

        // 3. Resolve Tool Policy (Route & Provider evidence)
        var effectivePolicy = toolPolicy ?? EffectiveToolPolicy.Resolve(profile);

        var allowFreeformApplyPatch = effectivePolicy.AllowCustomFreeformApplyPatch;
        var allowToolSearch = effectivePolicy.AllowToolSearch;
        var allowHostedWebSearch = effectivePolicy.AllowHostedWebSearch;

        // 4. Resolve Modalities & Streaming
        var supportsImages = modelItem.Capabilities == null || modelItem.Capabilities.Vision.State != CapabilityEvidenceState.ProbeFailed;
        var supportsStreaming = true;

        var priority = isSelectedModel ? 10 : 1;

        return new EffectiveModelDescriptor(
            Slug: slug,
            DisplayName: displayName,
            ContextWindow: targetContext,
            MaxContextWindow: maxContext,
            ReasoningEffort: reasoningEffort,
            Verbosity: verbosity,
            AllowFreeformApplyPatch: allowFreeformApplyPatch,
            AllowToolSearch: allowToolSearch,
            AllowHostedWebSearch: allowHostedWebSearch,
            SupportsImages: supportsImages,
            SupportsStreaming: supportsStreaming,
            Priority: priority);
    }

    public Dictionary<string, object?> BuildCatalogEntry(
        EffectiveModelDescriptor descriptor,
        bool isLegacyRuntime = false)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        if (isLegacyRuntime)
        {
            return new Dictionary<string, object?>
            {
                ["slug"] = descriptor.Slug,
                ["display_name"] = descriptor.DisplayName,
                ["description"] = "Switchboard-managed model configuration",
                ["context_window"] = descriptor.ContextWindow,
                ["max_context_window"] = descriptor.MaxContextWindow,
                ["supported_reasoning_levels"] = new[] { "none", "low", "medium", "high", "xhigh" },
                ["default_reasoning_level"] = "none",
                ["shell_type"] = "generic",
                ["visibility"] = "visible",
                ["supported_in_api"] = true,
                ["priority"] = descriptor.Priority,
                ["supports_streaming"] = descriptor.SupportsStreaming,
                ["supports_tools"] = true,
                ["supports_images"] = descriptor.SupportsImages
            };
        }

        var defaultReasoning = descriptor.ReasoningEffort switch
        {
            CodexReasoningEffort.None => "none",
            CodexReasoningEffort.Minimal => "low",
            CodexReasoningEffort.Low => "low",
            CodexReasoningEffort.Medium => "medium",
            CodexReasoningEffort.High => "high",
            CodexReasoningEffort.XHigh => "xhigh",
            _ => "none"
        };

        var supportVerbosity = descriptor.Verbosity is not CodexVerbosity.Default;
        var defaultVerbosity = descriptor.Verbosity switch
        {
            CodexVerbosity.Low => "low",
            CodexVerbosity.Medium => "medium",
            CodexVerbosity.High => "high",
            _ => "low"
        };

        var modernEntry = new Dictionary<string, object?>
        {
            ["slug"] = descriptor.Slug,
            ["display_name"] = descriptor.DisplayName,
            ["description"] = "Switchboard-managed model configuration",
            ["default_reasoning_level"] = defaultReasoning,
            ["supported_reasoning_levels"] = new object[]
            {
                new Dictionary<string, string> { ["effort"] = "none", ["description"] = "No reasoning effort" },
                new Dictionary<string, string> { ["effort"] = "low", ["description"] = "Fast responses with lighter reasoning" },
                new Dictionary<string, string> { ["effort"] = "medium", ["description"] = "Balances speed and reasoning depth" },
                new Dictionary<string, string> { ["effort"] = "high", ["description"] = "Greater reasoning depth for complex problems" },
                new Dictionary<string, string> { ["effort"] = "xhigh", ["description"] = "Extra high reasoning depth" }
            },
            ["shell_type"] = "unified_exec",
            ["visibility"] = "list",
            ["supported_in_api"] = true,
            ["priority"] = descriptor.Priority,
            ["context_window"] = descriptor.ContextWindow,
            ["max_context_window"] = descriptor.MaxContextWindow,
            ["support_verbosity"] = supportVerbosity,
            ["default_verbosity"] = defaultVerbosity,
            ["supports_reasoning_summaries"] = true,
            ["default_reasoning_summary"] = "none",
            ["web_search_tool_type"] = descriptor.AllowHostedWebSearch ? "text_and_image" : "none",
            ["truncation_policy"] = new Dictionary<string, object> { ["mode"] = "tokens", ["limit"] = 10000 },
            ["supports_parallel_tool_calls"] = true,
            ["supports_image_detail_original"] = true,
            ["supports_search_tool"] = descriptor.AllowToolSearch,
            ["input_modalities"] = descriptor.SupportsImages ? new[] { "text", "image" } : new[] { "text" },
            ["effective_context_window_percent"] = 95,
            ["experimental_supported_tools"] = Array.Empty<string>(),
            ["base_instructions"] = "You are a helpful AI assistant."
        };

        if (descriptor.AllowFreeformApplyPatch)
        {
            modernEntry["apply_patch_tool_type"] = "freeform";
        }

        return modernEntry;
    }
}

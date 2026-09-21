using System;
using System.Collections.Generic;
using CodexSwitcher.Core.Providers.Contracts;
using CodexSwitcher.Core.Providers.Models;

namespace CodexSwitcher.Core.Providers.Services;

/// <summary>
/// Default implementation of <see cref="ICodexModelMetadataResolver"/> backing official
/// documented model limits, provider catalog descriptors, and unverified fallbacks.
/// </summary>
public sealed class CodexModelMetadataResolver : ICodexModelMetadataResolver
{
    private readonly IProviderCatalogService? _catalogService;

    // Documentation-backed official limits
    private static readonly Dictionary<string, (long MaxContext, string Source)> BuiltInFacts =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // xAI Grok family
            ["grok-4.6"] = (500_000, "Official xAI Grok 4.6 documentation"),
            ["grok-4.6-0205"] = (500_000, "Official xAI Grok 4.6 documentation"),
            ["grok-4.20"] = (1_000_000, "Official xAI Grok 4.20 documentation"),
            ["grok-4-fast"] = (131_072, "Official xAI documentation"),
            ["grok-beta"] = (131_072, "Official xAI documentation"),
            ["grok-2-1212"] = (131_072, "Official xAI documentation"),

            // OpenAI GPT-4 / o-series
            ["gpt-4o"] = (128_000, "Official OpenAI documentation"),
            ["gpt-4o-mini"] = (128_000, "Official OpenAI documentation"),
            ["o1"] = (200_000, "Official OpenAI documentation"),
            ["o1-mini"] = (128_000, "Official OpenAI documentation"),
            ["o1-preview"] = (128_000, "Official OpenAI documentation"),
            ["o3-mini"] = (200_000, "Official OpenAI documentation"),

            // Upstream Codex internal models
            ["gpt-5.6-sol"] = (200_000, "Codex default specification"),
            ["gpt-5.6-terra"] = (200_000, "Codex default specification"),
            ["gpt-5.6-luna"] = (200_000, "Codex default specification"),
            ["gpt-5.5"] = (200_000, "Codex default specification"),
            ["gpt-5.4"] = (200_000, "Codex default specification"),
            ["gpt-6-astra"] = (200_000, "Codex default specification"),

            // Anthropic Claude
            ["claude-3-5-sonnet"] = (200_000, "Official Anthropic documentation"),
            ["claude-3-5-haiku"] = (200_000, "Official Anthropic documentation"),
            ["claude-3-opus"] = (200_000, "Official Anthropic documentation"),

            // DeepSeek
            ["deepseek-chat"] = (64_000, "Official DeepSeek documentation"),
            ["deepseek-reasoner"] = (64_000, "Official DeepSeek documentation"),
        };

    public CodexModelMetadataResolver(IProviderCatalogService? catalogService = null)
    {
        _catalogService = catalogService;
    }

    public CodexModelLimitFact ResolveLimitFact(string modelSlug, string? providerId = null)
    {
        if (string.IsNullOrWhiteSpace(modelSlug))
        {
            return CodexModelLimitFact.Unknown(string.Empty);
        }

        var normalizedSlug = modelSlug.Trim();

        // 1. Exact match in built-in documentation-backed facts
        if (BuiltInFacts.TryGetValue(normalizedSlug, out var directFact))
        {
            return CodexModelLimitFact.Known(normalizedSlug, directFact.MaxContext, directFact.Source);
        }

        // 2. Prefix / pattern matching for known families
        if (normalizedSlug.StartsWith("grok-4.6", StringComparison.OrdinalIgnoreCase))
        {
            return CodexModelLimitFact.Known(normalizedSlug, 500_000, "Official xAI Grok 4.6 documentation");
        }

        if (normalizedSlug.StartsWith("grok-4.20", StringComparison.OrdinalIgnoreCase))
        {
            return CodexModelLimitFact.Known(normalizedSlug, 1_000_000, "Official xAI Grok 4.20 documentation");
        }

        // 3. Provider catalog descriptor inspection (if providerId or catalog available)
        if (_catalogService != null && !string.IsNullOrWhiteSpace(providerId))
        {
            var desc = _catalogService.GetDescriptor(providerId);
            if (desc != null && !string.IsNullOrWhiteSpace(desc.Codex.DefaultModel))
            {
                if (string.Equals(desc.Codex.DefaultModel, normalizedSlug, StringComparison.OrdinalIgnoreCase))
                {
                    if (BuiltInFacts.TryGetValue(desc.Codex.DefaultModel, out var descModelFact))
                    {
                        return CodexModelLimitFact.Known(normalizedSlug, descModelFact.MaxContext, $"Catalog default model for '{desc.DisplayName}' ({descModelFact.Source})");
                    }
                }
            }
        }

        // 4. Unknown model (unverified)
        return CodexModelLimitFact.Unknown(normalizedSlug);
    }

    public CodexModelContextFact ResolveContextFact(
        string modelSlug,
        string? providerId = null,
        long? routeAdvertisedMax = null,
        long? requestedContext = null,
        long? codexClientContext = null)
    {
        var limitFact = ResolveLimitFact(modelSlug, providerId);
        return CodexModelContextFact.FromLimitFact(limitFact, routeAdvertisedMax, requestedContext, codexClientContext);
    }

    public ModelCapabilities ResolveCapabilities(string modelSlug, string? runtimeIdentity = null)
    {
        if (string.IsNullOrWhiteSpace(modelSlug))
        {
            return ModelCapabilities.ForUnknown(string.Empty);
        }

        var normalized = modelSlug.Trim();
        if (normalized.StartsWith("grok-4.6", StringComparison.OrdinalIgnoreCase))
        {
            return ModelCapabilities.ForGrok46(runtimeIdentity);
        }

        if (normalized.StartsWith("grok-4.20", StringComparison.OrdinalIgnoreCase))
        {
            return ModelCapabilities.ForGrok420(runtimeIdentity);
        }

        var limitFact = ResolveLimitFact(normalized);
        if (limitFact.IsKnown)
        {
            return new ModelCapabilities(
                normalized,
                CapabilityEvidence.Documented(limitFact.SourceDescription, "Reasoning supported"),
                CapabilityEvidence.Documented(limitFact.SourceDescription, "Vision supported"),
                CapabilityEvidence.Documented(limitFact.SourceDescription, "Tool calling supported"),
                CapabilityEvidence.Documented(limitFact.SourceDescription, $"{limitFact.DocumentedMaxContext} context limit"));
        }

        return ModelCapabilities.ForUnknown(normalized);
    }
}

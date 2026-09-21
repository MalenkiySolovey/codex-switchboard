using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CodexSwitcher.Core.Providers.Models;

namespace CodexSwitcher.Core.Providers.Contracts;

/// <summary>
/// Options for executing diagnostic capability probes against an API provider.
/// Hosted search and external tooling probes must be explicitly opt-in to avoid unexpected billing.
/// </summary>
public sealed record ProviderProbeOptions(
    bool IncludeVision = true,
    bool IncludeStreaming = false,
    bool IncludeHostedSearch = false,
    bool RunCodexSmokeTest = true,
    TimeSpan Timeout = default);

/// <summary>
/// Diagnostic report summarizing results from dual-layer provider capability probes:
/// Layer 1: HTTP protocol qualification (/models, /responses, reasoning, vision, tools, streaming)
/// Layer 2: Real isolated Codex execution smoke test
/// </summary>
public sealed record ProviderProbeReport(
    string BaseUrl,
    string ModelSlug,
    CodexCompatibilityLevel CompatibilityLevel,
    CapabilityEvidence ModelsEndpoint,
    CapabilityEvidence ResponsesEndpoint,
    CapabilityEvidence StreamingSupport,
    CapabilityEvidence ReasoningSupport,
    CapabilityEvidence VisionSupport,
    CapabilityEvidence ToolCallingSupport,
    CapabilityEvidence HostedSearchSupport,
    CapabilityEvidence CodexRuntimeSmokeTest,
    CapabilityEvidence ModelActuallyUsed,
    CapabilityEvidence ProviderActuallyUsed,
    CapabilityEvidence TokenUsageVerified,
    CapabilityEvidence BuiltInToolsExposed,
    CapabilityEvidence NamespaceToolsSupport,
    DateTimeOffset ProbedAt,
    string? CodexRuntimeIdentity,
    List<string>? DiscoveredModelIds = null,
    string? DiagnosticSummary = null)
{
    public bool AllStandardChecksPassed =>
        ModelsEndpoint.IsSupported &&
        ResponsesEndpoint.IsSupported &&
        ToolCallingSupport.IsSupported &&
        (CodexRuntimeSmokeTest.State == CapabilityEvidenceState.Unknown || CodexRuntimeSmokeTest.IsSupported);

    /// <summary>
    /// Generates a sanitized JSON export containing diagnostic metadata, runtime hashes,
    /// HTTP status classes, and capability matrices.
    /// Invariant: Strictly excludes API keys, Authorization headers, and secret env values.
    /// </summary>
    public string GenerateSanitizedExport(string providerDisplayName, string? routePoolLabel = null)
    {
        var export = new Dictionary<string, object?>
        {
            ["provider"] = providerDisplayName,
            ["routePoolLabel"] = routePoolLabel,
            ["model"] = ModelSlug,
            ["baseUrl"] = BaseUrl,
            ["compatibilityLevel"] = CompatibilityLevel.ToString(),
            ["codexRuntimeIdentity"] = CodexRuntimeIdentity,
            ["probedAt"] = ProbedAt.ToString("O"),
            ["capabilities"] = new Dictionary<string, object?>
            {
                ["modelsEndpoint"] = FormatEvidence(ModelsEndpoint),
                ["responsesEndpoint"] = FormatEvidence(ResponsesEndpoint),
                ["streaming"] = FormatEvidence(StreamingSupport),
                ["reasoning"] = FormatEvidence(ReasoningSupport),
                ["vision"] = FormatEvidence(VisionSupport),
                ["toolCalling"] = FormatEvidence(ToolCallingSupport),
                ["hostedSearch"] = FormatEvidence(HostedSearchSupport),
                ["codexSmokeTest"] = FormatEvidence(CodexRuntimeSmokeTest),
                ["modelActuallyUsed"] = FormatEvidence(ModelActuallyUsed),
                ["providerActuallyUsed"] = FormatEvidence(ProviderActuallyUsed),
                ["tokenUsageVerified"] = FormatEvidence(TokenUsageVerified),
                ["builtInToolsExposed"] = FormatEvidence(BuiltInToolsExposed),
                ["namespaceToolsSupport"] = FormatEvidence(NamespaceToolsSupport),
            },
            ["discoveredModelsCount"] = DiscoveredModelIds?.Count ?? 0,
            ["diagnosticSummary"] = DiagnosticSummary
        };

        return System.Text.Json.JsonSerializer.Serialize(export, s_exportJsonOptions);
    }

    private static readonly System.Text.Json.JsonSerializerOptions s_exportJsonOptions = new() { WriteIndented = true };

    private static object FormatEvidence(CapabilityEvidence ev) =>
        new { state = ev.State.ToString(), source = ev.Source, isSupported = ev.IsSupported, detail = ev.Detail };
}

/// <summary>
/// Service coordinating dual-layer compatibility probes against provider endpoints:
/// Layer 1: HTTP protocol qualification (/models, /responses, reasoning, vision, tools, streaming)
/// Layer 2: Real isolated Codex execution smoke test
/// </summary>
public interface IProviderCompatibilityProbeService
{
    Task<ProviderProbeReport> ProbeCompatibilityAsync(
        string baseUrl,
        string apiKey,
        string modelSlug,
        ProviderProbeOptions? options = null,
        CancellationToken cancellationToken = default);
}

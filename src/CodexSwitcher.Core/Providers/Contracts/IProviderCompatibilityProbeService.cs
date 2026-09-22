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
    bool RunToolSmokeTest = true,
    bool IncludeAdvancedNamespaceProbes = false,
    bool IncludeStandaloneSearch = false,
    TimeSpan Timeout = default);

/// <summary>
/// Diagnostic report summarizing results from dual-layer provider capability probes:
/// Layer 1: HTTP protocol qualification (/models, /responses, reasoning, vision, tools, streaming)
/// Layer 2: Real isolated Codex execution smoke test (basic turn + tool smoke test)
/// </summary>
public sealed record ProviderProbeReport(
    string BaseUrl,
    string ModelSlug,
    CodexCompatibilityLevel CompatibilityLevel,
    ProviderProbeOutcome ProbeOutcome,
    CapabilityEvidence ModelsEndpoint,
    CapabilityEvidence ResponsesEndpoint,
    CapabilityEvidence BasicCodexTurn,
    CapabilityEvidence BuiltInFunctionTools,
    CapabilityEvidence ExecTool,
    CapabilityEvidence Reasoning,
    CapabilityEvidence Vision,
    CapabilityEvidence StreamingSupport,
    CapabilityEvidence HostedSearchSupport,
    CapabilityEvidence McpNamespaceTools,
    CapabilityEvidence AppsNamespaceTools,
    CapabilityEvidence Plugins,
    CapabilityEvidence MultiAgent,
    DateTimeOffset ProbedAt,
    string? CodexRuntimeIdentity,
    List<string>? DiscoveredModelIds = null,
    string? DiagnosticSummary = null,
    CapabilityEvidence? CustomApplyPatchSupport = null,
    CapabilityEvidence? ToolSearchSupport = null,
    CapabilityEvidence? StandaloneSearchSupport = null)
{
    // Backwards-compatibility alias mapping ProbeOutcome to ResponsesFailureClassification
    public ResponsesFailureClassification ResponsesStatus => ProbeOutcome switch
    {
        ProviderProbeOutcome.Success => ResponsesFailureClassification.ResponsesPassed,
        ProviderProbeOutcome.AuthenticationFailed => ResponsesFailureClassification.AuthenticationFailed,
        ProviderProbeOutcome.RateLimited => ResponsesFailureClassification.RateLimited,
        ProviderProbeOutcome.UpstreamUnavailable => ResponsesFailureClassification.UpstreamUnavailable,
        ProviderProbeOutcome.ModelUnavailable => ResponsesFailureClassification.ModelUnavailable,
        ProviderProbeOutcome.PayloadRejected => ResponsesFailureClassification.PayloadRejected,
        ProviderProbeOutcome.CodexEnvelopeRejected => ResponsesFailureClassification.CodexEnvelopeRejected,
        ProviderProbeOutcome.EndpointMissing => ResponsesFailureClassification.EndpointMissing,
        _ => ResponsesFailureClassification.None,
    };

    // Backwards-compatibility aliases and helper properties
    public CapabilityEvidence CodexRuntimeSmokeTest => BasicCodexTurn;
    public CapabilityEvidence ToolCallingSupport => BuiltInFunctionTools;
    public CapabilityEvidence ReasoningSupport => Reasoning;
    public CapabilityEvidence VisionSupport => Vision;
    public CapabilityEvidence NamespaceToolsSupport => McpNamespaceTools;
    public CapabilityEvidence ModelActuallyUsed => BasicCodexTurn;
    public CapabilityEvidence ProviderActuallyUsed => BasicCodexTurn;
    public CapabilityEvidence TokenUsageVerified => BasicCodexTurn;
    public CapabilityEvidence BuiltInToolsExposed => ExecTool;
    public CapabilityEvidence CustomApplyPatch => CustomApplyPatchSupport ?? CapabilityEvidence.Unknown("Custom apply_patch not probed");
    public CapabilityEvidence ToolSearch => ToolSearchSupport ?? CapabilityEvidence.Unknown("Tool search not probed");
    public CapabilityEvidence StandaloneSearch => StandaloneSearchSupport ?? CapabilityEvidence.Unknown("Standalone search not qualified");

    public bool AllStandardChecksPassed =>
        ModelsEndpoint.IsSupported &&
        ResponsesEndpoint.IsSupported &&
        (BasicCodexTurn.State == CapabilityEvidenceState.Unknown || BasicCodexTurn.IsSupported) &&
        (ExecTool.State == CapabilityEvidenceState.Unknown || ExecTool.IsSupported);

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
            ["probeOutcome"] = ProbeOutcome.ToString(),
            ["responsesStatus"] = ResponsesStatus.ToString(),
            ["sandboxPolicy"] = "workspace-write",
            ["networkAccessEnforced"] = false,
            ["codexRuntimeIdentity"] = CodexRuntimeIdentity,
            ["probedAt"] = ProbedAt.ToString("O"),
            ["capabilities"] = new Dictionary<string, object?>
            {
                ["modelsEndpoint"] = FormatEvidence(ModelsEndpoint),
                ["responsesEndpoint"] = FormatEvidence(ResponsesEndpoint),
                ["basicCodexTurn"] = FormatEvidence(BasicCodexTurn),
                ["builtInFunctionTools"] = FormatEvidence(BuiltInFunctionTools),
                ["execTool"] = FormatEvidence(ExecTool),
                ["reasoning"] = FormatEvidence(Reasoning),
                ["vision"] = FormatEvidence(Vision),
                ["streaming"] = FormatEvidence(StreamingSupport),
                ["hostedSearch"] = FormatEvidence(HostedSearchSupport),
                ["customApplyPatch"] = FormatEvidence(CustomApplyPatch),
                ["toolSearch"] = FormatEvidence(ToolSearch),
                ["standaloneSearch"] = FormatEvidence(StandaloneSearch),
                ["mcpNamespaceTools"] = FormatEvidence(McpNamespaceTools),
                ["appsNamespaceTools"] = FormatEvidence(AppsNamespaceTools),
                ["plugins"] = FormatEvidence(Plugins),
                ["multiAgent"] = FormatEvidence(MultiAgent),
                // Legacy compatibility keys
                ["codexSmokeTest"] = FormatEvidence(CodexRuntimeSmokeTest),
                ["toolCalling"] = FormatEvidence(ToolCallingSupport),
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

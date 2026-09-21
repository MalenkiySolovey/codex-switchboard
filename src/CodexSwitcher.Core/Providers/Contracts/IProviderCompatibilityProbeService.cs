using System;
using System.Threading;
using System.Threading.Tasks;
using CodexSwitcher.Core.Providers.Models;

namespace CodexSwitcher.Core.Providers.Contracts;

/// <summary>
/// Options for executing cheap diagnostic capability probes against an API provider.
/// Hosted search and external tooling probes must be explicitly opt-in to avoid unexpected billing.
/// </summary>
public sealed record ProviderProbeOptions(
    bool IncludeVision = true,
    bool IncludeHostedSearch = false,
    TimeSpan Timeout = default);

/// <summary>
/// Diagnostic report summarizing results from cheap provider capability probes.
/// </summary>
public sealed record ProviderProbeReport(
    string BaseUrl,
    string ModelSlug,
    CapabilityEvidence ModelsEndpoint,
    CapabilityEvidence ResponsesEndpoint,
    CapabilityEvidence ReasoningSupport,
    CapabilityEvidence VisionSupport,
    CapabilityEvidence ToolCallingSupport,
    CapabilityEvidence HostedSearchSupport,
    CapabilityEvidence CodexRuntimeSmokeTest,
    DateTimeOffset ProbedAt,
    string? CodexRuntimeIdentity)
{
    public bool AllStandardChecksPassed =>
        ModelsEndpoint.IsSupported &&
        ResponsesEndpoint.IsSupported &&
        ToolCallingSupport.IsSupported;
}

/// <summary>
/// Service coordinating cheap compatibility probes against provider endpoints:
/// /models, minimal /responses, reasoning parameters, optional vision, ordinary tool calling,
/// and local Codex runtime smoke tests.
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

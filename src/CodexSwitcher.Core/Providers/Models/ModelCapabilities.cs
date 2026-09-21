using System;

namespace CodexSwitcher.Core.Providers.Models;

/// <summary>
/// Verified model-level capabilities independent of provider route passthrough limitations.
/// </summary>
public sealed record ModelCapabilities(
    string ModelSlug,
    CapabilityEvidence Reasoning,
    CapabilityEvidence Vision,
    CapabilityEvidence ToolCalling,
    CapabilityEvidence MaxContext)
{
    public static ModelCapabilities ForGrok46(string? runtimeIdentity = null) =>
        new(
            "grok-4.6",
            CapabilityEvidence.ProbePassed("Modelflare live qualification", runtimeIdentity, "Reasoning effort parameters accepted"),
            CapabilityEvidence.ProbePassed("Modelflare live qualification", runtimeIdentity, "Vision images accepted through Codex"),
            CapabilityEvidence.ProbePassed("Modelflare live qualification", runtimeIdentity, "Ordinary exec tool calling accepted"),
            CapabilityEvidence.Documented("Official xAI Grok 4.6 documentation", "500k context limit"));

    public static ModelCapabilities ForGrok420(string? runtimeIdentity = null) =>
        new(
            "grok-4.20",
            CapabilityEvidence.Documented("Official xAI documentation", "Reasoning effort supported"),
            CapabilityEvidence.Documented("Official xAI documentation", "Vision supported"),
            CapabilityEvidence.Documented("Official xAI documentation", "Tool calling supported"),
            CapabilityEvidence.Documented("Official xAI Grok 4.20 documentation", "1M context limit"));

    public static ModelCapabilities ForUnknown(string modelSlug) =>
        new(
            modelSlug,
            CapabilityEvidence.Unknown("Unverified third-party model"),
            CapabilityEvidence.Unknown("Unverified third-party model"),
            CapabilityEvidence.Unknown("Unverified third-party model"),
            CapabilityEvidence.Unknown("Unverified third-party model"));
}

/// <summary>
/// Verified route/provider passthrough capabilities.
/// Crucial: Route capabilities MUST NOT be inferred simply from model capabilities.
/// </summary>
public sealed record RouteCapabilities(
    string RouteId,
    string BaseUrl,
    CapabilityEvidence Responses,
    CapabilityEvidence Streaming,
    CapabilityEvidence WebSockets,
    CapabilityEvidence HostedWebSearch,
    CapabilityEvidence ToolCallingPassthrough,
    CapabilityEvidence VisionPassthrough)
{
    /// <summary>
    /// Live qualification evidence for Modelflare routing Grok 4.6:
    /// Responses, reasoning, vision, and tool calling pass, but provider-hosted
    /// web_search passthrough fails (HTTP 400 Bad Request).
    /// </summary>
    public static RouteCapabilities ForModelflareGrok46(string baseUrl, string? runtimeIdentity = null) =>
        new(
            "modelflare-grok-4.6",
            baseUrl,
            Responses: CapabilityEvidence.ProbePassed("Modelflare live qualification", runtimeIdentity, "Responses endpoint accepted"),
            Streaming: CapabilityEvidence.ProbePassed("Modelflare live qualification", runtimeIdentity, "SSE streaming accepted"),
            WebSockets: CapabilityEvidence.Unknown("WebSocket route not probed"),
            HostedWebSearch: CapabilityEvidence.ProbeFailed("Modelflare live qualification", runtimeIdentity, "HTTP 400 Bad Request on provider-hosted xAI web_search passthrough"),
            ToolCallingPassthrough: CapabilityEvidence.ProbePassed("Modelflare live qualification", runtimeIdentity, "Tool calling passthrough functional"),
            VisionPassthrough: CapabilityEvidence.ProbePassed("Modelflare live qualification", runtimeIdentity, "Vision payload passthrough functional"));
}

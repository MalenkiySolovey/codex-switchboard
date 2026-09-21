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
    CapabilityEvidence MaxContext,
    CapabilityEvidence TextInput,
    CapabilityEvidence ApplyPatch,
    CapabilityEvidence ShellExec,
    CapabilityEvidence MultiAgent)
{
    public ModelCapabilities(
        string modelSlug,
        CapabilityEvidence reasoning,
        CapabilityEvidence vision,
        CapabilityEvidence toolCalling,
        CapabilityEvidence maxContext)
        : this(
            modelSlug,
            reasoning,
            vision,
            toolCalling,
            maxContext,
            CapabilityEvidence.Documented("Default model text input"),
            CapabilityEvidence.Unknown("apply_patch support not probed"),
            CapabilityEvidence.Documented("Standard shell execution"),
            CapabilityEvidence.Unknown("Multi-agent capability not probed"))
    {
    }

    public static ModelCapabilities ForGrok46(string? runtimeIdentity = null) =>
        new(
            "grok-4.6",
            CapabilityEvidence.ProbePassed("Modelflare live qualification", runtimeIdentity, "Reasoning effort parameters accepted"),
            CapabilityEvidence.ProbePassed("Modelflare live qualification", runtimeIdentity, "Vision images accepted through Codex"),
            CapabilityEvidence.ProbePassed("Modelflare live qualification", runtimeIdentity, "Ordinary exec tool calling accepted"),
            CapabilityEvidence.Documented("Official xAI Grok 4.6 documentation", "500k context limit"),
            CapabilityEvidence.Documented("Official xAI Grok 4.6 documentation", "Text input supported"),
            CapabilityEvidence.ProbePassed("Modelflare live qualification", runtimeIdentity, "apply_patch tool supported"),
            CapabilityEvidence.ProbePassed("Modelflare live qualification", runtimeIdentity, "exec command execution supported"),
            CapabilityEvidence.Unknown("Multi-agent delegation unprobed"));

    public static ModelCapabilities ForGrok420(string? runtimeIdentity = null) =>
        new(
            "grok-4.20",
            CapabilityEvidence.Documented("Official xAI documentation", "Reasoning effort supported"),
            CapabilityEvidence.Documented("Official xAI documentation", "Vision supported"),
            CapabilityEvidence.Documented("Official xAI documentation", "Tool calling supported"),
            CapabilityEvidence.Documented("Official xAI Grok 4.20 documentation", "1M context limit"),
            CapabilityEvidence.Documented("Official xAI documentation", "Text input supported"),
            CapabilityEvidence.Documented("Official xAI documentation", "apply_patch supported"),
            CapabilityEvidence.Documented("Official xAI documentation", "exec command execution supported"),
            CapabilityEvidence.Unknown("Multi-agent delegation unprobed"));

    public static ModelCapabilities ForDeepSeek(string modelSlug = "deepseek-reasoner", string? runtimeIdentity = null) =>
        new(
            modelSlug,
            CapabilityEvidence.Documented("Official DeepSeek documentation", "DeepSeek reasoning model"),
            CapabilityEvidence.Unknown("Vision not supported on reasoning model"),
            CapabilityEvidence.Documented("Official DeepSeek documentation", "Function tools supported"),
            CapabilityEvidence.Documented("Official DeepSeek documentation", "128k context limit"),
            CapabilityEvidence.Documented("Official DeepSeek documentation", "Text input supported"),
            CapabilityEvidence.Documented("Official DeepSeek documentation", "Standard diff apply supported"),
            CapabilityEvidence.Documented("Official DeepSeek documentation", "Shell execution supported"),
            CapabilityEvidence.Unknown("Multi-agent delegation unprobed"));

    public static ModelCapabilities ForUnknown(string modelSlug) =>
        new(
            modelSlug,
            CapabilityEvidence.Unknown("Unverified third-party model"),
            CapabilityEvidence.Unknown("Unverified third-party model"),
            CapabilityEvidence.Unknown("Unverified third-party model"),
            CapabilityEvidence.Unknown("Unverified third-party model"),
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
    CapabilityEvidence VisionPassthrough,
    CapabilityEvidence NamespaceTools,
    CapabilityEvidence PromptCaching,
    CapabilityEvidence Mcp,
    CapabilityEvidence AppsPlugins)
{
    public RouteCapabilities(
        string routeId,
        string baseUrl,
        CapabilityEvidence responses,
        CapabilityEvidence streaming,
        CapabilityEvidence webSockets,
        CapabilityEvidence hostedWebSearch,
        CapabilityEvidence toolCallingPassthrough,
        CapabilityEvidence visionPassthrough)
        : this(
            routeId,
            baseUrl,
            responses,
            streaming,
            webSockets,
            hostedWebSearch,
            toolCallingPassthrough,
            visionPassthrough,
            CapabilityEvidence.Unknown("Namespace tools unverified"),
            CapabilityEvidence.Unknown("Prompt caching unverified"),
            CapabilityEvidence.Unknown("MCP unverified"),
            CapabilityEvidence.Unknown("Apps/plugins unverified"))
    {
    }

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
            VisionPassthrough: CapabilityEvidence.ProbePassed("Modelflare live qualification", runtimeIdentity, "Vision payload passthrough functional"),
            NamespaceTools: CapabilityEvidence.ProbeFailed("Modelflare live qualification", runtimeIdentity, "Third-party proxy rejects proprietary OpenAI namespace tools"),
            PromptCaching: CapabilityEvidence.Unknown("Prompt caching not declared"),
            Mcp: CapabilityEvidence.ProbePassed("Modelflare live qualification", runtimeIdentity, "Standard MCP functions supported"),
            AppsPlugins: CapabilityEvidence.Unknown("Plugins unprobed"));

    public static RouteCapabilities ForGenericResponses(string baseUrl, string? runtimeIdentity = null) =>
        new(
            "generic-responses",
            baseUrl,
            Responses: CapabilityEvidence.Unknown("Responses endpoint unverified"),
            Streaming: CapabilityEvidence.Unknown("Streaming unverified"),
            WebSockets: CapabilityEvidence.Unknown("WebSockets unverified"),
            HostedWebSearch: CapabilityEvidence.Unknown("Hosted web search unverified"),
            ToolCallingPassthrough: CapabilityEvidence.Unknown("Tool calling unverified"),
            VisionPassthrough: CapabilityEvidence.Unknown("Vision unverified"),
            NamespaceTools: CapabilityEvidence.Unknown("Namespace tools unverified"),
            PromptCaching: CapabilityEvidence.Unknown("Prompt caching unverified"),
            Mcp: CapabilityEvidence.Unknown("MCP unverified"),
            AppsPlugins: CapabilityEvidence.Unknown("Apps/plugins unverified"));
}

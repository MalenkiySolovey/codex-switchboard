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
public sealed record RouteCapabilities
{
    public string RouteId { get; init; }
    public string BaseUrl { get; init; }
    public CapabilityEvidence Responses { get; init; }
    public CapabilityEvidence Streaming { get; init; }
    public CapabilityEvidence WebSockets { get; init; }
    public CapabilityEvidence HostedWebSearch { get; init; }
    public CapabilityEvidence StandardFunctionTools { get; init; }
    public CapabilityEvidence VisionPassthrough { get; init; }
    public CapabilityEvidence CustomFreeformTools { get; init; }
    public CapabilityEvidence ApplyPatchFreeform { get; init; }
    public CapabilityEvidence ToolSearch { get; init; }
    public CapabilityEvidence StandaloneWebSearch { get; init; }
    public CapabilityEvidence NamespaceTools { get; init; }
    public CapabilityEvidence PromptCaching { get; init; }
    public CapabilityEvidence Mcp { get; init; }
    public CapabilityEvidence AppsPlugins { get; init; }

    public CapabilityEvidence ToolCallingPassthrough => StandardFunctionTools;

    public RouteCapabilities(
        string routeId,
        string baseUrl,
        CapabilityEvidence responses,
        CapabilityEvidence streaming,
        CapabilityEvidence webSockets,
        CapabilityEvidence hostedWebSearch,
        CapabilityEvidence standardFunctionTools,
        CapabilityEvidence visionPassthrough,
        CapabilityEvidence customFreeformTools,
        CapabilityEvidence applyPatchFreeform,
        CapabilityEvidence toolSearch,
        CapabilityEvidence standaloneWebSearch,
        CapabilityEvidence namespaceTools,
        CapabilityEvidence promptCaching,
        CapabilityEvidence mcp,
        CapabilityEvidence appsPlugins)
    {
        RouteId = routeId;
        BaseUrl = baseUrl;
        Responses = responses;
        Streaming = streaming;
        WebSockets = webSockets;
        HostedWebSearch = hostedWebSearch;
        StandardFunctionTools = standardFunctionTools;
        VisionPassthrough = visionPassthrough;
        CustomFreeformTools = customFreeformTools;
        ApplyPatchFreeform = applyPatchFreeform;
        ToolSearch = toolSearch;
        StandaloneWebSearch = standaloneWebSearch;
        NamespaceTools = namespaceTools;
        PromptCaching = promptCaching;
        Mcp = mcp;
        AppsPlugins = appsPlugins;
    }

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
            CapabilityEvidence.Unknown("Custom freeform tools unverified"),
            CapabilityEvidence.Unknown("Native apply_patch unverified"),
            CapabilityEvidence.Unknown("Tool search unverified"),
            CapabilityEvidence.Unknown("Standalone web search unverified"),
            CapabilityEvidence.Unknown("Namespace tools unverified"),
            CapabilityEvidence.Unknown("Prompt caching unverified"),
            CapabilityEvidence.Unknown("MCP unverified"),
            CapabilityEvidence.Unknown("Apps/plugins unverified"))
    {
    }

    public RouteCapabilities(
        string routeId,
        string baseUrl,
        CapabilityEvidence responses,
        CapabilityEvidence streaming,
        CapabilityEvidence webSockets,
        CapabilityEvidence hostedWebSearch,
        CapabilityEvidence toolCallingPassthrough,
        CapabilityEvidence visionPassthrough,
        CapabilityEvidence namespaceTools,
        CapabilityEvidence promptCaching,
        CapabilityEvidence mcp,
        CapabilityEvidence appsPlugins)
        : this(
            routeId,
            baseUrl,
            responses,
            streaming,
            webSockets,
            hostedWebSearch,
            toolCallingPassthrough,
            visionPassthrough,
            CapabilityEvidence.Unknown("Custom freeform tools unverified"),
            CapabilityEvidence.Unknown("Native apply_patch unverified"),
            CapabilityEvidence.Unknown("Tool search unverified"),
            CapabilityEvidence.Unknown("Standalone web search unverified"),
            namespaceTools,
            promptCaching,
            mcp,
            appsPlugins)
    {
    }

    /// <summary>
    /// Live qualification evidence for Modelflare routing Grok 4.6:
    /// Responses, reasoning, vision, and standard function tools pass, but
    /// custom apply_patch, tool_search, and provider-hosted web_search fail (HTTP 400 Bad Request).
    /// </summary>
    public static RouteCapabilities ForModelflareGrok46(string baseUrl, string? runtimeIdentity = null) =>
        new(
            routeId: "modelflare-grok-4.6",
            baseUrl: baseUrl,
            responses: CapabilityEvidence.ProbePassed("Modelflare live qualification", runtimeIdentity, "Responses endpoint accepted"),
            streaming: CapabilityEvidence.ProbePassed("Modelflare live qualification", runtimeIdentity, "SSE streaming accepted"),
            webSockets: CapabilityEvidence.Unknown("WebSocket route not probed"),
            hostedWebSearch: CapabilityEvidence.ProbeFailed("Modelflare live qualification", runtimeIdentity, "HTTP 400 Bad Request on provider-hosted xAI web_search passthrough"),
            standardFunctionTools: CapabilityEvidence.ProbePassed("Modelflare live qualification", runtimeIdentity, "Standard function tools accepted"),
            visionPassthrough: CapabilityEvidence.ProbePassed("Modelflare live qualification", runtimeIdentity, "Vision payload passthrough functional"),
            customFreeformTools: CapabilityEvidence.ProbeFailed("Modelflare live qualification", runtimeIdentity, "HTTP 400 Bad Request on custom freeform tools"),
            applyPatchFreeform: CapabilityEvidence.ProbeFailed("Modelflare live qualification", runtimeIdentity, "HTTP 400 Bad Request on native freeform apply_patch"),
            toolSearch: CapabilityEvidence.ProbeFailed("Modelflare live qualification", runtimeIdentity, "HTTP 400 Bad Request on tool_search"),
            standaloneWebSearch: CapabilityEvidence.Unknown("Standalone search not qualified for Modelflare"),
            namespaceTools: CapabilityEvidence.ProbeFailed("Modelflare live qualification", runtimeIdentity, "Third-party proxy rejects proprietary OpenAI namespace tools"),
            promptCaching: CapabilityEvidence.Unknown("Prompt caching not declared"),
            mcp: CapabilityEvidence.ProbePassed("Modelflare live qualification", runtimeIdentity, "Standard MCP functions supported"),
            appsPlugins: CapabilityEvidence.Unknown("Plugins unprobed"));

    public static RouteCapabilities ForGenericResponses(string baseUrl, string? runtimeIdentity = null) =>
        new(
            routeId: "generic-responses",
            baseUrl: baseUrl,
            responses: CapabilityEvidence.Unknown("Responses endpoint unverified"),
            streaming: CapabilityEvidence.Unknown("Streaming unverified"),
            webSockets: CapabilityEvidence.Unknown("WebSockets unverified"),
            hostedWebSearch: CapabilityEvidence.Unknown("Hosted web search unverified"),
            standardFunctionTools: CapabilityEvidence.Unknown("Standard function tools unverified"),
            visionPassthrough: CapabilityEvidence.Unknown("Vision unverified"),
            customFreeformTools: CapabilityEvidence.Unknown("Custom freeform tools unverified"),
            applyPatchFreeform: CapabilityEvidence.Unknown("Native apply_patch unverified"),
            toolSearch: CapabilityEvidence.Unknown("Tool search unverified"),
            standaloneWebSearch: CapabilityEvidence.Unknown("Standalone web search unverified"),
            namespaceTools: CapabilityEvidence.Unknown("Namespace tools unverified"),
            promptCaching: CapabilityEvidence.Unknown("Prompt caching unverified"),
            mcp: CapabilityEvidence.Unknown("MCP unverified"),
            appsPlugins: CapabilityEvidence.Unknown("Apps/plugins unverified"));
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CodexSwitcher.Core.Common.Storage;
using CodexSwitcher.Core.Providers.Contracts;
using CodexSwitcher.Core.Providers.Models;
using CodexSwitcher.Core.Routing.Contracts;
using CodexSwitcher.Core.Routing.Models;
using CodexSwitcher.Infra.Common.Storage;

namespace CodexSwitcher.Infra.Providers.Inspection;

public delegate Task<(int ExitCode, string Stdout, string Stderr)> ProcessRunnerFunc(ProcessStartInfo psi, CancellationToken ct);

/// <summary>
/// Executes dual-layer compatibility qualification for Codex providers:
/// Layer 1: HTTP protocol qualification (/models, /responses, streaming, reasoning, vision, tools, hosted search)
/// Layer 2: Real isolated Codex execution smoke test in a temporary CODEX_HOME
/// </summary>
public sealed class ProviderCompatibilityProbeService : IProviderCompatibilityProbeService
{
    private static readonly char[] s_lineDelimiters = ['\r', '\n'];
    private static readonly JsonSerializerOptions s_indentedJsonOpts = new() { WriteIndented = true };
    private readonly HttpClient _httpClient;
    private readonly ICodexRuntimeResolver? _runtimeResolver;
    private readonly IFileSystem _fs;
    private readonly ProcessRunnerFunc _processRunner;

    public ProviderCompatibilityProbeService(
        HttpClient? httpClient = null,
        ICodexRuntimeResolver? runtimeResolver = null,
        IFileSystem? fs = null,
        ProcessRunnerFunc? processRunner = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _runtimeResolver = runtimeResolver;
        _fs = fs ?? new PhysicalFileSystem();
        _processRunner = processRunner ?? DefaultProcessRunnerAsync;
    }

    public async Task<ProviderProbeReport> ProbeCompatibilityAsync(
        string baseUrl,
        string apiKey,
        string modelSlug,
        ProviderProbeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelSlug);

        options ??= new ProviderProbeOptions();
        var timeout = options.Timeout > TimeSpan.Zero ? options.Timeout : TimeSpan.FromSeconds(12);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        var runtimeInfo = _runtimeResolver?.ResolveCurrentRuntime();
        var runtimeIdentity = runtimeInfo != null
            ? $"{runtimeInfo.ExecutablePath}|{runtimeInfo.Version}"
            : "unknown";

        var cleanBaseUrl = baseUrl.TrimEnd('/');

        // 1. Probe /models
        var (modelsEvidence, discoveredModelIds) = await ProbeModelsAsync(cleanBaseUrl, apiKey, cts.Token).ConfigureAwait(false);

        // 2. Probe /responses (minimal text ping) with fine-grained outcome classification
        var (responsesEvidence, httpOutcome) = await ProbeMinimalResponsesAsync(cleanBaseUrl, apiKey, modelSlug, cts.Token).ConfigureAwait(false);

        // 3. Probe streaming Responses (if enabled)
        var streamingEvidence = options.IncludeStreaming
            ? await ProbeStreamingResponsesAsync(cleanBaseUrl, apiKey, modelSlug, cts.Token).ConfigureAwait(false)
            : CapabilityEvidence.Unknown("Streaming probe not requested");

        // 4. Probe reasoning
        var reasoningEvidence = await ProbeReasoningAsync(cleanBaseUrl, apiKey, modelSlug, cts.Token).ConfigureAwait(false);

        // 5. Probe vision (if enabled)
        var visionEvidence = options.IncludeVision
            ? await ProbeVisionAsync(cleanBaseUrl, apiKey, modelSlug, cts.Token).ConfigureAwait(false)
            : CapabilityEvidence.Unknown("Vision check skipped by probe options");

        // 6. Probe ordinary tool calling via HTTP protocol
        var httpToolCallingEvidence = await ProbeToolCallingAsync(cleanBaseUrl, apiKey, modelSlug, cts.Token).ConfigureAwait(false);
        if (!httpToolCallingEvidence.IsSupported && httpOutcome == ProviderProbeOutcome.Success)
        {
            if (httpToolCallingEvidence.Detail != null &&
                (httpToolCallingEvidence.Detail.Contains("namespace", StringComparison.OrdinalIgnoreCase) ||
                 httpToolCallingEvidence.Detail.Contains("unsupported tool", StringComparison.OrdinalIgnoreCase) ||
                 httpToolCallingEvidence.Detail.Contains("schema", StringComparison.OrdinalIgnoreCase)))
            {
                httpOutcome = ProviderProbeOutcome.CodexEnvelopeRejected;
            }
        }

        // 6a. Schema probes for proprietary tool types
        var customApplyPatchEvidence = await ProbeCustomApplyPatchAsync(cleanBaseUrl, apiKey, modelSlug, cts.Token).ConfigureAwait(false);
        var toolSearchEvidence = await ProbeToolSearchAsync(cleanBaseUrl, apiKey, modelSlug, cts.Token).ConfigureAwait(false);
        var standaloneSearchEvidence = options.IncludeStandaloneSearch
            ? await ProbeStandaloneSearchAsync(cleanBaseUrl, apiKey, cts.Token).ConfigureAwait(false)
            : CapabilityEvidence.Unknown("Standalone search not verified (opt-in)");

        // 7. Probe hosted search (strictly opt-in to avoid unexpected billing)
        var hostedSearchEvidence = options.IncludeHostedSearch
            ? await ProbeHostedSearchAsync(cleanBaseUrl, apiKey, modelSlug, cts.Token).ConfigureAwait(false)
            : CapabilityEvidence.Unknown("Hosted search probe not requested (opt-in to avoid extra billing)");

        // 8. Layer 2 — Real isolated Codex runtime execution (Phase A: basic turn, Phase B: real tool smoke)
        var (basicTurnEvidence, execToolEvidence, functionToolsEvidence, mcpToolsEvidence, layer2Outcome) =
            options.RunCodexSmokeTest
                ? await ProbeCodexRealExecutionAsync(runtimeInfo, cleanBaseUrl, apiKey, modelSlug, options.RunToolSmokeTest, cts.Token).ConfigureAwait(false)
                : (CapabilityEvidence.Unknown("Codex smoke test skipped by options"),
                   CapabilityEvidence.Unknown("Tool smoke skipped by options"),
                   httpToolCallingEvidence,
                   CapabilityEvidence.Unknown("MCP namespace tools unverified (advanced probe)"),
                   null);

        // 9. Synthesize overall compatibility level and probe outcome
        var (level, finalOutcome, summary) = SynthesizeCompatibility(
            httpOutcome,
            responsesEvidence,
            basicTurnEvidence,
            execToolEvidence,
            layer2Outcome);

        var appsToolsEvidence = CapabilityEvidence.Unknown("Apps namespace tools unverified (advanced probe)");
        var pluginsEvidence = CapabilityEvidence.Unknown("Plugins unverified (advanced probe)");
        var multiAgentEvidence = CapabilityEvidence.Unknown("Multi-agent unverified (advanced probe)");

        return new ProviderProbeReport(
            cleanBaseUrl,
            modelSlug,
            level,
            finalOutcome,
            modelsEvidence,
            responsesEvidence,
            basicTurnEvidence,
            functionToolsEvidence.IsSupported ? functionToolsEvidence : httpToolCallingEvidence,
            execToolEvidence,
            reasoningEvidence,
            visionEvidence,
            streamingEvidence,
            hostedSearchEvidence,
            mcpToolsEvidence,
            appsToolsEvidence,
            pluginsEvidence,
            multiAgentEvidence,
            DateTimeOffset.UtcNow,
            runtimeIdentity,
            discoveredModelIds,
            summary,
            CustomApplyPatchSupport: customApplyPatchEvidence,
            ToolSearchSupport: toolSearchEvidence,
            StandaloneSearchSupport: standaloneSearchEvidence);
    }

    private static (CodexCompatibilityLevel Level, ProviderProbeOutcome Outcome, string Summary) SynthesizeCompatibility(
        ProviderProbeOutcome httpOutcome,
        CapabilityEvidence responses,
        CapabilityEvidence basicTurn,
        CapabilityEvidence execTool,
        ProviderProbeOutcome? layer2Outcome)
    {
        switch (httpOutcome)
        {
            case ProviderProbeOutcome.EndpointMissing:
                return (CodexCompatibilityLevel.NotCompatible, ProviderProbeOutcome.EndpointMissing,
                    "Not compatible with Codex: /responses endpoint not found (HTTP 404). Provider does not support Codex Responses API.");

            case ProviderProbeOutcome.AuthenticationFailed:
                return (CodexCompatibilityLevel.Unknown, ProviderProbeOutcome.AuthenticationFailed,
                    "Authentication failed (HTTP 401/403): Provider rejected the API key; compatibility is unverified.");

            case ProviderProbeOutcome.RateLimited:
                return (CodexCompatibilityLevel.Unknown, ProviderProbeOutcome.RateLimited,
                    "Rate limit exceeded (HTTP 429) during qualification; compatibility is unverified.");

            case ProviderProbeOutcome.UpstreamUnavailable:
                return (CodexCompatibilityLevel.Unknown, ProviderProbeOutcome.UpstreamUnavailable,
                    "Upstream provider server error or connection failure; compatibility is unverified.");

            case ProviderProbeOutcome.ModelUnavailable:
                return (CodexCompatibilityLevel.PartiallyCompatible, ProviderProbeOutcome.ModelUnavailable,
                    "Partially compatible: /responses endpoint is reachable, but the requested model is not available or rejected.");

            case ProviderProbeOutcome.PayloadRejected:
                return (CodexCompatibilityLevel.PartiallyCompatible, ProviderProbeOutcome.PayloadRejected,
                    "Partially compatible: /responses endpoint reachable, but request payload parameters were rejected.");

            case ProviderProbeOutcome.CodexEnvelopeRejected:
                return (CodexCompatibilityLevel.PartiallyCompatible, ProviderProbeOutcome.CodexEnvelopeRejected,
                    "Partially compatible: Provider rejected custom Codex tool calling or namespace envelope.");

            case ProviderProbeOutcome.Success:
            default:
                if (!responses.IsSupported)
                {
                    return (CodexCompatibilityLevel.NotCompatible, ProviderProbeOutcome.EndpointMissing,
                        "Not compatible with Codex: /responses endpoint is not supported or rejected the request.");
                }

                // Check Layer 2 outcome
                if (layer2Outcome == ProviderProbeOutcome.LocalSandboxBlocked)
                {
                    return (CodexCompatibilityLevel.Unknown, ProviderProbeOutcome.LocalSandboxBlocked,
                        "Local probe-environment failure: Codex sandbox prevented workspace-write execution on this system; provider compatibility is unverified.");
                }

                if (layer2Outcome == ProviderProbeOutcome.CodexEnvelopeRejected)
                {
                    return (CodexCompatibilityLevel.PartiallyCompatible, ProviderProbeOutcome.CodexEnvelopeRejected,
                        "Partially compatible: Provider accepted plain Responses inference, but rejected Codex tool envelope.");
                }

                if (basicTurn.IsSupported)
                {
                    if (execTool.IsSupported)
                    {
                        return (CodexCompatibilityLevel.CodexCompatible, ProviderProbeOutcome.Success,
                            "Codex compatible: Direct Responses API verified with real Codex execution and built-in tools.");
                    }

                    if (execTool.State == CapabilityEvidenceState.ProbeFailed)
                    {
                        return (CodexCompatibilityLevel.PartiallyCompatible, ProviderProbeOutcome.CodexEnvelopeRejected,
                            "Partially compatible: Basic Codex turn succeeded, but built-in tool execution sequence failed or was rejected by provider.");
                    }

                    // Exec tool was skipped or unknown
                    return (CodexCompatibilityLevel.CodexCompatible, ProviderProbeOutcome.Success,
                        "Codex compatible: Direct Responses API verified via live execution.");
                }

                if (basicTurn.State == CapabilityEvidenceState.ProbeFailed)
                {
                    return (CodexCompatibilityLevel.PartiallyCompatible, ProviderProbeOutcome.CodexEnvelopeRejected,
                        "Partially compatible: Basic Responses inference passed; Codex runtime execution encountered errors.");
                }

                // Smoke test was skipped/unknown, but responses protocol works
                return (CodexCompatibilityLevel.CodexCompatible, ProviderProbeOutcome.Success,
                    "Codex compatible: Direct Responses API verified via HTTP protocol probe.");
        }
    }

    public Task<(CapabilityEvidence Evidence, List<string> ModelIds)> DiscoverModelsOnlyAsync(
        string baseUrl,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        var cleanBaseUrl = baseUrl.TrimEnd('/');
        return ProbeModelsAsync(cleanBaseUrl, apiKey, cancellationToken);
    }

    private async Task<(CapabilityEvidence Evidence, List<string> ModelIds)> ProbeModelsAsync(string baseUrl, string apiKey, CancellationToken ct)
    {
        var modelIds = new List<string>();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/models");
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }

            using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("data", out var dataElem) && dataElem.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in dataElem.EnumerateArray())
                    {
                        if (item.TryGetProperty("id", out var idProp) && idProp.GetString() is string id && !string.IsNullOrWhiteSpace(id))
                        {
                            modelIds.Add(id.Trim());
                        }
                    }
                }

                var countDetail = modelIds.Count > 0 ? $"{modelIds.Count} models discovered" : "0 models returned";
                return (CapabilityEvidence.ProbePassed("GET /models", detail: $"HTTP {(int)resp.StatusCode} OK ({countDetail})"), modelIds);
            }

            return (CapabilityEvidence.ProbeFailed("GET /models", detail: $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}"), modelIds);
        }
        catch (Exception ex)
        {
            return (CapabilityEvidence.ProbeFailed("GET /models", detail: ex.Message), modelIds);
        }
    }

    private async Task<(CapabilityEvidence Evidence, ProviderProbeOutcome Outcome)>
        ProbeMinimalResponsesAsync(string baseUrl, string apiKey, string model, CancellationToken ct)
    {
        try
        {
            var payload = new
            {
                model,
                input = "ping",
                max_tokens = 5
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/responses")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }

            using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            var statusCode = (int)resp.StatusCode;

            if (resp.IsSuccessStatusCode)
            {
                return (CapabilityEvidence.ProbePassed("POST /responses", detail: $"HTTP {statusCode} OK"), ProviderProbeOutcome.Success);
            }

            var responseBody = string.Empty;
            try
            {
                responseBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                // Non-fatal
            }

            var outcome = ClassifyHttpFailure(statusCode, responseBody);
            var detail = $"HTTP {statusCode} {resp.ReasonPhrase}".Trim();
            if (!string.IsNullOrWhiteSpace(responseBody) && responseBody.Length < 250)
            {
                detail += $": {responseBody.Trim()}";
            }

            return (CapabilityEvidence.ProbeFailed("POST /responses", detail: detail), outcome);
        }
        catch (HttpRequestException ex)
        {
            var outcome = ex.StatusCode.HasValue
                ? ClassifyHttpFailure((int)ex.StatusCode.Value, ex.Message)
                : ProviderProbeOutcome.UpstreamUnavailable;

            return (CapabilityEvidence.ProbeFailed("POST /responses", detail: ex.Message), outcome);
        }
        catch (Exception ex)
        {
            return (CapabilityEvidence.ProbeFailed("POST /responses", detail: ex.Message), ProviderProbeOutcome.UpstreamUnavailable);
        }
    }

    private static ProviderProbeOutcome ClassifyHttpFailure(int statusCode, string body)
    {
        if (statusCode == 404)
        {
            return ProviderProbeOutcome.EndpointMissing;
        }

        if (statusCode is 401 or 403)
        {
            return ProviderProbeOutcome.AuthenticationFailed;
        }

        if (statusCode == 429)
        {
            return ProviderProbeOutcome.RateLimited;
        }

        if (statusCode >= 500)
        {
            return ProviderProbeOutcome.UpstreamUnavailable;
        }

        if (statusCode == 400)
        {
            if (body.Contains("namespace", StringComparison.OrdinalIgnoreCase) ||
                body.Contains("unsupported tool", StringComparison.OrdinalIgnoreCase) ||
                (body.Contains("tool", StringComparison.OrdinalIgnoreCase) && body.Contains("schema", StringComparison.OrdinalIgnoreCase)))
            {
                return ProviderProbeOutcome.CodexEnvelopeRejected;
            }

            if (body.Contains("model", StringComparison.OrdinalIgnoreCase) &&
                (body.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
                 body.Contains("unknown", StringComparison.OrdinalIgnoreCase) ||
                 body.Contains("does not exist", StringComparison.OrdinalIgnoreCase) ||
                 body.Contains("unavailable", StringComparison.OrdinalIgnoreCase) ||
                 body.Contains("invalid", StringComparison.OrdinalIgnoreCase) ||
                 body.Contains("not available", StringComparison.OrdinalIgnoreCase) ||
                 body.Contains("not supported", StringComparison.OrdinalIgnoreCase)))
            {
                return ProviderProbeOutcome.ModelUnavailable;
            }

            return ProviderProbeOutcome.PayloadRejected;
        }

        return ProviderProbeOutcome.PayloadRejected;
    }


    private async Task<CapabilityEvidence> ProbeStreamingResponsesAsync(string baseUrl, string apiKey, string model, CancellationToken ct)
    {
        try
        {
            var payload = new
            {
                model,
                input = "ping",
                stream = true,
                max_tokens = 5
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/responses")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }

            using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                return CapabilityEvidence.ProbePassed("POST /responses (streaming)", detail: $"HTTP {(int)resp.StatusCode} SSE accepted");
            }

            return CapabilityEvidence.ProbeFailed("POST /responses (streaming)", detail: $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
        }
        catch (Exception ex)
        {
            return CapabilityEvidence.ProbeFailed("POST /responses (streaming)", detail: ex.Message);
        }
    }

    private async Task<CapabilityEvidence> ProbeReasoningAsync(string baseUrl, string apiKey, string model, CancellationToken ct)
    {
        try
        {
            var payload = new
            {
                model,
                input = "ping",
                reasoning = new { effort = "low" },
                max_tokens = 5
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/responses")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }

            using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                return CapabilityEvidence.ProbePassed("POST /responses (reasoning)", detail: "Reasoning effort parameter accepted");
            }

            return CapabilityEvidence.ProbeFailed("POST /responses (reasoning)", detail: $"HTTP {(int)resp.StatusCode}");
        }
        catch (Exception ex)
        {
            return CapabilityEvidence.ProbeFailed("POST /responses (reasoning)", detail: ex.Message);
        }
    }

    private async Task<CapabilityEvidence> ProbeVisionAsync(string baseUrl, string apiKey, string model, CancellationToken ct)
    {
        try
        {
            var payload = new
            {
                model,
                input = new object[]
                {
                    new { type = "text", text = "what is this?" },
                    new { type = "image_url", image_url = new { url = "data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7" } }
                },
                max_tokens = 5
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/responses")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }

            using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                return CapabilityEvidence.ProbePassed("POST /responses (vision)", detail: "Vision input accepted");
            }

            return CapabilityEvidence.ProbeFailed("POST /responses (vision)", detail: $"HTTP {(int)resp.StatusCode}");
        }
        catch (Exception ex)
        {
            return CapabilityEvidence.ProbeFailed("POST /responses (vision)", detail: ex.Message);
        }
    }

    private async Task<CapabilityEvidence> ProbeToolCallingAsync(string baseUrl, string apiKey, string model, CancellationToken ct)
    {
        try
        {
            var payload = new
            {
                model,
                input = "What is the weather?",
                tools = new object[]
                {
                    new
                    {
                        type = "function",
                        function = new
                        {
                            name = "get_weather",
                            description = "Get current weather",
                            parameters = new { type = "object", properties = new { } }
                        }
                    }
                },
                max_tokens = 10
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/responses")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }

            using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                return CapabilityEvidence.ProbePassed("POST /responses (tools)", detail: "Standard tool calling accepted");
            }

            return CapabilityEvidence.ProbeFailed("POST /responses (tools)", detail: $"HTTP {(int)resp.StatusCode}");
        }
        catch (Exception ex)
        {
            return CapabilityEvidence.ProbeFailed("POST /responses (tools)", detail: ex.Message);
        }
    }

    private async Task<CapabilityEvidence> ProbeHostedSearchAsync(string baseUrl, string apiKey, string model, CancellationToken ct)
    {
        try
        {
            var payload = new
            {
                model,
                input = "Search for recent news",
                tools = new object[]
                {
                    new { type = "web_search" }
                },
                max_tokens = 10
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/responses")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }

            using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                return CapabilityEvidence.ProbePassed("POST /responses (web_search)", detail: "Provider-hosted web search passthrough functional");
            }

            return CapabilityEvidence.ProbeFailed("POST /responses (web_search)", detail: $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
        }
        catch (Exception ex)
        {
            return CapabilityEvidence.ProbeFailed("POST /responses (web_search)", detail: ex.Message);
        }
    }

    private async Task<CapabilityEvidence> ProbeCustomApplyPatchAsync(string baseUrl, string apiKey, string model, CancellationToken ct)
    {
        try
        {
            var payload = new
            {
                model,
                input = "ping",
                tools = new object[]
                {
                    new
                    {
                        type = "custom",
                        name = "apply_patch",
                        description = "Apply a diff patch",
                        format = new { type = "freeform" }
                    }
                },
                max_tokens = 5
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/responses")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }

            using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                return CapabilityEvidence.ProbePassed("POST /responses (custom apply_patch)", detail: "Custom apply_patch tool accepted");
            }

            return CapabilityEvidence.ProbeFailed("POST /responses (custom apply_patch)", detail: $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
        }
        catch (Exception ex)
        {
            return CapabilityEvidence.ProbeFailed("POST /responses (custom apply_patch)", detail: ex.Message);
        }
    }

    private async Task<CapabilityEvidence> ProbeToolSearchAsync(string baseUrl, string apiKey, string model, CancellationToken ct)
    {
        try
        {
            var payload = new
            {
                model,
                input = "ping",
                tools = new object[]
                {
                    new
                    {
                        type = "tool_search"
                    }
                },
                max_tokens = 5
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/responses")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }

            using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                return CapabilityEvidence.ProbePassed("POST /responses (tool_search)", detail: "Tool search accepted");
            }

            return CapabilityEvidence.ProbeFailed("POST /responses (tool_search)", detail: $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
        }
        catch (Exception ex)
        {
            return CapabilityEvidence.ProbeFailed("POST /responses (tool_search)", detail: ex.Message);
        }
    }

    private async Task<CapabilityEvidence> ProbeStandaloneSearchAsync(string baseUrl, string apiKey, CancellationToken ct)
    {
        try
        {
            var searchUrl = baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
                ? $"{baseUrl}/alpha/search"
                : $"{baseUrl}/v1/alpha/search";

            var payload = new { query = "ping" };
            using var req = new HttpRequestMessage(HttpMethod.Post, searchUrl)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }

            using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                return CapabilityEvidence.ProbePassed("POST /alpha/search (standalone search)", detail: "Standalone search endpoint operational");
            }

            return CapabilityEvidence.ProbeFailed("POST /alpha/search (standalone search)", detail: $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
        }
        catch (Exception ex)
        {
            return CapabilityEvidence.ProbeFailed("POST /alpha/search (standalone search)", detail: ex.Message);
        }
    }

    private async Task<(
        CapabilityEvidence BasicTurn,
        CapabilityEvidence ExecTool,
        CapabilityEvidence FunctionTools,
        CapabilityEvidence McpTools,
        ProviderProbeOutcome? Layer2Outcome)>
        ProbeCodexRealExecutionAsync(
            CodexRuntimeInfo? runtimeInfo,
            string baseUrl,
            string apiKey,
            string modelSlug,
            bool runToolSmoke,
            CancellationToken ct)
    {
        if (runtimeInfo == null || string.IsNullOrWhiteSpace(runtimeInfo.ExecutablePath) || !File.Exists(runtimeInfo.ExecutablePath))
        {
            return (CapabilityEvidence.Unknown("Codex runtime executable not available on this system"),
                    CapabilityEvidence.Unknown("Codex runtime not available"),
                    CapabilityEvidence.Unknown("Codex runtime not available"),
                    CapabilityEvidence.Unknown("Codex runtime not available"),
                    null);
        }

        var runtimeIdentity = $"{runtimeInfo.ExecutablePath}|{runtimeInfo.Version}";
        var tempDir = Path.Combine(Path.GetTempPath(), "CodexSmoke_" + Guid.NewGuid().ToString("N"));

        try
        {
            _fs.CreateDirectory(tempDir);

            // Write conservative model catalog without apply_patch_tool_type and with supports_search_tool = false
            var catalogPath = Path.Combine(tempDir, "catalog.json");
            var catalogContent = JsonSerializer.Serialize(new
            {
                models = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["slug"] = modelSlug,
                        ["display_name"] = modelSlug,
                        ["shell_type"] = "unified_exec",
                        ["visibility"] = "list",
                        ["supported_in_api"] = true,
                        ["priority"] = 1,
                        ["supports_search_tool"] = false,
                        ["supports_parallel_tool_calls"] = true
                    }
                }
            }, s_indentedJsonOpts);
            _fs.WriteAllTextAtomic(catalogPath, catalogContent);

            var toml = new StringBuilder();
            toml.Append("model = \"").Append(modelSlug).AppendLine("\"");
            toml.AppendLine("model_provider = \"probe_provider\"");
            toml.Append("model_catalog_json = \"").Append(catalogPath.Replace('\\', '/')).AppendLine("\"");
            toml.AppendLine("approval_policy = \"never\"");
            toml.AppendLine("sandbox_mode = \"workspace-write\"");
            toml.AppendLine("web_search = \"disabled\"");
            toml.AppendLine();
            toml.AppendLine("[features]");
            toml.AppendLine("multi_agent = false");
            toml.AppendLine();
            toml.AppendLine("[sandbox_workspace_write]");
            toml.AppendLine("network_access = false");
            toml.AppendLine();
            toml.AppendLine("[model_providers.probe_provider]");
            toml.AppendLine("name = \"probe_provider\"");
            toml.Append("base_url = \"").Append(baseUrl).AppendLine("\"");
            toml.AppendLine("wire_api = \"responses\"");
            toml.AppendLine("requires_openai_auth = false");
            toml.AppendLine("env_key = \"CODEX_SMOKE_KEY\"");

            var configPath = Path.Combine(tempDir, "config.toml");
            _fs.WriteAllTextAtomic(configPath, toml.ToString());

            // Phase A: Basic Codex Turn (plain ping) with isolated sandbox
            var basicPsi = new ProcessStartInfo
            {
                FileName = runtimeInfo.ExecutablePath,
                Arguments = "--sandbox workspace-write --ask-for-approval never exec --ephemeral --skip-git-repo-check --json --color never \"ping\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            basicPsi.Environment["CODEX_HOME"] = tempDir;
            basicPsi.Environment["CODEX_SMOKE_KEY"] = apiKey;

            var (basicExit, basicStdout, basicStderr) = await _processRunner(basicPsi, ct).ConfigureAwait(false);

            CapabilityEvidence basicTurnEvidence;
            ProviderProbeOutcome? layer2Outcome = null;

            if (basicExit == 0)
            {
                basicTurnEvidence = CapabilityEvidence.ProbePassed("Codex CLI execution", runtimeIdentity, "Real exec turn completed successfully (exit 0)");
            }
            else
            {
                var combined = (basicStdout + "\n" + basicStderr).Trim();
                if (IsLocalSandboxBlocked(basicExit, basicStdout, basicStderr))
                {
                    basicTurnEvidence = CapabilityEvidence.ProbeFailed("Codex CLI execution", runtimeIdentity, "Local sandbox blocked execution (probe-environment failure)");
                    layer2Outcome = ProviderProbeOutcome.LocalSandboxBlocked;
                }
                else if (combined.Contains("namespace", StringComparison.OrdinalIgnoreCase) ||
                    combined.Contains("unsupported tool", StringComparison.OrdinalIgnoreCase) ||
                    combined.Contains("schema", StringComparison.OrdinalIgnoreCase))
                {
                    basicTurnEvidence = CapabilityEvidence.ProbeFailed("Codex CLI execution", runtimeIdentity, "Provider rejected Codex tool/namespace envelope");
                    layer2Outcome = ProviderProbeOutcome.CodexEnvelopeRejected;
                }
                else
                {
                    var failDetail = !string.IsNullOrWhiteSpace(basicStderr) ? basicStderr.Trim() : $"Exit code {basicExit}";
                    basicTurnEvidence = CapabilityEvidence.ProbeFailed("Codex CLI execution", runtimeIdentity, failDetail);
                    layer2Outcome = ProviderProbeOutcome.PayloadRejected;
                }
            }

            // Phase B: Real Codex Tool Smoke Probe (cheap isolated canary file turn)
            CapabilityEvidence execToolEvidence;
            CapabilityEvidence functionToolsEvidence;

            if (runToolSmoke && basicExit == 0)
            {
                var workspaceDir = Path.Combine(tempDir, "workspace");
                _fs.CreateDirectory(workspaceDir);
                var canaryPath = Path.Combine(workspaceDir, "canary.txt");

                var toolPsi = new ProcessStartInfo
                {
                    FileName = runtimeInfo.ExecutablePath,
                    Arguments = $"--sandbox workspace-write --ask-for-approval never exec --ephemeral --skip-git-repo-check --json --color never -C \"{workspaceDir}\" \"Create a file named canary.txt with text 'canary-ok'\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                toolPsi.Environment["CODEX_HOME"] = tempDir;
                toolPsi.Environment["CODEX_SMOKE_KEY"] = apiKey;

                var (toolExit, toolStdout, toolStderr) = await _processRunner(toolPsi, ct).ConfigureAwait(false);

                var (passed, outcome, detail) = ValidateToolSmokeExecution(toolExit, toolStdout, toolStderr, canaryPath, _fs);

                if (passed)
                {
                    execToolEvidence = CapabilityEvidence.ProbePassed("Codex CLI tool execution", runtimeIdentity, detail);
                    functionToolsEvidence = CapabilityEvidence.ProbePassed("Codex CLI tool execution", runtimeIdentity, "Built-in function calling accepted and executed in isolated workspace");
                    layer2Outcome = ProviderProbeOutcome.Success;
                }
                else
                {
                    layer2Outcome = outcome ?? ProviderProbeOutcome.CodexEnvelopeRejected;
                    execToolEvidence = CapabilityEvidence.ProbeFailed("Codex CLI tool execution", runtimeIdentity, detail);
                    functionToolsEvidence = CapabilityEvidence.ProbeFailed("Codex CLI tool execution", runtimeIdentity, "Built-in function calling failed or rejected by provider");
                }
            }
            else if (!runToolSmoke)
            {
                execToolEvidence = CapabilityEvidence.Unknown("Tool smoke probe skipped by probe options");
                functionToolsEvidence = CapabilityEvidence.Unknown("Tool calling not verified via real execution");
            }
            else
            {
                execToolEvidence = CapabilityEvidence.Unknown("Tool smoke skipped due to basic turn failure");
                functionToolsEvidence = CapabilityEvidence.Unknown("Tool calling skipped due to basic turn failure");
            }

            var mcpToolsEvidence = CapabilityEvidence.Unknown("MCP namespace tools unverified (advanced probe)");

            return (basicTurnEvidence, execToolEvidence, functionToolsEvidence, mcpToolsEvidence, layer2Outcome);
        }
        catch (Exception ex)
        {
            return (CapabilityEvidence.ProbeFailed("Codex CLI execution", runtimeIdentity, ex.Message),
                    CapabilityEvidence.Unknown("Execution error"),
                    CapabilityEvidence.Unknown("Execution error"),
                    CapabilityEvidence.Unknown("Execution error"),
                    null);
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
            catch
            {
                // Non-fatal cleanup
            }
        }
    }

    private static bool IsLocalSandboxBlocked(int exitCode, string stdout, string stderr)
    {
        var combined = (stdout + "\n" + stderr).ToLowerInvariant();
        return (combined.Contains("sandbox") && (
                combined.Contains("failed to start") ||
                combined.Contains("failed to initialize") ||
                combined.Contains("not supported") ||
                combined.Contains("blocked") ||
                combined.Contains("operation not permitted") ||
                combined.Contains("access is denied") ||
                combined.Contains("policy") ||
                combined.Contains("appcontainer") ||
                combined.Contains("seatbelt") ||
                combined.Contains("landlock") ||
                combined.Contains("permission denied") ||
                combined.Contains("cannot execute in sandbox") ||
                combined.Contains("initialization error"))) ||
            combined.Contains("local probe-environment failure") ||
            combined.Contains("sandbox error");
    }

    private static (bool Passed, ProviderProbeOutcome? Outcome, string Detail) ValidateToolSmokeExecution(
        int exitCode,
        string stdout,
        string stderr,
        string canaryPath,
        IFileSystem fs)
    {
        // 1. Check for local sandbox blocking first
        if (IsLocalSandboxBlocked(exitCode, stdout, stderr))
        {
            return (false, ProviderProbeOutcome.LocalSandboxBlocked,
                "Local sandbox blocked tool execution (probe-environment failure)");
        }

        // 2. Check for tool / namespace / schema envelope rejection
        var combined = (stdout + "\n" + stderr).Trim();
        if (combined.Contains("namespace", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("unsupported tool", StringComparison.OrdinalIgnoreCase) ||
            (combined.Contains("tool", StringComparison.OrdinalIgnoreCase) && combined.Contains("schema", StringComparison.OrdinalIgnoreCase)))
        {
            return (false, ProviderProbeOutcome.CodexEnvelopeRejected,
                "Provider rejected Codex tool/namespace envelope");
        }

        // 3. Physical canary file check
        bool canaryFileExists = fs.FileExists(canaryPath);
        bool canaryContentMatches = canaryFileExists &&
            fs.ReadAllText(canaryPath).Contains("canary-ok", StringComparison.OrdinalIgnoreCase);

        // 4. Parse complete JSONL event sequence:
        // Event 1: Model emits tool call
        // Event 2: Codex executes it
        // Event 3: Tool result is returned upstream
        // Event 4: Model produces a subsequent final message
        // Event 5: Turn completes successfully
        bool toolCallEmitted = false;
        bool toolExecuted = false;
        bool toolResultReturnedUpstream = false;
        bool subsequentFinalMessage = false;

        var lines = stdout.Split(s_lineDelimiters, StringSplitOptions.RemoveEmptyEntries);
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith('{') || !line.EndsWith('}'))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var typeStr = root.TryGetProperty("type", out var tp) ? tp.GetString() ?? "" : "";

                // Event 1: Model emits tool call
                if (!toolCallEmitted)
                {
                    if (typeStr.Contains("tool_call", StringComparison.OrdinalIgnoreCase) ||
                        typeStr.Contains("function_call", StringComparison.OrdinalIgnoreCase) ||
                        root.TryGetProperty("tool_calls", out _) ||
                        root.TryGetProperty("function_call", out _) ||
                        (root.TryGetProperty("item", out var itm1) &&
                         itm1.TryGetProperty("type", out var itm1Type) &&
                         (itm1Type.GetString() == "function_call" || itm1Type.GetString() == "tool_call")))
                    {
                        toolCallEmitted = true;
                        continue;
                    }
                }

                // Event 2: Codex executes it
                if (toolCallEmitted && !toolExecuted)
                {
                    if (typeStr.Contains("function_call_output", StringComparison.OrdinalIgnoreCase) ||
                        typeStr.Contains("tool_output", StringComparison.OrdinalIgnoreCase) ||
                        typeStr.Contains("tool_result", StringComparison.OrdinalIgnoreCase) ||
                        typeStr.Contains("execution_completed", StringComparison.OrdinalIgnoreCase) ||
                        (root.TryGetProperty("item", out var itm2) &&
                         itm2.TryGetProperty("type", out var itm2Type) &&
                         (itm2Type.GetString() == "function_call_output" || itm2Type.GetString() == "tool_result")))
                    {
                        toolExecuted = true;
                        continue;
                    }
                }

                // Event 3: Tool result returned upstream
                if (toolExecuted && !toolResultReturnedUpstream)
                {
                    if (typeStr.Equals("item.completed", StringComparison.OrdinalIgnoreCase) ||
                        typeStr.Contains("output_item.completed", StringComparison.OrdinalIgnoreCase) ||
                        typeStr.Contains("response.created", StringComparison.OrdinalIgnoreCase) ||
                        typeStr.Contains("turn.started", StringComparison.OrdinalIgnoreCase))
                    {
                        toolResultReturnedUpstream = true;
                        continue;
                    }
                }

                // Event 4: Model produces subsequent final message
                if (toolExecuted && !subsequentFinalMessage)
                {
                    if (typeStr.Contains("message", StringComparison.OrdinalIgnoreCase) ||
                        (root.TryGetProperty("role", out var role) && role.GetString() == "assistant" && !root.TryGetProperty("tool_calls", out _)) ||
                        (root.TryGetProperty("item", out var itm3) &&
                         itm3.TryGetProperty("type", out var itm3Type) &&
                         itm3Type.GetString() == "message"))
                    {
                        toolResultReturnedUpstream = true;
                        subsequentFinalMessage = true;
                        continue;
                    }
                }
            }
            catch
            {
                // Non-fatal parse error on non-standard line
            }
        }

        // Robust fallback checks if lines were concatenated or slightly non-standard
        if (!toolCallEmitted && (stdout.Contains("\"function_call\"") || stdout.Contains("\"tool_call\"")))
        {
            toolCallEmitted = true;
        }
        if (toolCallEmitted && !toolExecuted && (stdout.Contains("\"function_call_output\"") || stdout.Contains("\"tool_output\"") || stdout.Contains("\"tool_result\"")))
        {
            toolExecuted = true;
        }
        if (toolExecuted && !toolResultReturnedUpstream && (stdout.Contains("item.completed") || subsequentFinalMessage))
        {
            toolResultReturnedUpstream = true;
        }

        if (!canaryContentMatches)
        {
            return (false, ProviderProbeOutcome.CodexEnvelopeRejected,
                "Canary file 'canary.txt' was not created with expected content 'canary-ok'");
        }

        if (!toolCallEmitted)
        {
            return (false, ProviderProbeOutcome.CodexEnvelopeRejected,
                "Tool smoke failed: model did not emit a tool call");
        }

        if (!toolExecuted)
        {
            return (false, ProviderProbeOutcome.CodexEnvelopeRejected,
                "Tool smoke failed: tool execution event not observed");
        }

        if (!toolResultReturnedUpstream)
        {
            return (false, ProviderProbeOutcome.CodexEnvelopeRejected,
                "Tool smoke failed: tool result was not returned upstream");
        }

        if (!subsequentFinalMessage)
        {
            return (false, ProviderProbeOutcome.CodexEnvelopeRejected,
                "Tool smoke failed: model did not produce a subsequent final message after tool execution");
        }

        if (exitCode != 0)
        {
            return (false, ProviderProbeOutcome.CodexEnvelopeRejected,
                $"Tool smoke turn did not complete successfully (exit code {exitCode})");
        }

        return (true, ProviderProbeOutcome.Success,
            "Built-in tool operation verified with canary file in workspace-write sandbox (network_access = false enforced)");
    }


    private static async Task<(int ExitCode, string Stdout, string Stderr)> DefaultProcessRunnerAsync(ProcessStartInfo psi, CancellationToken ct)
    {
        using var process = new Process { StartInfo = psi };
        if (!process.Start())
        {
            return (-1, string.Empty, "Failed to start process");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        return (process.ExitCode, stdout, stderr);
    }
}

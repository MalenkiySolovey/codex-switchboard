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

        // 2. Probe /responses (minimal text ping) with fine-grained failure classification
        var (responsesEvidence, responsesClassification) = await ProbeMinimalResponsesAsync(cleanBaseUrl, apiKey, modelSlug, cts.Token).ConfigureAwait(false);

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
        if (!httpToolCallingEvidence.IsSupported && responsesClassification == ResponsesFailureClassification.ResponsesPassed)
        {
            if (httpToolCallingEvidence.Detail != null &&
                (httpToolCallingEvidence.Detail.Contains("namespace", StringComparison.OrdinalIgnoreCase) ||
                 httpToolCallingEvidence.Detail.Contains("unsupported tool", StringComparison.OrdinalIgnoreCase) ||
                 httpToolCallingEvidence.Detail.Contains("schema", StringComparison.OrdinalIgnoreCase)))
            {
                responsesClassification = ResponsesFailureClassification.CodexEnvelopeRejected;
            }
        }

        // 7. Probe hosted search (strictly opt-in to avoid unexpected billing)
        var hostedSearchEvidence = options.IncludeHostedSearch
            ? await ProbeHostedSearchAsync(cleanBaseUrl, apiKey, modelSlug, cts.Token).ConfigureAwait(false)
            : CapabilityEvidence.Unknown("Hosted search probe not requested (opt-in to avoid extra billing)");

        // 8. Layer 2 — Real isolated Codex runtime execution (Phase A: basic turn, Phase B: real tool smoke)
        var (basicTurnEvidence, execToolEvidence, functionToolsEvidence, mcpToolsEvidence, envelopeRejection) =
            options.RunCodexSmokeTest
                ? await ProbeCodexRealExecutionAsync(runtimeInfo, cleanBaseUrl, apiKey, modelSlug, options.RunToolSmokeTest, cts.Token).ConfigureAwait(false)
                : (CapabilityEvidence.Unknown("Codex smoke test skipped by options"),
                   CapabilityEvidence.Unknown("Tool smoke skipped by options"),
                   httpToolCallingEvidence,
                   CapabilityEvidence.Unknown("MCP namespace tools unverified (advanced probe)"),
                   null);

        if (envelopeRejection.HasValue)
        {
            responsesClassification = envelopeRejection.Value;
        }

        // 9. Synthesize overall compatibility level
        var (level, summary) = SynthesizeCompatibility(
            responsesClassification,
            responsesEvidence,
            basicTurnEvidence,
            execToolEvidence);

        var appsToolsEvidence = CapabilityEvidence.Unknown("Apps namespace tools unverified (advanced probe)");
        var pluginsEvidence = CapabilityEvidence.Unknown("Plugins unverified (advanced probe)");
        var multiAgentEvidence = CapabilityEvidence.Unknown("Multi-agent unverified (advanced probe)");

        return new ProviderProbeReport(
            cleanBaseUrl,
            modelSlug,
            level,
            responsesClassification,
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
            summary);
    }

    private static (CodexCompatibilityLevel Level, string Summary) SynthesizeCompatibility(
        ResponsesFailureClassification responsesStatus,
        CapabilityEvidence responses,
        CapabilityEvidence basicTurn,
        CapabilityEvidence execTool)
    {
        switch (responsesStatus)
        {
            case ResponsesFailureClassification.EndpointMissing:
                return (CodexCompatibilityLevel.NotCompatible,
                    "Not compatible with Codex: /responses endpoint not found (HTTP 404). Provider does not support Codex Responses API.");

            case ResponsesFailureClassification.AuthenticationFailed:
                return (CodexCompatibilityLevel.NotCompatible,
                    "Authentication failed (HTTP 401/403): Provider rejected the API key.");

            case ResponsesFailureClassification.ModelUnavailable:
                return (CodexCompatibilityLevel.PartiallyCompatible,
                    "Partially compatible: /responses endpoint is reachable, but the requested model is not available or rejected.");

            case ResponsesFailureClassification.PayloadRejected:
                return (CodexCompatibilityLevel.PartiallyCompatible,
                    "Partially compatible: /responses endpoint reachable, but request payload parameters were rejected.");

            case ResponsesFailureClassification.RateLimited:
                return (CodexCompatibilityLevel.PartiallyCompatible,
                    "Partially compatible: Rate limit exceeded (HTTP 429) during qualification.");

            case ResponsesFailureClassification.UpstreamUnavailable:
                return (CodexCompatibilityLevel.PartiallyCompatible,
                    "Partially compatible: Upstream provider server error or connection failure.");

            case ResponsesFailureClassification.CodexEnvelopeRejected:
                return (CodexCompatibilityLevel.PartiallyCompatible,
                    "Partially compatible: Provider rejected custom Codex tool calling or namespace envelope.");

            case ResponsesFailureClassification.ResponsesPassed:
            default:
                if (!responses.IsSupported)
                {
                    return (CodexCompatibilityLevel.NotCompatible,
                        "Not compatible with Codex: /responses endpoint is not supported or rejected the request.");
                }

                if (basicTurn.IsSupported)
                {
                    if (execTool.IsSupported)
                    {
                        return (CodexCompatibilityLevel.CodexCompatible,
                            "Codex compatible: Direct Responses API verified with real Codex execution and built-in tools.");
                    }

                    if (execTool.State == CapabilityEvidenceState.ProbeFailed)
                    {
                        return (CodexCompatibilityLevel.PartiallyCompatible,
                            "Partially compatible: Basic Codex turn succeeded, but built-in tool execution failed or was rejected by provider.");
                    }

                    // Exec tool was skipped or unknown
                    return (CodexCompatibilityLevel.CodexCompatible,
                        "Codex compatible: Direct Responses API verified via live execution.");
                }

                if (basicTurn.State == CapabilityEvidenceState.ProbeFailed)
                {
                    return (CodexCompatibilityLevel.PartiallyCompatible,
                        "Partially compatible: Basic Responses inference passed; Codex runtime execution encountered errors.");
                }

                // Smoke test was skipped/unknown, but responses protocol works
                return (CodexCompatibilityLevel.CodexCompatible,
                    "Codex compatible: Direct Responses API verified via HTTP protocol probe.");
        }
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

    private async Task<(CapabilityEvidence Evidence, ResponsesFailureClassification Classification)>
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
                return (CapabilityEvidence.ProbePassed("POST /responses", detail: $"HTTP {statusCode} OK"), ResponsesFailureClassification.ResponsesPassed);
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

            var classification = ClassifyHttpFailure(statusCode, responseBody);
            var detail = $"HTTP {statusCode} {resp.ReasonPhrase}".Trim();
            if (!string.IsNullOrWhiteSpace(responseBody) && responseBody.Length < 250)
            {
                detail += $": {responseBody.Trim()}";
            }

            return (CapabilityEvidence.ProbeFailed("POST /responses", detail: detail), classification);
        }
        catch (HttpRequestException ex)
        {
            var classification = ex.StatusCode.HasValue
                ? ClassifyHttpFailure((int)ex.StatusCode.Value, ex.Message)
                : ResponsesFailureClassification.UpstreamUnavailable;

            return (CapabilityEvidence.ProbeFailed("POST /responses", detail: ex.Message), classification);
        }
        catch (Exception ex)
        {
            return (CapabilityEvidence.ProbeFailed("POST /responses", detail: ex.Message), ResponsesFailureClassification.UpstreamUnavailable);
        }
    }

    private static ResponsesFailureClassification ClassifyHttpFailure(int statusCode, string body)
    {
        if (statusCode == 404)
        {
            return ResponsesFailureClassification.EndpointMissing;
        }

        if (statusCode is 401 or 403)
        {
            return ResponsesFailureClassification.AuthenticationFailed;
        }

        if (statusCode == 429)
        {
            return ResponsesFailureClassification.RateLimited;
        }

        if (statusCode >= 500)
        {
            return ResponsesFailureClassification.UpstreamUnavailable;
        }

        if (statusCode == 400)
        {
            if (body.Contains("namespace", StringComparison.OrdinalIgnoreCase) ||
                body.Contains("unsupported tool", StringComparison.OrdinalIgnoreCase) ||
                body.Contains("tool", StringComparison.OrdinalIgnoreCase) && body.Contains("schema", StringComparison.OrdinalIgnoreCase))
            {
                return ResponsesFailureClassification.CodexEnvelopeRejected;
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
                return ResponsesFailureClassification.ModelUnavailable;
            }

            return ResponsesFailureClassification.PayloadRejected;
        }

        return ResponsesFailureClassification.PayloadRejected;
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

    private async Task<(
        CapabilityEvidence BasicTurn,
        CapabilityEvidence ExecTool,
        CapabilityEvidence FunctionTools,
        CapabilityEvidence McpTools,
        ResponsesFailureClassification? EnvelopeRejection)>
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
            var toml = new StringBuilder();
            toml.Append("model = \"").Append(modelSlug).AppendLine("\"");
            toml.AppendLine("model_provider = \"probe_provider\"");
            toml.AppendLine();
            toml.AppendLine("[model_providers.probe_provider]");
            toml.AppendLine("name = \"probe_provider\"");
            toml.Append("base_url = \"").Append(baseUrl).AppendLine("\"");
            toml.AppendLine("wire_api = \"responses\"");
            toml.AppendLine("requires_openai_auth = false");
            toml.AppendLine("env_key = \"CODEX_SMOKE_KEY\"");

            var configPath = Path.Combine(tempDir, "config.toml");
            _fs.WriteAllTextAtomic(configPath, toml.ToString());

            // Phase A: Basic Codex Turn (plain ping)
            var basicPsi = new ProcessStartInfo
            {
                FileName = runtimeInfo.ExecutablePath,
                Arguments = "exec --ephemeral --skip-git-repo-check --json --color never \"ping\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            basicPsi.Environment["CODEX_HOME"] = tempDir;
            basicPsi.Environment["CODEX_SMOKE_KEY"] = apiKey;

            var (basicExit, basicStdout, basicStderr) = await _processRunner(basicPsi, ct).ConfigureAwait(false);

            CapabilityEvidence basicTurnEvidence;
            ResponsesFailureClassification? envelopeRejection = null;

            if (basicExit == 0)
            {
                basicTurnEvidence = CapabilityEvidence.ProbePassed("Codex CLI execution", runtimeIdentity, "Real exec turn completed successfully (exit 0)");
            }
            else
            {
                var combined = (basicStdout + "\n" + basicStderr).Trim();
                if (combined.Contains("namespace", StringComparison.OrdinalIgnoreCase) ||
                    combined.Contains("unsupported tool", StringComparison.OrdinalIgnoreCase) ||
                    combined.Contains("schema", StringComparison.OrdinalIgnoreCase))
                {
                    basicTurnEvidence = CapabilityEvidence.ProbeFailed("Codex CLI execution", runtimeIdentity, "Provider rejected Codex tool/namespace envelope");
                    envelopeRejection = ResponsesFailureClassification.CodexEnvelopeRejected;
                }
                else
                {
                    var failDetail = !string.IsNullOrWhiteSpace(basicStderr) ? basicStderr.Trim() : $"Exit code {basicExit}";
                    basicTurnEvidence = CapabilityEvidence.ProbeFailed("Codex CLI execution", runtimeIdentity, failDetail);
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
                    Arguments = $"exec --ephemeral --skip-git-repo-check --json --color never --dangerously-bypass-approvals-and-sandbox -C \"{workspaceDir}\" \"Create a file named canary.txt with text 'canary-ok'\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                toolPsi.Environment["CODEX_HOME"] = tempDir;
                toolPsi.Environment["CODEX_SMOKE_KEY"] = apiKey;

                var (toolExit, toolStdout, toolStderr) = await _processRunner(toolPsi, ct).ConfigureAwait(false);

                bool canaryVerified = _fs.FileExists(canaryPath) &&
                    (_fs.ReadAllText(canaryPath).Contains("canary-ok", StringComparison.OrdinalIgnoreCase));

                bool eventsVerified = toolStdout.Contains("canary", StringComparison.OrdinalIgnoreCase) ||
                                      toolStdout.Contains("tool_call", StringComparison.OrdinalIgnoreCase) ||
                                      toolStdout.Contains("function_call", StringComparison.OrdinalIgnoreCase);

                if (toolExit == 0 && (canaryVerified || eventsVerified))
                {
                    execToolEvidence = CapabilityEvidence.ProbePassed("Codex CLI tool execution", runtimeIdentity, "Built-in tool operation (canary file) completed successfully");
                    functionToolsEvidence = CapabilityEvidence.ProbePassed("Codex CLI tool execution", runtimeIdentity, "Built-in function calling accepted and executed");
                }
                else
                {
                    var toolCombined = (toolStdout + "\n" + toolStderr).Trim();
                    if (toolCombined.Contains("namespace", StringComparison.OrdinalIgnoreCase) ||
                        toolCombined.Contains("unsupported tool", StringComparison.OrdinalIgnoreCase) ||
                        toolCombined.Contains("tool", StringComparison.OrdinalIgnoreCase) && toolCombined.Contains("schema", StringComparison.OrdinalIgnoreCase))
                    {
                        envelopeRejection = ResponsesFailureClassification.CodexEnvelopeRejected;
                        execToolEvidence = CapabilityEvidence.ProbeFailed("Codex CLI tool execution", runtimeIdentity, "Provider rejected Codex tool/namespace envelope");
                    }
                    else
                    {
                        var failDetail = !string.IsNullOrWhiteSpace(toolStderr) ? toolStderr.Trim() : $"Tool smoke failed (exit code {toolExit})";
                        execToolEvidence = CapabilityEvidence.ProbeFailed("Codex CLI tool execution", runtimeIdentity, failDetail);
                    }
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

            return (basicTurnEvidence, execToolEvidence, functionToolsEvidence, mcpToolsEvidence, envelopeRejection);
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

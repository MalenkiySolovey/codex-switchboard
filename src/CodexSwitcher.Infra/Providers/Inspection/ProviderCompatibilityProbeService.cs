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
using CodexSwitcher.Core.Providers.Contracts;
using CodexSwitcher.Core.Providers.Models;
using CodexSwitcher.Core.Routing.Contracts;
using CodexSwitcher.Core.Routing.Models;

namespace CodexSwitcher.Infra.Providers.Inspection;

/// <summary>
/// Executes cheap diagnostic compatibility probes against API providers:
/// - /models endpoint
/// - minimal /responses endpoint
/// - reasoning parameter compatibility
/// - optional vision payload
/// - ordinary tool calling
/// - opt-in hosted search probe (disabled by default to prevent unexpected billing)
/// - production Codex runtime smoke test
/// </summary>
public sealed class ProviderCompatibilityProbeService : IProviderCompatibilityProbeService
{
    private readonly HttpClient _httpClient;
    private readonly ICodexRuntimeResolver? _runtimeResolver;

    public ProviderCompatibilityProbeService(
        HttpClient? httpClient = null,
        ICodexRuntimeResolver? runtimeResolver = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _runtimeResolver = runtimeResolver;
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
        var timeout = options.Timeout > TimeSpan.Zero ? options.Timeout : TimeSpan.FromSeconds(10);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        var runtimeInfo = _runtimeResolver?.ResolveCurrentRuntime();
        var runtimeIdentity = runtimeInfo != null
            ? $"{runtimeInfo.ExecutablePath}|{runtimeInfo.Version}"
            : "unknown";

        var cleanBaseUrl = baseUrl.TrimEnd('/');

        // 1. Probe /models
        var modelsEvidence = await ProbeModelsAsync(cleanBaseUrl, apiKey, cts.Token).ConfigureAwait(false);

        // 2. Probe /responses (minimal ping)
        var responsesEvidence = await ProbeMinimalResponsesAsync(cleanBaseUrl, apiKey, modelSlug, cts.Token).ConfigureAwait(false);

        // 3. Probe reasoning
        var reasoningEvidence = await ProbeReasoningAsync(cleanBaseUrl, apiKey, modelSlug, cts.Token).ConfigureAwait(false);

        // 4. Probe vision (if enabled)
        var visionEvidence = options.IncludeVision
            ? await ProbeVisionAsync(cleanBaseUrl, apiKey, modelSlug, cts.Token).ConfigureAwait(false)
            : CapabilityEvidence.Unknown("Vision check skipped by probe options");

        // 5. Probe ordinary tool calling
        var toolCallingEvidence = await ProbeToolCallingAsync(cleanBaseUrl, apiKey, modelSlug, cts.Token).ConfigureAwait(false);

        // 6. Probe hosted search (strictly opt-in to avoid unexpected billing)
        var hostedSearchEvidence = options.IncludeHostedSearch
            ? await ProbeHostedSearchAsync(cleanBaseUrl, apiKey, modelSlug, cts.Token).ConfigureAwait(false)
            : CapabilityEvidence.Unknown("Hosted search probe not requested (opt-in to avoid extra billing)");

        // 7. Codex runtime smoke test
        var smokeTestEvidence = ProbeCodexSmokeTest(runtimeInfo);

        return new ProviderProbeReport(
            cleanBaseUrl,
            modelSlug,
            modelsEvidence,
            responsesEvidence,
            reasoningEvidence,
            visionEvidence,
            toolCallingEvidence,
            hostedSearchEvidence,
            smokeTestEvidence,
            DateTimeOffset.UtcNow,
            runtimeIdentity);
    }

    private async Task<CapabilityEvidence> ProbeModelsAsync(string baseUrl, string apiKey, CancellationToken ct)
    {
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
                return CapabilityEvidence.ProbePassed("GET /models", detail: $"HTTP {(int)resp.StatusCode} OK");
            }

            return CapabilityEvidence.ProbeFailed("GET /models", detail: $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
        }
        catch (Exception ex)
        {
            return CapabilityEvidence.ProbeFailed("GET /models", detail: ex.Message);
        }
    }

    private async Task<CapabilityEvidence> ProbeMinimalResponsesAsync(string baseUrl, string apiKey, string model, CancellationToken ct)
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
            if (resp.IsSuccessStatusCode)
            {
                return CapabilityEvidence.ProbePassed("POST /responses", detail: $"HTTP {(int)resp.StatusCode} OK");
            }

            return CapabilityEvidence.ProbeFailed("POST /responses", detail: $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
        }
        catch (Exception ex)
        {
            return CapabilityEvidence.ProbeFailed("POST /responses", detail: ex.Message);
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
            // Minimal 1x1 transparent GIF base64
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

    private static CapabilityEvidence ProbeCodexSmokeTest(CodexRuntimeInfo? runtimeInfo)
    {
        if (runtimeInfo == null || string.IsNullOrWhiteSpace(runtimeInfo.ExecutablePath) || !File.Exists(runtimeInfo.ExecutablePath))
        {
            return CapabilityEvidence.Unknown("Codex runtime executable not available on this system");
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = runtimeInfo.ExecutablePath,
                Arguments = "--version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process != null && process.WaitForExit(3000) && process.ExitCode == 0)
            {
                var stdout = process.StandardOutput.ReadToEnd().Trim();
                return CapabilityEvidence.ProbePassed("Codex CLI smoke test", runtimeInfo.ExecutablePath, stdout);
            }

            return CapabilityEvidence.ProbeFailed("Codex CLI smoke test", runtimeInfo.ExecutablePath, "Non-zero exit code or timeout");
        }
        catch (Exception ex)
        {
            return CapabilityEvidence.ProbeFailed("Codex CLI smoke test", runtimeInfo.ExecutablePath, ex.Message);
        }
    }
}

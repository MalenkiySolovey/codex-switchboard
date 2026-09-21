using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CodexSwitcher.Core.Providers.Contracts;
using CodexSwitcher.Core.Providers.Models;
using CodexSwitcher.Infra.Providers.Inspection;
using Xunit;

namespace CodexSwitcher.Core.Tests;

public sealed class ProviderCompatibilityProbeTests
{
    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> OnSend { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(OnSend(request));
        }
    }

    [Fact]
    public async Task ProbeCompatibility_DefaultOptions_SkipsHostedSearchProbe()
    {
        var handler = new MockHttpMessageHandler
        {
            OnSend = req =>
            {
                if (req.RequestUri!.AbsolutePath.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"data\":[{\"id\":\"grok-4.6\"}]}")
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"pong\"}}]}")
                };
            }
        };

        using var client = new HttpClient(handler);
        var probeService = new ProviderCompatibilityProbeService(client);

        var report = await probeService.ProbeCompatibilityAsync(
            "https://api.test/v1",
            "test-key",
            "grok-4.6");

        Assert.True(report.ModelsEndpoint.IsSupported);
        Assert.True(report.ResponsesEndpoint.IsSupported);
        Assert.True(report.ReasoningSupport.IsSupported);
        Assert.True(report.VisionSupport.IsSupported);
        Assert.True(report.ToolCallingSupport.IsSupported);

        // Hosted search MUST be Unknown when not requested (opt-in requirement 9)
        Assert.Equal(CapabilityEvidenceState.Unknown, report.HostedSearchSupport.State);
        Assert.Contains("opt-in", report.HostedSearchSupport.Source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProbeCompatibility_OptInHostedSearch_ModelflareFailure_ReportsProbeFailed()
    {
        var handler = new MockHttpMessageHandler
        {
            OnSend = req =>
            {
                // Simulate Modelflare grok-4.6 web_search failure
                if (req.Content != null)
                {
                    var body = req.Content.ReadAsStringAsync().Result;
                    if (body.Contains("web_search"))
                    {
                        return new HttpResponseMessage(HttpStatusCode.BadRequest)
                        {
                            ReasonPhrase = "Bad Request (Tool web_search not supported)"
                        };
                    }
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"status\":\"ok\"}")
                };
            }
        };

        using var client = new HttpClient(handler);
        var probeService = new ProviderCompatibilityProbeService(client);

        var options = new ProviderProbeOptions
        {
            IncludeHostedSearch = true // Opt-in explicitly
        };

        var report = await probeService.ProbeCompatibilityAsync(
            "https://api.modelflare.test/v1",
            "test-key",
            "grok-4.6",
            options);

        Assert.True(report.ResponsesEndpoint.IsSupported);
        Assert.Equal(CapabilityEvidenceState.ProbeFailed, report.HostedSearchSupport.State);
        Assert.False(report.HostedSearchSupport.IsSupported);
    }

    [Fact]
    public async Task ProbeCompatibility_WhenModelsFails_ReportsProbeFailed()
    {
        var handler = new MockHttpMessageHandler
        {
            OnSend = req =>
            {
                if (req.RequestUri!.AbsolutePath.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
                {
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized)
                    {
                        ReasonPhrase = "Invalid API Key"
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
        };

        using var client = new HttpClient(handler);
        var probeService = new ProviderCompatibilityProbeService(client);

        var report = await probeService.ProbeCompatibilityAsync(
            "https://api.test/v1",
            "bad-key",
            "gpt-5.6-sol");

        Assert.Equal(CapabilityEvidenceState.ProbeFailed, report.ModelsEndpoint.State);
        Assert.False(report.AllStandardChecksPassed);
    }

    [Fact]
    public async Task ProbeCompatibility_WhenResponsesFails_ReportsNotCompatible()
    {
        var handler = new MockHttpMessageHandler
        {
            OnSend = req =>
            {
                if (req.RequestUri!.AbsolutePath.EndsWith("/responses", StringComparison.OrdinalIgnoreCase))
                {
                    // Incompatible API: Chat-Completions only or Anthropic Messages only
                    return new HttpResponseMessage(HttpStatusCode.NotFound)
                    {
                        ReasonPhrase = "Not Found - /responses unsupported"
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"data\":[{\"id\":\"test-model\"}]}")
                };
            }
        };

        using var client = new HttpClient(handler);
        var probeService = new ProviderCompatibilityProbeService(client);

        var report = await probeService.ProbeCompatibilityAsync(
            "https://api.test/v1",
            "test-key",
            "test-model");

        Assert.Equal(CapabilityEvidenceState.ProbeFailed, report.ResponsesEndpoint.State);
        Assert.Equal(CodexCompatibilityLevel.NotCompatible, report.CompatibilityLevel);
        Assert.Contains("Not compatible", report.DiagnosticSummary, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class MockRuntimeResolver : ICodexRuntimeResolver
    {
        private readonly CodexRuntimeInfo _info;

        public MockRuntimeResolver(string exePath)
        {
            _info = new CodexRuntimeInfo(
                exePath,
                "0.155.0",
                "test-identity",
                CodexRuntimeCapabilities.Default);
        }

        public CodexRuntimeInfo ResolveCurrentRuntime(string? overridePath = null) => _info;
        public IReadOnlyList<CodexRuntimeCandidate> EnumerateCandidates() => Array.Empty<CodexRuntimeCandidate>();
        public bool ValidateExecutable(string path, out string? version, out string? errorMessage)
        {
            version = "0.155.0";
            errorMessage = null;
            return true;
        }
    }

    [Fact]
    public async Task ProbeCompatibility_WhenResponsesPassesAndSmokeTestFails_ReportsPartiallyCompatible()
    {
        var handler = new MockHttpMessageHandler
        {
            OnSend = req =>
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"pong\"}}]}")
                };
            }
        };

        using var client = new HttpClient(handler);

        // Custom process runner simulating smoke test failure (exit code 1)
        ProcessRunnerFunc mockRunner = (_, _) =>
            Task.FromResult((1, "", "Error: Tool dispatch unsupported by runtime proxy"));

        var mockResolver = new MockRuntimeResolver(Environment.ProcessPath ?? typeof(ProviderCompatibilityProbeTests).Assembly.Location);

        var probeService = new ProviderCompatibilityProbeService(
            httpClient: client,
            runtimeResolver: mockResolver,
            fs: null,
            processRunner: mockRunner);

        var options = new ProviderProbeOptions
        {
            RunCodexSmokeTest = true
        };

        var report = await probeService.ProbeCompatibilityAsync(
            "https://api.test/v1",
            "test-key",
            "test-model",
            options);


        Assert.Equal(CapabilityEvidenceState.ProbePassed, report.ResponsesEndpoint.State);
        Assert.Equal(CapabilityEvidenceState.ProbeFailed, report.CodexRuntimeSmokeTest.State);
        Assert.Equal(CodexCompatibilityLevel.PartiallyCompatible, report.CompatibilityLevel);
        Assert.Contains("Partially compatible", report.DiagnosticSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerateSanitizedExport_ExcludesRawSecrets_AndProducesValidJson()
    {
        const string rawSecretKey = "sk-super-secret-key-123456789";

        var handler = new MockHttpMessageHandler
        {
            OnSend = req => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"data\":[{\"id\":\"m1\"}]}")
            }
        };

        using var client = new HttpClient(handler);
        var probeService = new ProviderCompatibilityProbeService(client);

        var report = await probeService.ProbeCompatibilityAsync(
            "https://api.test/v1",
            rawSecretKey,
            "test-model");

        var jsonExport = report.GenerateSanitizedExport("Test Provider Display", "pool-east-1");

        // Raw key must NEVER appear in sanitized export
        Assert.DoesNotContain(rawSecretKey, jsonExport);

        // Must be valid JSON
        using var doc = JsonDocument.Parse(jsonExport);
        var root = doc.RootElement;

        Assert.Equal("Test Provider Display", root.GetProperty("provider").GetString());
        Assert.Equal("pool-east-1", root.GetProperty("routePoolLabel").GetString());
        Assert.Equal("test-model", root.GetProperty("model").GetString());
        Assert.Equal("https://api.test/v1", root.GetProperty("baseUrl").GetString());
        Assert.True(root.TryGetProperty("capabilities", out var caps));
        Assert.True(caps.TryGetProperty("responsesEndpoint", out _));
        Assert.True(caps.TryGetProperty("hostedSearch", out _));
        Assert.NotNull(root.GetProperty("diagnosticSummary").GetString());
    }
}

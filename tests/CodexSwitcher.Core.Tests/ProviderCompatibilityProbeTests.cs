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
    public async Task ProbeCompatibility_ResponsesPass_PlainTurnPass_ToolSmokePass_ReportsCodexCompatible()
    {
        var handler = new MockHttpMessageHandler
        {
            OnSend = req => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"pong\"}}]}")
            }
        };

        using var client = new HttpClient(handler);

        ProcessRunnerFunc mockRunner = (psi, _) =>
        {
            if (psi.Arguments.Contains("canary.txt", StringComparison.OrdinalIgnoreCase))
            {
                // Write canary file to the workspace
                var match = System.Text.RegularExpressions.Regex.Match(psi.Arguments, @"-C\s+""([^""]+)""");
                if (match.Success)
                {
                    var canaryFile = Path.Combine(match.Groups[1].Value, "canary.txt");
                    File.WriteAllText(canaryFile, "canary-ok");
                }
                // Simulate complete event sequence:
                // 1. Tool call emitted
                // 2. Tool executed
                // 3. Tool result returned upstream
                // 4. Model produces subsequent final message
                // 5. Turn completed (exit 0)
                var jsonEvents = string.Join("\n",
                    "{\"type\":\"item.created\",\"item\":{\"type\":\"function_call\",\"name\":\"write_file\",\"call_id\":\"call_1\"}}",
                    "{\"type\":\"item.created\",\"item\":{\"type\":\"function_call_output\",\"call_id\":\"call_1\",\"output\":\"canary-ok\"}}",
                    "{\"type\":\"item.completed\",\"item\":{\"type\":\"function_call_output\",\"call_id\":\"call_1\"}}",
                    "{\"type\":\"item.created\",\"item\":{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"Canary file created successfully.\"}]}}",
                    "{\"type\":\"turn.completed\"}");
                return Task.FromResult((0, jsonEvents, ""));
            }
            // Basic ping turn
            return Task.FromResult((0, "{\"type\":\"message\",\"content\":\"pong\"}", ""));
        };

        var mockResolver = new MockRuntimeResolver(Environment.ProcessPath ?? typeof(ProviderCompatibilityProbeTests).Assembly.Location);

        var probeService = new ProviderCompatibilityProbeService(
            httpClient: client,
            runtimeResolver: mockResolver,
            fs: null,
            processRunner: mockRunner);

        var options = new ProviderProbeOptions
        {
            RunCodexSmokeTest = true,
            RunToolSmokeTest = true
        };

        var report = await probeService.ProbeCompatibilityAsync(
            "https://api.test/v1",
            "test-key",
            "test-model",
            options);

        Assert.Equal(ProviderProbeOutcome.Success, report.ProbeOutcome);
        Assert.Equal(ResponsesFailureClassification.ResponsesPassed, report.ResponsesStatus);
        Assert.Equal(CodexCompatibilityLevel.CodexCompatible, report.CompatibilityLevel);
        Assert.True(report.BasicCodexTurn.IsSupported);
        Assert.True(report.ExecTool.IsSupported);
        Assert.True(report.BuiltInFunctionTools.IsSupported);
        Assert.Contains("workspace-write sandbox", report.ExecTool.Detail);
        Assert.Contains("network_access = false", report.ExecTool.Detail);
        // MCP namespace tools must remain Unknown by default
        Assert.Equal(CapabilityEvidenceState.Unknown, report.McpNamespaceTools.State);
    }

    [Fact]
    public async Task ProbeCompatibility_ResponsesPass_PlainTurnPass_ToolSmokeFail_ReportsPartiallyCompatible()
    {
        var handler = new MockHttpMessageHandler
        {
            OnSend = req => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"pong\"}}]}")
            }
        };

        using var client = new HttpClient(handler);

        ProcessRunnerFunc mockRunner = (psi, _) =>
        {
            if (psi.Arguments.Contains("canary.txt", StringComparison.OrdinalIgnoreCase))
            {
                // Tool turn fails (provider rejected tool calling)
                return Task.FromResult((1, "", "Error: Provider rejected tool call execution"));
            }
            // Basic ping turn passes
            return Task.FromResult((0, "{\"type\":\"message\",\"content\":\"pong\"}", ""));
        };

        var mockResolver = new MockRuntimeResolver(Environment.ProcessPath ?? typeof(ProviderCompatibilityProbeTests).Assembly.Location);

        var probeService = new ProviderCompatibilityProbeService(
            httpClient: client,
            runtimeResolver: mockResolver,
            fs: null,
            processRunner: mockRunner);

        var options = new ProviderProbeOptions
        {
            RunCodexSmokeTest = true,
            RunToolSmokeTest = true
        };

        var report = await probeService.ProbeCompatibilityAsync(
            "https://api.test/v1",
            "test-key",
            "test-model",
            options);

        // Minimal responses PASS + plain turn PASS + tool smoke FAIL must be PartiallyCompatible, not NotCompatible!
        Assert.Equal(CodexCompatibilityLevel.PartiallyCompatible, report.CompatibilityLevel);
        Assert.True(report.BasicCodexTurn.IsSupported);
        Assert.False(report.ExecTool.IsSupported);
        Assert.Equal(CapabilityEvidenceState.ProbeFailed, report.ExecTool.State);
        Assert.Contains("Partially compatible", report.DiagnosticSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProbeCompatibility_Responses404_ReportsEndpointMissing_AndNotCompatible()
    {
        var handler = new MockHttpMessageHandler
        {
            OnSend = req =>
            {
                if (req.RequestUri!.AbsolutePath.EndsWith("/responses", StringComparison.OrdinalIgnoreCase))
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound)
                    {
                        ReasonPhrase = "Not Found",
                        Content = new StringContent("Cannot POST /responses")
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
        };

        using var client = new HttpClient(handler);
        var probeService = new ProviderCompatibilityProbeService(client);

        var report = await probeService.ProbeCompatibilityAsync(
            "https://api.test/v1",
            "test-key",
            "test-model");

        Assert.Equal(ResponsesFailureClassification.EndpointMissing, report.ResponsesStatus);
        Assert.Equal(CodexCompatibilityLevel.NotCompatible, report.CompatibilityLevel);
        Assert.Contains("404", report.DiagnosticSummary);
    }

    [Fact]
    public async Task ProbeCompatibility_Responses401_ReportsAuthenticationFailed_AndUnknown()
    {
        var handler = new MockHttpMessageHandler
        {
            OnSend = req =>
            {
                if (req.RequestUri!.AbsolutePath.EndsWith("/responses", StringComparison.OrdinalIgnoreCase))
                {
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized)
                    {
                        ReasonPhrase = "Unauthorized",
                        Content = new StringContent("{\"error\":\"Invalid bearer token\"}")
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
            "test-model");

        Assert.Equal(ProviderProbeOutcome.AuthenticationFailed, report.ProbeOutcome);
        Assert.Equal(ResponsesFailureClassification.AuthenticationFailed, report.ResponsesStatus);
        Assert.Equal(CodexCompatibilityLevel.Unknown, report.CompatibilityLevel);
        Assert.Contains("Authentication failed", report.DiagnosticSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unverified", report.DiagnosticSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProbeCompatibility_Responses429_ReportsRateLimited_AndUnknown()
    {
        var handler = new MockHttpMessageHandler
        {
            OnSend = req =>
            {
                if (req.RequestUri!.AbsolutePath.EndsWith("/responses", StringComparison.OrdinalIgnoreCase))
                {
                    return new HttpResponseMessage((HttpStatusCode)429)
                    {
                        ReasonPhrase = "Too Many Requests",
                        Content = new StringContent("{\"error\":{\"message\":\"Rate limit exceeded\"}}")
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
        };

        using var client = new HttpClient(handler);
        var probeService = new ProviderCompatibilityProbeService(client);

        var report = await probeService.ProbeCompatibilityAsync(
            "https://api.test/v1",
            "test-key",
            "test-model");

        Assert.Equal(ProviderProbeOutcome.RateLimited, report.ProbeOutcome);
        Assert.Equal(CodexCompatibilityLevel.Unknown, report.CompatibilityLevel);
        Assert.Contains("Rate limit", report.DiagnosticSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unverified", report.DiagnosticSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProbeCompatibility_Responses5xx_ReportsUpstreamUnavailable_AndUnknown()
    {
        var handler = new MockHttpMessageHandler
        {
            OnSend = req =>
            {
                if (req.RequestUri!.AbsolutePath.EndsWith("/responses", StringComparison.OrdinalIgnoreCase))
                {
                    return new HttpResponseMessage(HttpStatusCode.BadGateway)
                    {
                        ReasonPhrase = "Bad Gateway",
                        Content = new StringContent("502 Bad Gateway upstream")
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
        };

        using var client = new HttpClient(handler);
        var probeService = new ProviderCompatibilityProbeService(client);

        var report = await probeService.ProbeCompatibilityAsync(
            "https://api.test/v1",
            "test-key",
            "test-model");

        Assert.Equal(ProviderProbeOutcome.UpstreamUnavailable, report.ProbeOutcome);
        Assert.Equal(CodexCompatibilityLevel.Unknown, report.CompatibilityLevel);
        Assert.Contains("Upstream", report.DiagnosticSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unverified", report.DiagnosticSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProbeCompatibility_Responses400_UnsupportedModel_ReportsModelUnavailable_AndPartiallyCompatible()
    {
        var handler = new MockHttpMessageHandler
        {
            OnSend = req =>
            {
                if (req.RequestUri!.AbsolutePath.EndsWith("/responses", StringComparison.OrdinalIgnoreCase))
                {
                    return new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        ReasonPhrase = "Bad Request",
                        Content = new StringContent("{\"error\":{\"message\":\"The model 'grok-unknown' does not exist.\"}}")
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
        };

        using var client = new HttpClient(handler);
        var probeService = new ProviderCompatibilityProbeService(client);

        var report = await probeService.ProbeCompatibilityAsync(
            "https://api.test/v1",
            "test-key",
            "grok-unknown");

        Assert.Equal(ResponsesFailureClassification.ModelUnavailable, report.ResponsesStatus);
        Assert.Equal(CodexCompatibilityLevel.PartiallyCompatible, report.CompatibilityLevel);
        Assert.Contains("model", report.DiagnosticSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProbeCompatibility_Responses400_UnsupportedToolField_ReportsPayloadRejected_AndPartiallyCompatible()
    {
        var handler = new MockHttpMessageHandler
        {
            OnSend = req =>
            {
                if (req.RequestUri!.AbsolutePath.EndsWith("/responses", StringComparison.OrdinalIgnoreCase))
                {
                    return new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        ReasonPhrase = "Bad Request",
                        Content = new StringContent("{\"error\":{\"message\":\"Unsupported parameter: stream_options is not supported.\"}}")
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
        };

        using var client = new HttpClient(handler);
        var probeService = new ProviderCompatibilityProbeService(client);

        var report = await probeService.ProbeCompatibilityAsync(
            "https://api.test/v1",
            "test-key",
            "test-model");

        Assert.Equal(ResponsesFailureClassification.PayloadRejected, report.ResponsesStatus);
        Assert.Equal(CodexCompatibilityLevel.PartiallyCompatible, report.CompatibilityLevel);
        Assert.Contains("payload", report.DiagnosticSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProbeCompatibility_Responses400_NamespaceStyleRejection_ReportsCodexEnvelopeRejected_AndPartiallyCompatible()
    {
        var handler = new MockHttpMessageHandler
        {
            OnSend = req =>
            {
                if (req.RequestUri!.AbsolutePath.EndsWith("/responses", StringComparison.OrdinalIgnoreCase))
                {
                    return new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        ReasonPhrase = "Bad Request",
                        Content = new StringContent("{\"error\":{\"message\":\"Provider rejected tool namespace: openai/code_interpreter\"}}")
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
        };

        using var client = new HttpClient(handler);
        var probeService = new ProviderCompatibilityProbeService(client);

        var report = await probeService.ProbeCompatibilityAsync(
            "https://api.test/v1",
            "test-key",
            "test-model");

        Assert.Equal(ResponsesFailureClassification.CodexEnvelopeRejected, report.ResponsesStatus);
        Assert.Equal(CodexCompatibilityLevel.PartiallyCompatible, report.CompatibilityLevel);
        Assert.Contains("envelope", report.DiagnosticSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProbeCompatibility_LocalSandboxBlocked_ReportsLocalSandboxBlocked_AndUnknown()
    {
        var handler = new MockHttpMessageHandler
        {
            OnSend = req => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"pong\"}}]}")
            }
        };

        using var client = new HttpClient(handler);

        ProcessRunnerFunc mockRunner = (psi, _) =>
        {
            if (psi.Arguments.Contains("canary.txt", StringComparison.OrdinalIgnoreCase))
            {
                // Local sandbox prevented workspace-write execution
                return Task.FromResult((1, "", "sandbox initialization error: operation not permitted in Windows sandbox"));
            }
            // Basic ping turn passed
            return Task.FromResult((0, "{\"type\":\"message\",\"content\":\"pong\"}", ""));
        };

        var mockResolver = new MockRuntimeResolver(Environment.ProcessPath ?? typeof(ProviderCompatibilityProbeTests).Assembly.Location);

        var probeService = new ProviderCompatibilityProbeService(
            httpClient: client,
            runtimeResolver: mockResolver,
            fs: null,
            processRunner: mockRunner);

        var options = new ProviderProbeOptions
        {
            RunCodexSmokeTest = true,
            RunToolSmokeTest = true
        };

        var report = await probeService.ProbeCompatibilityAsync(
            "https://api.test/v1",
            "test-key",
            "test-model",
            options);

        // Local sandbox blocked is a probe-environment failure, NOT provider incompatibility!
        Assert.Equal(ProviderProbeOutcome.LocalSandboxBlocked, report.ProbeOutcome);
        Assert.Equal(CodexCompatibilityLevel.Unknown, report.CompatibilityLevel);
        Assert.False(report.ExecTool.IsSupported);
        Assert.Contains("probe-environment failure", report.DiagnosticSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProbeCompatibility_ToolSmoke_CanaryFileCreated_ButMissingEventSequence_ReportsPartiallyCompatible()
    {
        var handler = new MockHttpMessageHandler
        {
            OnSend = req => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"pong\"}}]}")
            }
        };

        using var client = new HttpClient(handler);

        ProcessRunnerFunc mockRunner = (psi, _) =>
        {
            if (psi.Arguments.Contains("canary.txt", StringComparison.OrdinalIgnoreCase))
            {
                // Canary file was created by an external script, but model never actually emitted tool call!
                var match = System.Text.RegularExpressions.Regex.Match(psi.Arguments, @"-C\s+""([^""]+)""");
                if (match.Success)
                {
                    var canaryFile = Path.Combine(match.Groups[1].Value, "canary.txt");
                    File.WriteAllText(canaryFile, "canary-ok");
                }
                // Only plain text was returned - no tool call sequence!
                return Task.FromResult((0, "{\"type\":\"item.created\",\"item\":{\"type\":\"message\",\"content\":\"I wrote the file myself\"}}", ""));
            }
            return Task.FromResult((0, "{\"type\":\"message\",\"content\":\"pong\"}", ""));
        };

        var mockResolver = new MockRuntimeResolver(Environment.ProcessPath ?? typeof(ProviderCompatibilityProbeTests).Assembly.Location);

        var probeService = new ProviderCompatibilityProbeService(
            httpClient: client,
            runtimeResolver: mockResolver,
            fs: null,
            processRunner: mockRunner);

        var options = new ProviderProbeOptions
        {
            RunCodexSmokeTest = true,
            RunToolSmokeTest = true
        };

        var report = await probeService.ProbeCompatibilityAsync(
            "https://api.test/v1",
            "test-key",
            "test-model",
            options);

        // File existence alone is NOT sufficient: missing tool call emission MUST fail!
        Assert.Equal(CodexCompatibilityLevel.PartiallyCompatible, report.CompatibilityLevel);
        Assert.False(report.ExecTool.IsSupported);
        Assert.Contains("model did not emit a tool call", report.ExecTool.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProbeCompatibility_ToolSmoke_ToolExecuted_ButMissingSubsequentFinalMessage_ReportsPartiallyCompatible()
    {
        var handler = new MockHttpMessageHandler
        {
            OnSend = req => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"pong\"}}]}")
            }
        };

        using var client = new HttpClient(handler);

        ProcessRunnerFunc mockRunner = (psi, _) =>
        {
            if (psi.Arguments.Contains("canary.txt", StringComparison.OrdinalIgnoreCase))
            {
                var match = System.Text.RegularExpressions.Regex.Match(psi.Arguments, @"-C\s+""([^""]+)""");
                if (match.Success)
                {
                    var canaryFile = Path.Combine(match.Groups[1].Value, "canary.txt");
                    File.WriteAllText(canaryFile, "canary-ok");
                }
                // Tool call emitted, tool executed, but stream terminated before model produced subsequent final message
                var brokenSequence = string.Join("\n",
                    "{\"type\":\"item.created\",\"item\":{\"type\":\"function_call\",\"name\":\"write_file\",\"call_id\":\"call_1\"}}",
                    "{\"type\":\"item.created\",\"item\":{\"type\":\"function_call_output\",\"call_id\":\"call_1\",\"output\":\"canary-ok\"}}",
                    "{\"type\":\"item.completed\",\"item\":{\"type\":\"function_call_output\",\"call_id\":\"call_1\"}}");
                return Task.FromResult((0, brokenSequence, ""));
            }
            return Task.FromResult((0, "{\"type\":\"message\",\"content\":\"pong\"}", ""));
        };

        var mockResolver = new MockRuntimeResolver(Environment.ProcessPath ?? typeof(ProviderCompatibilityProbeTests).Assembly.Location);

        var probeService = new ProviderCompatibilityProbeService(
            httpClient: client,
            runtimeResolver: mockResolver,
            fs: null,
            processRunner: mockRunner);

        var options = new ProviderProbeOptions
        {
            RunCodexSmokeTest = true,
            RunToolSmokeTest = true
        };

        var report = await probeService.ProbeCompatibilityAsync(
            "https://api.test/v1",
            "test-key",
            "test-model",
            options);

        // Missing subsequent final message must fail qualification
        Assert.Equal(CodexCompatibilityLevel.PartiallyCompatible, report.CompatibilityLevel);
        Assert.False(report.ExecTool.IsSupported);
        Assert.Contains("subsequent final message", report.ExecTool.Detail, StringComparison.OrdinalIgnoreCase);
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
        Assert.Equal("Success", root.GetProperty("probeOutcome").GetString());
        Assert.Equal("ResponsesPassed", root.GetProperty("responsesStatus").GetString());
        Assert.Equal("workspace-write", root.GetProperty("sandboxPolicy").GetString());
        Assert.False(root.GetProperty("networkAccessEnforced").GetBoolean());
        Assert.True(root.TryGetProperty("capabilities", out var caps));
        Assert.True(caps.TryGetProperty("responsesEndpoint", out _));
        Assert.True(caps.TryGetProperty("basicCodexTurn", out _));
        Assert.True(caps.TryGetProperty("execTool", out _));
        Assert.True(caps.TryGetProperty("mcpNamespaceTools", out _));
        Assert.NotNull(root.GetProperty("diagnosticSummary").GetString());
    }
}

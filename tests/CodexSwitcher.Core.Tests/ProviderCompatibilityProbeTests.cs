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
}

using System;
using System.Collections.Generic;
using CodexSwitcher.Core.Providers.Models;
using Xunit;

namespace CodexSwitcher.Core.Tests;

public sealed class ApiProviderAdvancedConfigTests
{
    [Fact]
    public void ModelOverrides_Validate_AcceptsValidBounds()
    {
        var overrides = new CodexModelOverrides
        {
            ContextWindowTokens = 500000,
            AutoCompactTokenLimit = 400000,
            AutoCompactTokenLimitScope = CompactLimitScope.Total,
            ReasoningEffort = CodexReasoningEffort.High,
            ReasoningSummary = CodexReasoningSummary.Detailed,
            Verbosity = CodexVerbosity.Medium,
            ToolOutputTokenLimit = 8192
        };

        // Should not throw
        overrides.Validate();
    }

    [Fact]
    public void ModelOverrides_Validate_RejectsZeroOrNegativeContextWindow()
    {
        var zero = new CodexModelOverrides { ContextWindowTokens = 0 };
        Assert.Throws<ArgumentOutOfRangeException>(() => zero.Validate());

        var negative = new CodexModelOverrides { ContextWindowTokens = -100 };
        Assert.Throws<ArgumentOutOfRangeException>(() => negative.Validate());
    }

    [Fact]
    public void ModelOverrides_Validate_RejectsAutoCompactExceedingContextWindow()
    {
        var invalid = new CodexModelOverrides
        {
            ContextWindowTokens = 200000,
            AutoCompactTokenLimit = 300000
        };

        var ex = Assert.Throws<ArgumentException>(() => invalid.Validate());
        Assert.Contains("exceed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ModelOverrides_Validate_RejectsZeroOrNegativeToolOutputLimit()
    {
        var zero = new CodexModelOverrides { ToolOutputTokenLimit = 0 };
        Assert.Throws<ArgumentOutOfRangeException>(() => zero.Validate());
    }

    [Theory]
    [InlineData("Authorization")]
    [InlineData("authorization")]
    [InlineData("AUTHORIZATION")]
    [InlineData("Proxy-Authorization")]
    [InlineData("proxy-authorization")]
    [InlineData("X-API-Key")]
    [InlineData("x-api-key")]
    [InlineData("Api-Key")]
    [InlineData("api-key")]
    public void TransportOverrides_RejectsSensitiveCredentialHeaders(string headerName)
    {
        var transport = new ApiProviderTransportOverrides
        {
            HttpHeaders = new Dictionary<string, string>
            {
                [headerName] = "Bearer some-token"
            }
        };

        var ex = Assert.Throws<InvalidOperationException>(() => transport.Validate());
        Assert.Contains("sensitive authorization header", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TransportOverrides_AcceptsNonSecretCustomHeaders()
    {
        var transport = new ApiProviderTransportOverrides
        {
            HttpHeaders = new Dictionary<string, string>
            {
                ["X-Custom-Client"] = "Switchboard",
                ["X-Organization-Id"] = "org-12345",
                ["X-Tenant"] = "testing"
            },
            QueryParams = new Dictionary<string, string>
            {
                ["api-version"] = "2024-02-01"
            },
            RequestMaxRetries = 3,
            StreamMaxRetries = 5,
            StreamIdleTimeoutMs = 60000,
            WebSocketConnectTimeoutMs = 15000,
            SupportsWebSockets = true,
            SupportsStandaloneWebSearch = false
        };

        // Should not throw
        transport.Validate();
    }

    [Fact]
    public void TransportOverrides_AcceptsValidEnvHttpHeaders()
    {
        var transport = new ApiProviderTransportOverrides
        {
            EnvHttpHeaders = new Dictionary<string, string>
            {
                ["Authorization"] = "MY_PROVIDER_API_KEY",
                ["X-API-Key"] = "GLOBAL_TOKEN_ENV"
            }
        };

        // Should not throw since values are environment variable names, not plain text credentials
        transport.Validate();
    }

    [Theory]
    [InlineData("Bearer sk-ant-api-12345")]
    [InlineData("sk-proj-xyz token with spaces")]
    [InlineData("")]
    public void TransportOverrides_RejectsRawSecretTokensInEnvHttpHeaders(string rawSecret)
    {
        var transport = new ApiProviderTransportOverrides
        {
            EnvHttpHeaders = new Dictionary<string, string>
            {
                ["Authorization"] = rawSecret
            }
        };

        var ex = Assert.Throws<InvalidOperationException>(() => transport.Validate());
        Assert.Contains("env_http_headers", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TransportOverrides_ResponsesPolicy_DefaultsToAuto()
    {
        var transport = new ApiProviderTransportOverrides();
        Assert.Equal(ResponsesCompatibilityPolicy.Auto, transport.ResponsesPolicy);

        transport.ResponsesPolicy = ResponsesCompatibilityPolicy.StandardResponses;
        Assert.Equal(ResponsesCompatibilityPolicy.StandardResponses, transport.ResponsesPolicy);
    }
}

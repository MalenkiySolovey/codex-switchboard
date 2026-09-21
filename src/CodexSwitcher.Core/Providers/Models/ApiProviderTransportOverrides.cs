using System;
using System.Collections.Generic;
using System.Linq;

namespace CodexSwitcher.Core.Providers.Models;

/// <summary>
/// Network and transport configuration overrides for a Codex model provider block.
/// Supports timeout, retries, websocket, standalone web search, and custom non-secret headers/query params.
/// </summary>
public sealed class ApiProviderTransportOverrides
{
    /// <summary>Maximum retry attempts for request-level failures.</summary>
    public ulong? RequestMaxRetries { get; set; }

    /// <summary>Maximum retry attempts for stream-level failures.</summary>
    public ulong? StreamMaxRetries { get; set; }

    /// <summary>Stream idle timeout in milliseconds.</summary>
    public ulong? StreamIdleTimeoutMs { get; set; }

    /// <summary>WebSocket connection timeout in milliseconds.</summary>
    public ulong? WebSocketConnectTimeoutMs { get; set; }

    /// <summary>Explicit flag indicating whether the provider endpoint supports WebSockets.</summary>
    public bool? SupportsWebSockets { get; set; }

    /// <summary>Explicit flag indicating whether the provider supports standalone web search.</summary>
    public bool? SupportsStandaloneWebSearch { get; set; }

    /// <summary>Safe query parameters map appended to model provider requests.</summary>
    public Dictionary<string, string>? QueryParams { get; set; }

    /// <summary>
    /// Safe static HTTP headers map sent with requests.
    /// SECURITY INVARIANT: Must NEVER contain credentials or authorization tokens.
    /// Headers like Authorization, Proxy-Authorization, X-API-Key, Api-Key are strictly rejected.
    /// </summary>
    public Dictionary<string, string>? HttpHeaders { get; set; }

    /// <summary>
    /// Dynamic HTTP headers backed by environment variables (e.g. Authorization = "MY_PROVIDER_KEY").
    /// Secret header values must be passed via environment variables rather than plain text.
    /// </summary>
    public Dictionary<string, string>? EnvHttpHeaders { get; set; }

    /// <summary>
    /// Compatibility policy for /responses wire API endpoint.
    /// </summary>
    public ResponsesCompatibilityPolicy ResponsesPolicy { get; set; } = ResponsesCompatibilityPolicy.Auto;

    /// <summary>
    /// Forbidden sensitive header names that could leak credentials into config.toml or logs.
    /// </summary>
    public static readonly string[] ForbiddenHeaders =
    [
        "Authorization",
        "Proxy-Authorization",
        "X-API-Key",
        "Api-Key"
    ];

    /// <summary>
    /// Asserts that custom HTTP headers and env-backed headers conform to security invariants.
    /// </summary>
    public void Validate()
    {
        ValidateHeaders(HttpHeaders);
        ValidateEnvHeaders(EnvHttpHeaders);
    }

    /// <summary>
    /// Validates an arbitrary collection of header names against forbidden credential keys.
    /// </summary>
    public static void ValidateHeaders(IDictionary<string, string>? headers)
    {
        if (headers == null) return;

        foreach (var key in headers.Keys)
        {
            var trimmedKey = key.Trim();
            if (ForbiddenHeaders.Any(f => f.Equals(trimmedKey, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    $"Header '{trimmedKey}' is a sensitive authorization header and cannot be stored in static custom headers. " +
                    "API keys must be managed exclusively through the secure KeyBroker credential store or env_http_headers.");
            }
        }
    }

    /// <summary>
    /// Validates environment variable references in env_http_headers to ensure valid variable names.
    /// </summary>
    public static void ValidateEnvHeaders(IDictionary<string, string>? envHeaders)
    {
        if (envHeaders == null) return;

        foreach (var (header, envVar) in envHeaders)
        {
            if (string.IsNullOrWhiteSpace(header))
            {
                throw new InvalidOperationException("Header name in env_http_headers cannot be empty.");
            }

            var trimmedVar = envVar?.Trim();
            if (string.IsNullOrWhiteSpace(trimmedVar))
            {
                throw new InvalidOperationException($"Environment variable for header '{header}' in env_http_headers cannot be empty.");
            }

            // Check that value is a valid environment variable identifier, not a raw token
            if (trimmedVar.Contains(' ') || trimmedVar.Contains('\t') || trimmedVar.Contains('\n') || trimmedVar.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Value '{trimmedVar}' for header '{header}' in env_http_headers must be an environment variable name, not a raw secret token.");
            }
        }
    }
}

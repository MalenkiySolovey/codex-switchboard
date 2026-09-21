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
    /// Asserts that custom HTTP headers do not contain credential or authorization keys.
    /// </summary>
    public void Validate()
    {
        ValidateHeaders(HttpHeaders);
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
                    "API keys must be managed exclusively through the secure KeyBroker credential store.");
            }
        }
    }
}

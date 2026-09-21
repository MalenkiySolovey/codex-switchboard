namespace CodexSwitcher.Core.Providers.Models;

/// <summary>
/// Detailed outcome of a provider compatibility probe.
/// Distinguishes transient, authentication, throttling, or local sandbox failures
/// from fundamental Codex Responses protocol compatibility.
/// </summary>
public enum ProviderProbeOutcome
{
    Unknown = 0,

    /// <summary>Probe succeeded and capability checks passed.</summary>
    Success = 1,

    /// <summary>API key rejected or unauthorized (HTTP 401 / 403).</summary>
    AuthenticationFailed = 2,

    /// <summary>Rate limit or quota threshold exceeded (HTTP 429).</summary>
    RateLimited = 3,

    /// <summary>Upstream provider server error or network connection drop (HTTP 5xx / connection failure).</summary>
    UpstreamUnavailable = 4,

    /// <summary>Local Codex or OS sandbox prevented tool execution in disposable workspace.</summary>
    LocalSandboxBlocked = 5,

    /// <summary>Responses supported, but requested model slug is not recognized or unavailable (HTTP 400/404 with model error).</summary>
    ModelUnavailable = 6,

    /// <summary>Payload parameter, structure, or option rejected by endpoint (HTTP 400).</summary>
    PayloadRejected = 7,

    /// <summary>Responses accepted, but custom Codex tool calling, namespace, or schema envelope was rejected.</summary>
    CodexEnvelopeRejected = 8,

    /// <summary>Responses endpoint not found (HTTP 404 or equivalent missing route).</summary>
    EndpointMissing = 9,
}

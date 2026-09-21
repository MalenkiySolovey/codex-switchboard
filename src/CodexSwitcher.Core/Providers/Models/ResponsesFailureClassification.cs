namespace CodexSwitcher.Core.Providers.Models;

/// <summary>
/// Fine-grained failure and qualification status for HTTP /responses endpoints.
/// Distinguishes missing endpoints from authentication failures, model unavailability,
/// parameter rejection, and custom tool/namespace envelope errors.
/// </summary>
public enum ResponsesFailureClassification
{
    None = 0,

    /// <summary>Minimal Responses API call succeeded (HTTP 200 OK).</summary>
    ResponsesPassed = 1,

    /// <summary>Responses endpoint not found (HTTP 404 or equivalent missing route).</summary>
    EndpointMissing = 2,

    /// <summary>API key rejected or unauthorized (HTTP 401 / 403).</summary>
    AuthenticationFailed = 3,

    /// <summary>Responses supported, but requested model slug is not recognized or unavailable (HTTP 400/404 with model error).</summary>
    ModelUnavailable = 4,

    /// <summary>Payload parameter, structure, or option rejected by endpoint (HTTP 400).</summary>
    PayloadRejected = 5,

    /// <summary>Rate limit or quota threshold exceeded (HTTP 429).</summary>
    RateLimited = 6,

    /// <summary>Upstream provider server error or connection failure (HTTP 5xx / connection error).</summary>
    UpstreamUnavailable = 7,

    /// <summary>Responses accepted, but custom Codex tool calling, namespace, or schema envelope was rejected.</summary>
    CodexEnvelopeRejected = 8
}

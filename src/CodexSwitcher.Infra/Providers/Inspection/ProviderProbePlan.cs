using System.Text.Json;
using CodexSwitcher.Core.Providers.Catalog;

namespace CodexSwitcher.Infra.Providers.Inspection;

/// <summary>
/// Immutable, secret-free specification of a provider probe request.
/// Strictly contains NO raw API keys or sensitive credential values.
/// </summary>
public sealed record ProviderProbePlan(
    Uri TargetUri,
    string Method,
    string AuthScheme,
    IReadOnlyDictionary<string, string>? Headers,
    IReadOnlyList<string> TrustedHosts,
    string CapabilityType,
    string? ModelsPointer = null,
    RecipeResponseMapping? ResponseMapping = null
);

/// <summary>
/// Result of an executed HTTP probe containing parsed JSON, HTTP status code, or sanitized error.
/// </summary>
public sealed record ProbeHttpResponse(
    bool Success,
    JsonElement? Json,
    int StatusCode,
    string? Error
);

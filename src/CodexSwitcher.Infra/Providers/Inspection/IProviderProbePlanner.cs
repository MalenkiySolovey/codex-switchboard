using CodexSwitcher.Core.Providers.Catalog;

namespace CodexSwitcher.Infra.Providers.Inspection;

/// <summary>
/// Prepares deterministic probe request plans from provider catalog recipes.
/// Enforces GET/HEAD-only method boundaries, HTTPS scheme enforcement (except loopback),
/// and trusted-host authorization. Strictly non-network and secret-free.
/// </summary>
public interface IProviderProbePlanner
{
    /// <summary>
    /// Plans a probe request for a specific capability recipe.
    /// </summary>
    (bool Valid, ProviderProbePlan? Plan, string? Error) PlanProbe(
        ProviderDescriptor descriptor,
        ProviderCapabilityRecipe recipe,
        string baseUrl,
        string capabilityType);

    /// <summary>
    /// Plans a generic ad-hoc models probe for an unknown OpenAI-compatible provider.
    /// </summary>
    (bool Valid, ProviderProbePlan? Plan, ProviderDescriptor? SyntheticDescriptor, string? Error) PlanGenericUnknownModelsProbe(
        string rawBaseUrl);

    /// <summary>
    /// Normalizes and safely joins base URL and endpoint path with /v1 deduplication.
    /// </summary>
    string JoinBaseUrlAndPath(string baseUrl, string? path, string? strategy = null);
}

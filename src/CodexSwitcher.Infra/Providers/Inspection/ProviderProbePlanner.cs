using CodexSwitcher.Core.Providers.Catalog;
using CodexSwitcher.Core.Providers.Services;
using CodexSwitcher.Core.Routing.Models;

namespace CodexSwitcher.Infra.Providers.Inspection;

/// <summary>
/// Production implementation of <see cref="IProviderProbePlanner"/> providing
/// non-network, deterministic recipe evaluation, trusted-host whitelisting,
/// and URL normalization.
/// </summary>
public sealed class ProviderProbePlanner : IProviderProbePlanner
{
    public (bool Valid, ProviderProbePlan? Plan, string? Error) PlanProbe(
        ProviderDescriptor descriptor,
        ProviderCapabilityRecipe recipe,
        string baseUrl,
        string capabilityType)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(recipe);

        var reqConfig = recipe.Request ?? new RecipeRequest { Method = "GET", Path = "/models" };

        // Hard requirement: Only GET and HEAD are permitted for automatic inspection
        var method = reqConfig.Method?.Trim().ToUpperInvariant() ?? "GET";
        if (method != "GET" && method != "HEAD")
        {
            return (false, null, $"Security violation: Method '{method}' is not allowed for automatic inspection. Only GET and HEAD are permitted.");
        }

        // Build target URI
        string targetUrl;
        if (!string.IsNullOrWhiteSpace(reqConfig.Url))
        {
            targetUrl = reqConfig.Url;
        }
        else
        {
            targetUrl = JoinBaseUrlAndPath(baseUrl, reqConfig.Path, recipe.Strategy);
        }

        if (!Uri.TryCreate(targetUrl, UriKind.Absolute, out var targetUri))
        {
            return (false, null, "Invalid target probe URL.");
        }

        // Security boundary: Scheme check (HTTPS required, except for loopback in test environments)
        if (!targetUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) && !IsLoopback(targetUri))
        {
            return (false, null, $"Security violation: Insecure HTTP is prohibited for credential probes: {targetUri.Host}");
        }

        // Security boundary: Trusted host check
        if (!ProviderMatcher.IsHostTrusted(descriptor, targetUri))
        {
            return (false, null, $"Security violation: Target host '{targetUri.Host}' is not in the trustedHosts whitelist.");
        }

        var authScheme = reqConfig.Auth?.Trim().ToLowerInvariant() ?? "none";
        var trustedHosts = descriptor.TrustedHosts ?? [];
        var modelsPointer = recipe.Response?.Models?.Pointer ?? "/data";

        var plan = new ProviderProbePlan(
            targetUri,
            method,
            authScheme,
            reqConfig.Headers,
            trustedHosts,
            capabilityType,
            modelsPointer,
            recipe.Response);

        return (true, plan, null);
    }

    public (bool Valid, ProviderProbePlan? Plan, ProviderDescriptor? SyntheticDescriptor, string? Error) PlanGenericUnknownModelsProbe(
        string rawBaseUrl)
    {
        var normalizedBase = ProviderMatcher.NormalizeBaseUrl(rawBaseUrl);
        if (string.IsNullOrEmpty(normalizedBase))
        {
            return (false, null, null, "Invalid base URL format.");
        }

        var baseUri = new Uri(normalizedBase);
        var host = baseUri.IdnHost.ToLowerInvariant();

        // Synthesize a generic ad-hoc descriptor with strict trustedHosts limited to the target host
        var syntheticDescriptor = new ProviderDescriptor
        {
            Id = "generic-unknown",
            DisplayName = host,
            TrustedHosts = new List<string> { host },
            Capabilities = new ProviderCapabilities
            {
                Models = new ProviderCapabilityRecipe
                {
                    Status = CapabilityStatus.Supported,
                    Strategy = "openai-models",
                    Request = new RecipeRequest { Method = "GET", Path = "/models", Auth = "bearer" }
                },
                Balance = new ProviderCapabilityRecipe { Status = CapabilityStatus.Unknown, Strategy = "unknown" },
                Usage = new ProviderCapabilityRecipe { Status = CapabilityStatus.Unknown, Strategy = "unknown" },
            }
        };

        var modelsRecipe = syntheticDescriptor.Capabilities.Models!;
        var (valid, plan, error) = PlanProbe(syntheticDescriptor, modelsRecipe, normalizedBase, "models");

        return (valid, plan, syntheticDescriptor, error);
    }

    public string JoinBaseUrlAndPath(string baseUrl, string? path, string? strategy = null)
    {
        var trimmedBase = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
        var trimmedPath = (path ?? string.Empty).Trim();

        if (string.IsNullOrEmpty(trimmedPath))
            return trimmedBase;

        if (!trimmedPath.StartsWith('/'))
            trimmedPath = "/" + trimmedPath;

        // Ensure correct URL joining (/v1 + /models != /v1/v1/models)
        // 1. If baseUrl ends with /v1 and path starts with /v1/, deduplicate the /v1
        if (trimmedBase.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) &&
            trimmedPath.StartsWith("/v1/", StringComparison.OrdinalIgnoreCase))
        {
            trimmedPath = trimmedPath[3..];
        }
        else if (trimmedBase.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) &&
                 trimmedPath.Equals("/v1", StringComparison.OrdinalIgnoreCase))
        {
            trimmedPath = string.Empty;
        }
        // 2. If strategy is openai-models and baseUrl does NOT end with /v1, and path is /models
        // then ensure /v1 is prefixed (e.g. https://api.openai.com + /models -> https://api.openai.com/v1/models)
        else if (string.Equals(strategy, "openai-models", StringComparison.OrdinalIgnoreCase) &&
                 !trimmedBase.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) &&
                 trimmedPath.Equals("/models", StringComparison.OrdinalIgnoreCase))
        {
            trimmedPath = "/v1/models";
        }

        return trimmedBase + trimmedPath;
    }

    private static bool IsLoopback(Uri uri)
    {
        return uri.IsLoopback ||
               string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(uri.Host, "::1", StringComparison.OrdinalIgnoreCase);
    }
}

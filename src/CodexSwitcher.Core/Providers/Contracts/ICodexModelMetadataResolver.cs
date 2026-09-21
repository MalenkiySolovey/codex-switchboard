using CodexSwitcher.Core.Providers.Models;

namespace CodexSwitcher.Core.Providers.Contracts;

/// <summary>
/// Resolves model capability facts (such as documented maximum context window limits)
/// using deterministic precedence:
/// 1. Trusted catalog/provider metadata if explicitly verified.
/// 2. Official provider documentation-backed built-in facts.
/// 3. Live model endpoint fields if semantically known.
/// 4. Unknown models fallback (unverified).
/// </summary>
public interface ICodexModelMetadataResolver
{
    CodexModelLimitFact ResolveLimitFact(string modelSlug, string? providerId = null);

    CodexModelContextFact ResolveContextFact(
        string modelSlug,
        string? providerId = null,
        long? routeAdvertisedMax = null,
        long? requestedContext = null,
        long? codexClientContext = null);

    ModelCapabilities ResolveCapabilities(string modelSlug, string? runtimeIdentity = null);
}

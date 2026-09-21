namespace CodexSwitcher.Core.Providers.Models;

/// <summary>
/// Represents resolved model capability facts regarding context window ceilings
/// backed by official documentation, catalog metadata, or explicit user override.
/// </summary>
public sealed record CodexModelLimitFact(
    string ModelSlug,
    long? DocumentedMaxContext,
    bool IsKnown,
    string SourceDescription)
{
    public static CodexModelLimitFact Unknown(string modelSlug) =>
        new(modelSlug, null, false, "Unverified third-party model (provider maximum unknown)");

    public static CodexModelLimitFact Known(string modelSlug, long maxContext, string source) =>
        new(modelSlug, maxContext, true, source);
}

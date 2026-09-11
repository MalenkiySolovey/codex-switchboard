using CodexSwitcher.Core.Catalog;
using CodexSwitcher.Core.Models;

namespace CodexSwitcher.Core.Abstractions;

/// <summary>
/// Abstraction for declarative, read-only AI provider capability and model inspection.
/// Strictly executes GET/HEAD-only probes with safe JSON pointer extraction.
/// </summary>
public interface IDeclarativeProviderInspector
{
    /// <summary>
    /// Inspects a provider using its catalog descriptor and returns a normalized snapshot.
    /// </summary>
    Task<ApiProviderSnapshot> InspectAsync(
        ProviderDescriptor descriptor,
        string activeBaseUrl,
        string? apiKey,
        CancellationToken ct = default);

    /// <summary>
    /// Inspects a generic OpenAI-compatible provider using safe standard /models probe,
    /// never guessing balance or usage endpoints.
    /// </summary>
    Task<ApiProviderSnapshot> InspectGenericUnknownAsync(
        string rawBaseUrl,
        string? apiKey,
        CancellationToken ct = default);
}

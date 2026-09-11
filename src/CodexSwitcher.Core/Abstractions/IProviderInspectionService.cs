using CodexSwitcher.Core.Models;

namespace CodexSwitcher.Core.Abstractions;

/// <summary>
/// Domain service coordinating provider model and capability inspection
/// without leaking stored credentials to the presentation layer.
/// </summary>
public interface IProviderInspectionService
{
    /// <summary>
    /// Inspects a saved provider profile by its unique ID.
    /// Looks up the encrypted secret and catalog descriptor internally,
    /// returning only the sanitized snapshot to the caller.
    /// </summary>
    Task<ApiProviderSnapshot> InspectAsync(Guid providerProfileId, CancellationToken cancellationToken = default);
}

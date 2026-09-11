using CodexSwitcher.Core.Providers.Catalog;

namespace CodexSwitcher.Infra.Providers.Inspection;

/// <summary>
/// Hardened HTTP transport dedicated to provider inspection probes.
/// Enforces strict redirect security, credential stripping, response size capping,
/// and secret redaction from all errors and logs.
/// </summary>
public interface ISafeProviderHttpTransport
{
    /// <summary>
    /// Executes an HTTP probe against the target provider, safely injecting credentials
    /// at execution time and defending against redirect exfiltration.
    /// </summary>
    Task<ProbeHttpResponse> SendProbeAsync(
        ProviderProbePlan plan,
        ProviderDescriptor descriptor,
        string? apiKey,
        CancellationToken ct = default);
}

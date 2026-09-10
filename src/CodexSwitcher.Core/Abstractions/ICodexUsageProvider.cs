using CodexSwitcher.Core.Models;

namespace CodexSwitcher.Core.Abstractions;

/// <summary>
/// Queries rate limits for a profile by executing in an isolated sandbox.
/// Never touches the user's active %USERPROFILE%\.codex\auth.json slot.
/// </summary>
public interface ICodexUsageProvider
{
    Task<UsageFetchResult> FetchRateLimitsAsync(
        Guid profileId,
        byte[] authJsonBytes,
        CancellationToken cancellationToken = default);
}

using CodexSwitcher.Core.Models;
using CodexSwitcher.Core.Services;

namespace CodexSwitcher.Core.Abstractions;

/// <summary>
/// Orchestrates switching between ChatGPT profiles and API provider endpoints.
/// Preserves auth.json immutability during API switches and coordinates multi-resource rollbacks.
/// </summary>
public interface ICodexTargetSwitchService
{
    Task<TargetSwitchResult> SwitchToApiProviderAsync(
        Guid apiProfileId,
        SwitchExecutionOptions options,
        CancellationToken cancellationToken = default);

    Task<TargetSwitchResult> SwitchToChatGptAsync(
        Guid? targetChatGptProfileId,
        List<ProfileMetadata> allChatGptProfiles,
        SwitchExecutionOptions options,
        CancellationToken cancellationToken = default);

    Task<TargetSwitchResult> SwitchApiRouteAsync(
        Guid apiProfileId,
        string routeId,
        string newBaseUrl,
        SwitchExecutionOptions options,
        CancellationToken cancellationToken = default);
}

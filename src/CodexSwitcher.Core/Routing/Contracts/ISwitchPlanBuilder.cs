using CodexSwitcher.Core.Accounts.Models;
using CodexSwitcher.Core.Routing.Models;
using CodexSwitcher.Core.Routing.Services;

namespace CodexSwitcher.Core.Routing.Contracts;

/// <summary>
/// Prepares deterministic, validated switch execution plans without applying side effects.
/// Ensures prerequisite verification (e.g. profile presence, credential presence, broker installation)
/// without exposing secret material in the resulting plan.
/// </summary>
public interface ISwitchPlanBuilder
{
    /// <summary>
    /// Builds a plan to switch to the specified API provider profile.
    /// </summary>
    CodexSwitchPlan BuildApiProviderPlan(Guid apiProfileId, SwitchExecutionOptions options);

    /// <summary>
    /// Builds a plan to switch to a ChatGPT profile or toggle routing back to OpenAI.
    /// </summary>
    CodexSwitchPlan BuildChatGptPlan(Guid? targetProfileId, List<ProfileMetadata> allChatGptProfiles, SwitchExecutionOptions options);

    /// <summary>
    /// Builds a plan to switch the active route of an API provider.
    /// </summary>
    CodexSwitchPlan BuildApiRoutePlan(Guid apiProfileId, string routeId, string newBaseUrl, SwitchExecutionOptions options);
}

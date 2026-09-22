using CodexSwitcher.Core.Accounts.Models;
using CodexSwitcher.Core.Common.Enums;
using CodexSwitcher.Core.Common.Errors;
using CodexSwitcher.Core.Providers.Models;
using CodexSwitcher.Core.Routing.Models;
using CodexSwitcher.Core.Routing.Services;

namespace CodexSwitcher.Core.Routing.Models;

/// <summary>
/// Lifecycle stage of an atomic target switch transaction.
/// Establishes the JOURNAL_READY_BOUNDARY for future durable transaction persistence.
/// </summary>
public enum SwitchTransactionStage
{
    Prepared,
    ProcessesClosed,
    StateMutated,
    Verified,
    Completed,
    Compensating,
    Compensated
}

/// <summary>
/// Base class for deterministic, validated switch execution plans.
/// Strictly contains NO secret material (no raw API keys, passwords, or tokens).
/// </summary>
public abstract record CodexSwitchPlan
{
    public SwitchTransactionStage Stage { get; init; } = SwitchTransactionStage.Prepared;
}

/// <summary>
/// Plan to switch active inference route to a configured API provider.
/// </summary>
public sealed record ApiProviderSwitchPlan(
    ApiProviderProfile TargetProfile,
    string BrokerPath,
    SwitchExecutionOptions Options,
    EffectiveToolPolicy? ToolPolicy = null
) : CodexSwitchPlan;

/// <summary>
/// Plan to switch active inference target to a different ChatGPT account.
/// </summary>
public sealed record ChatGptAccountSwitchPlan(
    ProfileMetadata TargetProfile,
    List<ProfileMetadata> AllChatGptProfiles,
    bool IsApiRoutingActive,
    SwitchExecutionOptions Options
) : CodexSwitchPlan;

/// <summary>
/// Plan to return active inference route from an API provider back to OpenAI for the same account.
/// </summary>
public sealed record ChatGptReturnRoutingSwitchPlan(
    ProfileMetadata? ActiveProfile,
    SwitchExecutionOptions Options
) : CodexSwitchPlan;

/// <summary>
/// Plan for an idempotent switch where the requested ChatGPT account is already active and routing is OpenAI.
/// </summary>
public sealed record ChatGptNoOpSwitchPlan(
    ProfileMetadata? ActiveProfile
) : CodexSwitchPlan;

/// <summary>
/// Plan to switch an API provider's active route (e.g. Primary to Reserve).
/// </summary>
public sealed record ApiRouteSwitchPlan(
    ApiProviderProfile Profile,
    string RouteId,
    string NewBaseUrl,
    bool IsCurrentlyActiveInToml,
    SwitchExecutionOptions Options
) : CodexSwitchPlan;

/// <summary>
/// Plan representing a validation failure prior to execution (e.g. profile not found, credential missing).
/// </summary>
public sealed record InvalidSwitchPlan(
    ActiveTarget Target,
    ErrorCategory Category,
    string ErrorMessage
) : CodexSwitchPlan;

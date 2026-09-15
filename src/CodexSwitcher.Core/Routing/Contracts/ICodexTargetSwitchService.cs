using CodexSwitcher.Core.Accounts.Contracts;
using CodexSwitcher.Core.Accounts.Formatting;
using CodexSwitcher.Core.Accounts.Services;
using CodexSwitcher.Core.Common.Dispatcher;
using CodexSwitcher.Core.Common.Enums;
using CodexSwitcher.Core.Common.Environment;
using CodexSwitcher.Core.Common.Errors;
using CodexSwitcher.Core.Common.Lifecycle;
using CodexSwitcher.Core.Common.Logging;
using CodexSwitcher.Core.Common.Storage;
using CodexSwitcher.Core.Common.Time;
using CodexSwitcher.Core.Providers.Catalog;
using CodexSwitcher.Core.Providers.Contracts;
using CodexSwitcher.Core.Providers.Models;
using CodexSwitcher.Core.Providers.Services;
using CodexSwitcher.Core.Routing.Contracts;
using CodexSwitcher.Core.Security.Secrets;
using CodexSwitcher.Core.Security.Totp;
using CodexSwitcher.Core.Security.Verification;
using CodexSwitcher.Core.Settings.Contracts;
using CodexSwitcher.Core.Settings.Models;
using CodexSwitcher.Core.Threads.Contracts;
using CodexSwitcher.Core.Threads.Models;
using CodexSwitcher.Core.Transfer.Contracts;
using CodexSwitcher.Core.Transfer.Models;
using CodexSwitcher.Core.Transfer.Services;
using CodexSwitcher.Core.Usage.Contracts;
using CodexSwitcher.Core.Usage.Formatting;
using CodexSwitcher.Core.Usage.Models;
using CodexSwitcher.Core.Usage.Services;
using CodexSwitcher.Core.Routing.Models;
using CodexSwitcher.Core.Routing.Services;
using CodexSwitcher.Core.Accounts.Models;

namespace CodexSwitcher.Core.Routing.Contracts;

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

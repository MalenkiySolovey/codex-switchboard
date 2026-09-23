using CodexSwitcher.Core.Accounts.Contracts;
using CodexSwitcher.Core.Accounts.Formatting;
using CodexSwitcher.Core.Accounts.Models;
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
using CodexSwitcher.Core.Routing.Models;
using CodexSwitcher.Core.Routing.Services;
using CodexSwitcher.Core.Security.Secrets;
using CodexSwitcher.Core.Security.Totp;
using CodexSwitcher.Core.Security.Verification;
using CodexSwitcher.Core.Settings.Contracts;
using CodexSwitcher.Core.Settings.Models;
using CodexSwitcher.Core.Threads.Contracts;
using CodexSwitcher.Core.Transfer.Contracts;
using CodexSwitcher.Core.Transfer.Models;
using CodexSwitcher.Core.Transfer.Services;
using CodexSwitcher.Core.Usage.Contracts;
using CodexSwitcher.Core.Usage.Formatting;
using CodexSwitcher.Core.Usage.Models;
using CodexSwitcher.Core.Usage.Services;
using CodexSwitcher.Core.Threads.Models;

namespace CodexSwitcher.Core.Threads.Contracts;

/// <summary>
/// Service managing cross-provider thread continuation via the official Codex app-server protocol.
/// Explicitly passes modelProvider and model on thread/fork while keeping source thread immutable.
/// Strictly avoids direct SQLite or rollout file mutations.
/// </summary>
public interface ICodexThreadHandoffService
{
    Task<IReadOnlyList<CodexThreadSummary>> ListThreadsAsync(
        int limit = 50,
        CancellationToken cancellationToken = default);

    Task<ThreadForkResult> ForkThreadAsync(
        string sourceThreadId,
        string targetModelProvider,
        string targetModel,
        string? newName = null,
        CancellationToken cancellationToken = default);

    Task<ThreadForkResult> StartFreshThreadAsync(
        string targetModelProvider,
        string targetModel,
        string? cwd = null,
        string? name = null,
        CancellationToken cancellationToken = default);

    Task<ThreadCompatibilityAssessment> AssessThreadCompatibilityAsync(
        string threadId,
        EffectiveToolPolicy targetPolicy,
        CancellationToken cancellationToken = default);
}

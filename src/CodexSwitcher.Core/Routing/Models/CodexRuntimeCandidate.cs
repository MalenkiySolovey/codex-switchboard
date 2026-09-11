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
using CodexSwitcher.Core.Threads.Models;
using CodexSwitcher.Core.Transfer.Contracts;
using CodexSwitcher.Core.Transfer.Models;
using CodexSwitcher.Core.Transfer.Services;
using CodexSwitcher.Core.Usage.Contracts;
using CodexSwitcher.Core.Usage.Formatting;
using CodexSwitcher.Core.Usage.Models;
using CodexSwitcher.Core.Usage.Services;
namespace CodexSwitcher.Core.Routing.Models;

/// <summary>
/// Source origin where a candidate Codex runtime was discovered.
/// </summary>
public enum RuntimeCandidateSource
{
    /// <summary>Explicit user override configured in app settings.</summary>
    CustomOverride = 0,

    /// <summary>Versioned hash directory under %LOCALAPPDATA%\OpenAI\Codex\bin\&lt;hash&gt;\.</summary>
    OfficialHashDirectory = 1,

    /// <summary>Standard well-known installer path %LOCALAPPDATA%\OpenAI\Codex\bin\codex.exe.</summary>
    OfficialWellKnown = 2,

    /// <summary>Found in system or user PATH environment variable.</summary>
    Path = 3,
}

/// <summary>
/// A candidate Codex executable discovered on the system.
/// </summary>
public sealed record CodexRuntimeCandidate(
    string Path,
    string? Version,
    long FileSize,
    DateTimeOffset LastWriteTimeUtc,
    bool IsAvailable,
    RuntimeCandidateSource Source,
    string? Error = null);

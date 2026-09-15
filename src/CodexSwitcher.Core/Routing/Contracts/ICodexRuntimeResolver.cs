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
using CodexSwitcher.Core.Routing.Models;

namespace CodexSwitcher.Core.Routing.Contracts;

/// <summary>
/// Resolves the active Codex runtime executable, enumerates candidates deterministically,
/// and provides validation for custom executable overrides.
/// </summary>
public interface ICodexRuntimeResolver
{
    /// <summary>
    /// Resolves the active runtime, applying deterministic precedence:
    /// 1. Custom override path if specified and valid.
    /// 2. Versioned official binaries under %LOCALAPPDATA%\OpenAI\Codex\bin\&lt;hash&gt;\codex.exe.
    /// 3. Well-known official installer path %LOCALAPPDATA%\OpenAI\Codex\bin\codex.exe.
    /// 4. PATH entries.
    /// </summary>
    CodexRuntimeInfo ResolveCurrentRuntime(string? overridePath = null);

    /// <summary>
    /// Discovers and enumerates all candidate Codex executables on the system.
    /// </summary>
    IReadOnlyList<CodexRuntimeCandidate> EnumerateCandidates();

    /// <summary>
    /// Validates whether a given executable path is a valid Codex runtime.
    /// </summary>
    bool ValidateExecutable(string path, out string? version, out string? errorMessage);
}

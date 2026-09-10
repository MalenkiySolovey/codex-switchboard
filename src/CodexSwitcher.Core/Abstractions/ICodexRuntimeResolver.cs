using CodexSwitcher.Core.Models;

namespace CodexSwitcher.Core.Abstractions;

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
    bool ValidateExecutable(string path, out string? version, out string? error);
}

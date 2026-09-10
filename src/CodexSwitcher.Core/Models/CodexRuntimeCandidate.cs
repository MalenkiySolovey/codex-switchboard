namespace CodexSwitcher.Core.Models;

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

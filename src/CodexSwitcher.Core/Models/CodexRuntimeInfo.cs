namespace CodexSwitcher.Core.Models;

/// <summary>
/// Diagnostic and capability metadata for a resolved Codex runtime executable.
/// </summary>
public sealed record CodexRuntimeInfo(
    string ExecutablePath,
    string? Version,
    string? FileIdentity,
    CodexRuntimeCapabilities Capabilities)
{
    /// <summary>
    /// Computes a deterministic identity string from path, file size, and last write time.
    /// </summary>
    public static string ComputeIdentity(string path, long fileSize, DateTimeOffset lastWriteTimeUtc, string? version) =>
        $"{path}|{fileSize}|{lastWriteTimeUtc.UtcTicks}|{version ?? "unknown"}";
}

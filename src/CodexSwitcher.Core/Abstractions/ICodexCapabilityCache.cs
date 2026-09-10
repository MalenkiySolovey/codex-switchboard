using CodexSwitcher.Core.Models;

namespace CodexSwitcher.Core.Abstractions;

/// <summary>
/// In-memory cache for Codex app-server runtime capabilities keyed by executable identity.
/// Avoids repeatedly calling known-unsupported RPC methods every polling interval.
/// Contains no credential data.
/// </summary>
public interface ICodexCapabilityCache
{
    CodexRuntimeCapabilities? GetCapabilities(string executableIdentity);

    void SetCapabilities(string executableIdentity, CodexRuntimeCapabilities capabilities);

    void Invalidate(string executableIdentity);

    void Clear();
}

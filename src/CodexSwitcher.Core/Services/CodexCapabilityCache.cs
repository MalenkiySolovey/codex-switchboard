using System.Collections.Concurrent;
using CodexSwitcher.Core.Abstractions;
using CodexSwitcher.Core.Models;

namespace CodexSwitcher.Core.Services;

/// <summary>
/// Thread-safe in-memory cache of Codex app-server runtime capabilities keyed by executable identity.
/// Contains no credential data or sensitive information.
/// </summary>
public sealed class CodexCapabilityCache : ICodexCapabilityCache
{
    private readonly ConcurrentDictionary<string, CodexRuntimeCapabilities> _cache = new(StringComparer.OrdinalIgnoreCase);

    public CodexRuntimeCapabilities? GetCapabilities(string executableIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executableIdentity);
        return _cache.TryGetValue(executableIdentity, out var caps) ? caps : null;
    }

    public void SetCapabilities(string executableIdentity, CodexRuntimeCapabilities capabilities)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executableIdentity);
        ArgumentNullException.ThrowIfNull(capabilities);
        _cache[executableIdentity] = capabilities;
    }

    public void Invalidate(string executableIdentity)
    {
        if (!string.IsNullOrWhiteSpace(executableIdentity))
            _cache.TryRemove(executableIdentity, out _);
    }

    public void Clear() => _cache.Clear();
}

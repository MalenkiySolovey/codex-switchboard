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
using CodexSwitcher.Core.Routing.Contracts;
using CodexSwitcher.Core.Routing.Models;
using System.Collections.Concurrent;

namespace CodexSwitcher.Core.Routing.Services;

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

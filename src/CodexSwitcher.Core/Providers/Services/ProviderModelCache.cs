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
using CodexSwitcher.Core.Providers.Contracts;
using System.Collections.Concurrent;

namespace CodexSwitcher.Core.Providers.Services;

/// <summary>
/// Thread-safe in-memory cache for discovered models, route-specifically keyed by
/// (profileId, routeKey, revision).
/// </summary>
public sealed class ProviderModelCache : IProviderModelCache
{
    private readonly ConcurrentDictionary<string, ProviderModelCacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);

    private static string MakeKey(Guid profileId, string routeKey, int revision) =>
        $"{profileId:N}:{routeKey.Trim().ToLowerInvariant()}:{revision}";

    public bool TryGetModels(Guid profileId, string routeKey, int revision, out IReadOnlyList<string> models)
    {
        models = [];
        if (string.IsNullOrWhiteSpace(routeKey)) return false;

        var key = MakeKey(profileId, routeKey, revision);
        if (_cache.TryGetValue(key, out var entry))
        {
            models = entry.Models;
            return true;
        }

        return false;
    }

    public void SetModels(Guid profileId, string routeKey, int revision, IReadOnlyList<string> models)
    {
        if (string.IsNullOrWhiteSpace(routeKey)) return;

        var key = MakeKey(profileId, routeKey, revision);
        _cache[key] = new ProviderModelCacheEntry(profileId, routeKey, revision, models, DateTimeOffset.UtcNow);
    }

    public void Invalidate(Guid profileId, string? routeKey = null)
    {
        var prefix = routeKey != null
            ? $"{profileId:N}:{routeKey.Trim().ToLowerInvariant()}:"
            : $"{profileId:N}:";

        foreach (var key in _cache.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                _cache.TryRemove(key, out _);
            }
        }
    }

    public IReadOnlyList<string>? GetLatestModels(Guid profileId)
    {
        var prefix = $"{profileId:N}:";
        ProviderModelCacheEntry? latest = null;

        foreach (var (k, entry) in _cache)
        {
            if (k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                if (latest == null || entry.CachedAt > latest.CachedAt)
                {
                    latest = entry;
                }
            }
        }

        return latest?.Models;
    }
}

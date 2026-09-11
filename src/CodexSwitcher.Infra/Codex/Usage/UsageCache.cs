using CodexSwitcher.Core.Accounts.Contracts;
using CodexSwitcher.Core.Accounts.Formatting;
using CodexSwitcher.Core.Accounts.Services;
using CodexSwitcher.Core.Common.Dispatcher;
using CodexSwitcher.Core.Common.Enums;
using CodexSwitcher.Core.Common.Environment;
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
using CodexSwitcher.Core.Usage.Formatting;
using CodexSwitcher.Core.Usage.Services;
using CodexSwitcher.Infra.Accounts.Storage;
using CodexSwitcher.Infra.Codex.Routing;
using CodexSwitcher.Infra.Codex.Runtime;
using CodexSwitcher.Infra.Codex.Threads;
using CodexSwitcher.Infra.Codex.Usage;
using CodexSwitcher.Infra.Common.Logging;
using CodexSwitcher.Infra.Common.Paths;
using CodexSwitcher.Infra.Common.Storage;
using CodexSwitcher.Infra.Common.Time;
using CodexSwitcher.Infra.Providers.Inspection;
using CodexSwitcher.Infra.Providers.Secrets;
using CodexSwitcher.Infra.Providers.Storage;
using CodexSwitcher.Infra.Scheduling;
using CodexSwitcher.Infra.Security.Dpapi;
using CodexSwitcher.Infra.Security.Hardening;
using CodexSwitcher.Infra.Security.Totp;
using CodexSwitcher.Infra.Settings;
using CodexSwitcher.Core.Accounts.Models;
using CodexSwitcher.Core.Common.Errors;
using CodexSwitcher.Core.Usage.Contracts;
using CodexSwitcher.Core.Usage.Models;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexSwitcher.Infra.Codex.Usage;

/// <summary>
/// Thread-safe in-memory usage cache backed by atomic JSON persistence on disk.
/// Contains strictly non-sensitive metadata (rate limits, quotas, statuses).
/// Never persists tokens, passwords, or auth.json content.
/// </summary>
public sealed class UsageCache : IUsageCache, IDisposable
{
    public const int CurrentSchemaVersion = 3;

    private readonly ConcurrentDictionary<Guid, UsageCacheEntry> _entries = new();
    private readonly IFileSystem _fs;
    private readonly string _cacheFilePath;
    private readonly SemaphoreSlim _ioLock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public UsageCache(IFileSystem fs, string cacheFilePath)
    {
        _fs = fs ?? throw new ArgumentNullException(nameof(fs));
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheFilePath);
        _cacheFilePath = cacheFilePath;
    }

    public UsageCacheEntry? Get(Guid profileId)
    {
        _entries.TryGetValue(profileId, out var entry);
        return entry;
    }

    public IReadOnlyDictionary<Guid, UsageCacheEntry> GetAll() => _entries;

    public void Set(
        Guid profileId,
        RateLimitsSnapshot? snapshot,
        UsageStatus status,
        ErrorInfo? lastError = null,
        bool isStale = false,
        bool credentialConflict = false,
        CredentialConflictReason conflictReason = CredentialConflictReason.None,
        AccountActivitySnapshot? activity = null,
        AccountActivityAvailability activityAvailability = AccountActivityAvailability.Unknown)
    {
        var observedAt = snapshot?.ObservedAt ?? activity?.ObservedAt ?? DateTimeOffset.UtcNow;
        var entry = new UsageCacheEntry(
            ProfileId: profileId,
            ObservedAt: observedAt,
            Snapshot: snapshot,
            Status: status,
            LastError: lastError,
            IsStale: isStale,
            CredentialConflict: credentialConflict,
            ConflictReason: conflictReason,
            Activity: activity,
            ActivityAvailability: activityAvailability);

        _entries[profileId] = entry;
    }

    public void Invalidate(Guid profileId)
    {
        _entries.TryRemove(profileId, out _);
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_fs.FileExists(_cacheFilePath))
                return;

            byte[] bytes;
            try
            {
                bytes = _fs.ReadAllBytes(_cacheFilePath);
            }
            catch
            {
                // Unreadable file; start with clean cache
                return;
            }

            if (bytes.Length == 0)
                return;

            UsageCacheDocument? doc;
            try
            {
                doc = JsonSerializer.Deserialize<UsageCacheDocument>(bytes, JsonOpts);
            }
            catch
            {
                // Corrupt JSON; do not crash
                return;
            }

            if (doc is null || doc.SchemaVersion > CurrentSchemaVersion)
            {
                // Future schema or invalid doc; ignore safely
                return;
            }

            _entries.Clear();
            if (doc.Profiles is not null)
            {
                foreach (var (key, entry) in doc.Profiles)
                {
                    if (entry is not null)
                    {
                        // Startup behavior: mark restored entries as stale/unverified
                        var staleEntry = entry with { IsStale = true };
                        _entries[key] = staleEntry;
                    }
                }
            }
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var doc = new UsageCacheDocument
            {
                SchemaVersion = CurrentSchemaVersion,
                SavedAt = DateTimeOffset.UtcNow,
                Profiles = new Dictionary<Guid, UsageCacheEntry>(_entries)
            };

            var json = JsonSerializer.Serialize(doc, JsonOpts);
            var bytes = Encoding.UTF8.GetBytes(json);

            var dir = Path.GetDirectoryName(_cacheFilePath);
            if (!string.IsNullOrEmpty(dir))
                _fs.CreateDirectory(dir);

            _fs.WriteAllBytesAtomic(_cacheFilePath, bytes);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public void Dispose()
    {
        _ioLock.Dispose();
    }

    internal sealed class UsageCacheDocument
    {
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        public DateTimeOffset SavedAt { get; set; } = DateTimeOffset.UtcNow;
        public Dictionary<Guid, UsageCacheEntry> Profiles { get; set; } = new();
    }
}

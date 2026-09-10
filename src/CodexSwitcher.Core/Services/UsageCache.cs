using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodexSwitcher.Core.Abstractions;
using CodexSwitcher.Core.Models;

namespace CodexSwitcher.Core.Services;

/// <summary>
/// Thread-safe in-memory usage cache backed by atomic JSON persistence on disk.
/// Contains strictly non-sensitive metadata (rate limits, quotas, statuses).
/// Never persists tokens, passwords, or auth.json content.
/// </summary>
public sealed class UsageCache : IUsageCache
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

    internal sealed class UsageCacheDocument
    {
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        public DateTimeOffset SavedAt { get; set; } = DateTimeOffset.UtcNow;
        public Dictionary<Guid, UsageCacheEntry> Profiles { get; set; } = new();
    }
}

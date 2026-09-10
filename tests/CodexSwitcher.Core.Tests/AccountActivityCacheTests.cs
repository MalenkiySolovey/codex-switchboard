using System.Text;
using System.Text.Json;
using CodexSwitcher.Core.Abstractions;
using CodexSwitcher.Core.Models;
using CodexSwitcher.Core.Services;

using CodexSwitcher.Core.Tests.TestSupport;
using CodexSwitcher.Infra.Io;

namespace CodexSwitcher.Core.Tests;

public sealed class AccountActivityCacheTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly PhysicalFileSystem _fs = new();
    private readonly string _cachePath;
    private readonly Guid _profileId = Guid.NewGuid();
    private readonly DateTimeOffset _now = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    public AccountActivityCacheTests()
    {
        _cachePath = _dir.Combine("usage-cache.json");
    }

    public void Dispose()
    {
        _dir.Dispose();
    }

    [Fact]
    public async Task Test16_CachedActivity_RestoredStaleOnStartup()
    {
        var cache = new UsageCache(_fs, _cachePath);

        var activity = new AccountActivitySnapshot(
            ProfileId: _profileId,
            ObservedAt: _now,
            Summary: new AccountTokenUsageSummary(1000, 200, 60, 1, 2),
            DailyBuckets: [new DailyTokenUsage("2026-08-01", new DateOnly(2026, 8, 1), 500)]);

        cache.Set(
            _profileId,
            snapshot: null,
            status: UsageStatus.Healthy,
            lastError: null,
            isStale: false,
            credentialConflict: false,
            conflictReason: CredentialConflictReason.None,
            activity: activity);

        await cache.SaveAsync();

        // Restore in a fresh cache instance
        var freshCache = new UsageCache(_fs, _cachePath);
        await freshCache.LoadAsync();

        var entry = freshCache.Get(_profileId);
        Assert.NotNull(entry);
        Assert.True(entry.IsStale); // Guaranteed stale on reload
        Assert.NotNull(entry.Activity);
        Assert.Equal(1000L, entry.Activity.Summary?.LifetimeTokens);
        Assert.Single(entry.Activity.DailyBuckets);
        Assert.Equal(500L, entry.Activity.DailyBuckets[0].Tokens);
    }

    [Fact]
    public async Task Test17_CacheMigration_FromSchemaVersion1_UpgradesToVersion2()
    {
        // Simulate a Phase 3 cache file with schemaVersion = 1
        var v1Json = $$"""
        {
            "schemaVersion": 1,
            "savedAt": "2026-09-09T20:00:00Z",
            "profiles": {
                "{{_profileId}}": {
                    "profileId": "{{_profileId}}",
                    "observedAt": "2026-09-09T20:00:00Z",
                    "status": "Healthy",
                    "isStale": false,
                    "credentialConflict": false
                }
            }
        }
        """;

        _fs.WriteAllBytesAtomic(_cachePath, Encoding.UTF8.GetBytes(v1Json));

        var cache = new UsageCache(_fs, _cachePath);
        await cache.LoadAsync();

        var entry = cache.Get(_profileId);
        Assert.NotNull(entry);
        Assert.True(entry.IsStale);
        Assert.Null(entry.Activity); // v1 had no activity
        Assert.Equal(CredentialConflictReason.None, entry.ConflictReason);

        // Now save: must upgrade to current schemaVersion
        await cache.SaveAsync();

        var savedBytes = _fs.ReadAllBytes(_cachePath);
        using var doc = JsonDocument.Parse(savedBytes);
        Assert.Equal(UsageCache.CurrentSchemaVersion, doc.RootElement.GetProperty("schemaVersion").GetInt32());
    }

    [Fact]
    public async Task Test18_CorruptActivityCacheData_DoesNotCrashApp()
    {
        _fs.WriteAllBytesAtomic(_cachePath, Encoding.UTF8.GetBytes("{ broken json: true, ..."));

        var cache = new UsageCache(_fs, _cachePath);

        // Must not throw
        await cache.LoadAsync();

        Assert.Empty(cache.GetAll());
    }

    [Fact]
    public async Task Test19_ActivityData_ContainsNoCredentials()
    {
        var cache = new UsageCache(_fs, _cachePath);

        var activity = new AccountActivitySnapshot(
            ProfileId: _profileId,
            ObservedAt: _now,
            Summary: new AccountTokenUsageSummary(5000, 1000, 120, 3, 5),
            DailyBuckets: [new DailyTokenUsage("2026-09-01", new DateOnly(2026, 9, 1), 2500)]);

        cache.Set(
            _profileId,
            snapshot: null,
            status: UsageStatus.Healthy,
            activity: activity);

        await cache.SaveAsync();

        var json = Encoding.UTF8.GetString(_fs.ReadAllBytes(_cachePath));

        Assert.DoesNotContain("access_token", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("refresh_token", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("id_token", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("auth.json", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("bearer", json, StringComparison.OrdinalIgnoreCase);
    }
}

using System.Text;
using CodexSwitcher.Core.Models;
using CodexSwitcher.Core.Services;
using CodexSwitcher.Core.Tests.TestSupport;
using CodexSwitcher.Infra.Io;

namespace CodexSwitcher.Core.Tests;

public sealed class UsageCacheTests
{
    private static (UsageCache Cache, TempDir Dir, string FilePath) CreateCache()
    {
        var dir = new TempDir();
        var filePath = dir.Combine("usage-cache.json");
        var fs = new PhysicalFileSystem();
        var cache = new UsageCache(fs, filePath);
        return (cache, dir, filePath);
    }

    [Fact]
    public async Task ColdStart_WhenNoFileExists_InitializesEmpty()
    {
        var (cache, dir, _) = CreateCache();
        using (dir)
        {
            await cache.LoadAsync();
            Assert.Empty(cache.GetAll());
            Assert.Null(cache.Get(Guid.NewGuid()));
        }
    }

    [Fact]
    public async Task Restore_MarksAllRestoredEntriesAsStale()
    {
        var (cache, dir, filePath) = CreateCache();
        using (dir)
        {
            var id = Guid.NewGuid();
            var observed = DateTimeOffset.UtcNow.AddMinutes(-5);
            var snapshot = new RateLimitsSnapshot(
                ProfileId: id,
                ObservedAt: observed,
                PrimaryLimitId: "codex",
                Limits: [new LimitBucket("codex", "Codex", [new UsageWindow("primary", 300, "5h", 42.0, 58.0, observed.AddHours(2))], null, "plus")],
                ResetCreditsAvailable: null,
                PlanType: "plus",
                AccountEmail: "user@example.com",
                Status: UsageStatus.Healthy);

            cache.Set(id, snapshot, UsageStatus.Healthy, lastError: null, isStale: false);
            await cache.SaveAsync();

            // Create a brand new cache instance simulating app restart
            var restoredCache = new UsageCache(new PhysicalFileSystem(), filePath);
            await restoredCache.LoadAsync();

            var restored = restoredCache.Get(id);
            Assert.NotNull(restored);
            Assert.Equal(id, restored.ProfileId);
            Assert.True(restored.IsStale, "Restored entries must be marked as stale on startup.");
            Assert.Equal(UsageStatus.Healthy, restored.Status);
            Assert.Equal("plus", restored.Snapshot?.PlanType);
        }
    }

    [Fact]
    public async Task CorruptFile_HandledGracefullyWithoutThrowing()
    {
        var (cache, dir, filePath) = CreateCache();
        using (dir)
        {
            File.WriteAllText(filePath, "{ this is invalid corrupted json content ");

            // Must not throw exception
            await cache.LoadAsync();
            Assert.Empty(cache.GetAll());
        }
    }

    [Fact]
    public async Task UnsupportedFutureSchema_IgnoredGracefully()
    {
        var (cache, dir, filePath) = CreateCache();
        using (dir)
        {
            var futureJson = "{\"schemaVersion\": 999, \"profiles\": {}}";
            File.WriteAllText(filePath, futureJson);

            await cache.LoadAsync();
            Assert.Empty(cache.GetAll());
        }
    }

    [Fact]
    public async Task Invalidate_RemovesProfileEntry()
    {
        var (cache, dir, _) = CreateCache();
        using (dir)
        {
            var id = Guid.NewGuid();
            cache.Set(id, null, UsageStatus.Healthy);
            Assert.NotNull(cache.Get(id));

            cache.Invalidate(id);
            Assert.Null(cache.Get(id));
            Assert.Empty(cache.GetAll());
        }
    }

    [Fact]
    public async Task ZeroSecrets_SerializedCacheNeverContainsSensitiveMarkers()
    {
        var (cache, dir, filePath) = CreateCache();
        using (dir)
        {
            var id = Guid.NewGuid();
            var observed = DateTimeOffset.UtcNow;
            var snapshot = new RateLimitsSnapshot(
                ProfileId: id,
                ObservedAt: observed,
                PrimaryLimitId: "codex",
                Limits: [new LimitBucket("codex", "Codex", [new UsageWindow("primary", 300, "5h", 10.0, 90.0, observed.AddHours(4))], null, "pro")],
                ResetCreditsAvailable: 0,
                PlanType: "pro",
                AccountEmail: "test@example.com",
                Status: UsageStatus.Healthy);

            cache.Set(id, snapshot, UsageStatus.Healthy);
            await cache.SaveAsync();

            var rawJson = File.ReadAllText(filePath, Encoding.UTF8);

            // Assert absolute absence of known token and secret markers
            var forbidden = new[]
            {
                "refresh_token",
                "access_token",
                "id_token",
                "authorization",
                "bearer ",
                "password",
                "client_secret",
                "api_key",
                "auth.json"
            };

            foreach (var marker in forbidden)
            {
                Assert.DoesNotContain(marker, rawJson, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}


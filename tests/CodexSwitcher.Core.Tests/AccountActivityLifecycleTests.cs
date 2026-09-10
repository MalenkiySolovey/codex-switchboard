using System.Text.Json;
using CodexSwitcher.Core.Abstractions;
using CodexSwitcher.Core.Models;
using CodexSwitcher.Core.Security;
using CodexSwitcher.Core.Services;
using CodexSwitcher.Core.Tests.TestSupport;
using CodexSwitcher.Infra.Codex;
using CodexSwitcher.Infra.Io;
using CodexSwitcher.Infra.Security;

namespace CodexSwitcher.Core.Tests;

public sealed class AccountActivityLifecycleTests : IDisposable
{
    private readonly TempDir _tempDir = new();
    private readonly PhysicalFileSystem _fs = new();
    private readonly string _tempRoot;
    private readonly Guid _profileId = Guid.NewGuid();
    private readonly byte[] _authJson = """{"tokens":{"access_token":"valid","account_id":"acc-123"}}"""u8.ToArray();

    public AccountActivityLifecycleTests()
    {
        _tempRoot = _tempDir.Combine("work");
    }

    public void Dispose()
    {
        _tempDir.Dispose();
    }

    [Fact]
    public async Task Test14_PartialSuccess_QuotaSucceeds_ActivityFails_PreservesQuota()
    {
        var mockClient = new MockClientLifecycle(failUsage: true);
        var provider = new CodexUsageProvider(
            _fs,
            _tempRoot,
            "codex.exe",
            clientFactory: (exe, home) => mockClient);

        var result = await provider.FetchRateLimitsAsync(_profileId, _authJson);

        Assert.True(result.Success);
        Assert.NotNull(result.Snapshot);
        Assert.Equal(UsageStatus.Healthy, result.Snapshot.Status);
        Assert.Null(result.Activity); // Activity failed gracefully
    }

    [Fact]
    public async Task Test15_PartialSuccess_ActivitySucceeds_QuotaFails_PreservesActivity()
    {
        var mockClient = new MockClientLifecycle(failRateLimits: true);
        var provider = new CodexUsageProvider(
            _fs,
            _tempRoot,
            "codex.exe",
            clientFactory: (exe, home) => mockClient);

        var result = await provider.FetchRateLimitsAsync(_profileId, _authJson);

        Assert.True(result.Success);
        Assert.Null(result.Snapshot);
        Assert.NotNull(result.Activity);
        Assert.Equal(5000L, result.Activity.Summary?.LifetimeTokens);
    }

    [Fact]
    public async Task Test20_SingleClientProcess_AcrossRateLimitsAndUsageFetch()
    {
        var mockClient = new MockClientLifecycle();
        var clientCreatedCount = 0;

        var provider = new CodexUsageProvider(
            _fs,
            _tempRoot,
            "codex.exe",
            clientFactory: (exe, home) =>
            {
                clientCreatedCount++;
                return mockClient;
            });

        var result = await provider.FetchRateLimitsAsync(_profileId, _authJson);

        Assert.True(result.Success);
        Assert.Equal(1, clientCreatedCount);
    }

    [Fact]
    public async Task Test21_RpcCallSequence_RateLimitsBeforeUsage_InSameSession()
    {
        var mockClient = new MockClientLifecycle();

        var provider = new CodexUsageProvider(
            _fs,
            _tempRoot,
            "codex.exe",
            clientFactory: (exe, home) => mockClient);

        var result = await provider.FetchRateLimitsAsync(_profileId, _authJson);

        Assert.True(result.Success);
        Assert.Equal(["account/read", "account/rateLimits/read", "account/usage/read"], mockClient.Calls);
    }

    [Fact]
    public async Task Test22_ActivityRefresh_RespectsProcessThrottle()
    {
        var coordinator = new ProfileOperationCoordinator();
        var cache = new UsageCache(_fs, _tempDir.Combine("usage-cache.json"));
        var activeProcesses = 0;
        var maxObservedProcesses = 0;
        var throttleLock = new object();

        var mockProvider = new MockThrottleUsageProvider(async () =>
        {
            lock (throttleLock)
            {
                activeProcesses++;
                if (activeProcesses > maxObservedProcesses)
                    maxObservedProcesses = activeProcesses;
            }
            await Task.Delay(50);
            lock (throttleLock)
            {
                activeProcesses--;
            }
            var snapshot = new RateLimitsSnapshot(_profileId, DateTimeOffset.UtcNow, "codex", [], null, "plus", "test@org", UsageStatus.Healthy);
            return UsageFetchResult.Ok(snapshot);
        });

        var vault = new VaultService(new DpapiSecretProtector(), _fs, _tempDir.Combine("vault"), coordinator);
        var p1 = Guid.NewGuid();
        var p2 = Guid.NewGuid();
        vault.SaveBlob(p1, _authJson);
        vault.SaveBlob(p2, _authJson);

        var profiles = new List<ProfileMetadata>
        {
            new() { Id = p1, Nickname = "Profile 1" },
            new() { Id = p2, Nickname = "Profile 2" }
        };

        var service = new UsageService(
            mockProvider,
            cache,
            vault,
            coordinator,
            new FakeClock(),
            new UsageServiceOptions { MaxConcurrentUsageProcesses = 1 });

        // Run concurrent refreshes
        var t1 = service.RefreshAsync(profiles[0]);
        var t2 = service.RefreshAsync(profiles[1]);
        await Task.WhenAll(t1, t2);

        Assert.Equal(1, maxObservedProcesses);
    }

    [Fact]
    public async Task Test23_ActivityRefresh_CoalescesDuplicateCalls()
    {
        var coordinator = new ProfileOperationCoordinator();
        var cache = new UsageCache(_fs, _tempDir.Combine("usage-cache.json"));
        var invocationCount = 0;

        var mockProvider = new MockThrottleUsageProvider(async () =>
        {
            Interlocked.Increment(ref invocationCount);
            await Task.Delay(50);
            var snapshot = new RateLimitsSnapshot(_profileId, DateTimeOffset.UtcNow, "codex", [], null, "plus", "test@org", UsageStatus.Healthy);
            return UsageFetchResult.Ok(snapshot);
        });

        var vault = new VaultService(new DpapiSecretProtector(), _fs, _tempDir.Combine("vault"), coordinator);
        vault.SaveBlob(_profileId, _authJson);
        var profile = new ProfileMetadata { Id = _profileId, Nickname = "Profile 1" };

        var service = new UsageService(
            mockProvider,
            cache,
            vault,
            coordinator,
            new FakeClock(),
            new UsageServiceOptions { MaxConcurrentUsageProcesses = 1 });

        // Launch 3 simultaneous refreshes for the same profile
        var t1 = service.RefreshAsync(profile);
        var t2 = service.RefreshAsync(profile);
        var t3 = service.RefreshAsync(profile);

        await Task.WhenAll(t1, t2, t3);

        // Coalesced to 1 provider call
        Assert.Equal(1, invocationCount);
    }

    [Fact]
    public async Task Test24_SandboxMutation_AfterUsageRead_FollowsCasPolicy()
    {
        var coordinator = new ProfileOperationCoordinator();
        var cache = new UsageCache(_fs, _tempDir.Combine("usage-cache.json"));
        var vault = new VaultService(new DpapiSecretProtector(), _fs, _tempDir.Combine("vault"), coordinator);

        var profile = new ProfileMetadata { Id = _profileId, Nickname = "Profile 1", IsActive = false };
        vault.SaveBlob(_profileId, _authJson);

        var rotatedAuth = """{"tokens":{"access_token":"rotated-123"}}"""u8.ToArray();

        var mockProvider = new MockThrottleUsageProvider(() =>
        {
            var snapshot = new RateLimitsSnapshot(_profileId, DateTimeOffset.UtcNow, "codex", [], null, "plus", "test@org", UsageStatus.Healthy);
            return Task.FromResult(UsageFetchResult.Ok(snapshot, sandboxAuthMutated: true, rotatedAuthJson: rotatedAuth));
        });

        var service = new UsageService(
            mockProvider,
            cache,
            vault,
            coordinator,
            new FakeClock(),
            new UsageServiceOptions { MaxConcurrentUsageProcesses = 1 });

        var result = await service.RefreshAsync(profile);

        Assert.True(result.Success);
        var cached = cache.Get(_profileId);
        Assert.NotNull(cached);
        Assert.False(cached.CredentialConflict); // CAS succeeded
    }

    [Fact]
    public async Task Test25_ActiveSlot_ZeroWrite_RemainsTrueOnActiveRotation()
    {
        var coordinator = new ProfileOperationCoordinator();
        var cache = new UsageCache(_fs, _tempDir.Combine("usage-cache.json"));
        var vault = new VaultService(new DpapiSecretProtector(), _fs, _tempDir.Combine("vault"), coordinator);

        // Profile is active!
        var profile = new ProfileMetadata { Id = _profileId, Nickname = "Active Profile", IsActive = true };
        vault.SaveBlob(_profileId, _authJson);

        var rotatedAuth = """{"tokens":{"access_token":"rotated-active"}}"""u8.ToArray();

        var mockProvider = new MockThrottleUsageProvider(() =>
        {
            var snapshot = new RateLimitsSnapshot(_profileId, DateTimeOffset.UtcNow, "codex", [], null, "plus", "test@org", UsageStatus.Healthy);
            return Task.FromResult(UsageFetchResult.Ok(snapshot, sandboxAuthMutated: true, rotatedAuthJson: rotatedAuth));
        });

        var service = new UsageService(
            mockProvider,
            cache,
            vault,
            coordinator,
            new FakeClock(),
            new UsageServiceOptions { MaxConcurrentUsageProcesses = 1 });

        var result = await service.RefreshAsync(profile);

        var cached = cache.Get(_profileId);
        Assert.NotNull(cached);
        Assert.True(cached.CredentialConflict);
        Assert.Equal(CredentialConflictReason.ActiveCredentialMutation, cached.ConflictReason);
    }

    [Fact]
    public void Test35_UsedPercent100_DoesNotSetRateLimited_UnlessRateLimitReachedTypeOrServerStatusRateLimited()
    {
        var snapshot = new RateLimitsSnapshot(
            ProfileId: _profileId,
            ObservedAt: DateTimeOffset.UtcNow,
            PrimaryLimitId: "codex",
            Limits: [
                new LimitBucket("codex", "Codex", [
                    new UsageWindow("primary", 300, "5h", 100.0, 0.0, DateTimeOffset.UtcNow.AddMinutes(60))
                ], null, "plus")
            ],
            ResetCreditsAvailable: null,
            PlanType: "plus",
            AccountEmail: "test@org",
            Status: UsageStatus.Healthy);

        var state = UsagePresentationMapper.MapFromFetchResult(UsageFetchResult.Ok(snapshot), _profileId, DateTimeOffset.UtcNow);

        Assert.NotEqual(UsageVisualState.RateLimited, state.VisualState);
        Assert.Equal(UsageVisualState.Fresh, state.VisualState);
    }

    [Fact]
    public void Test36_InactiveGenerationConflict_DisplaysCorrectNotice()
    {
        var entry = new UsageCacheEntry(
            ProfileId: _profileId,
            ObservedAt: DateTimeOffset.UtcNow,
            Snapshot: null,
            Status: UsageStatus.Error,
            LastError: null,
            IsStale: false,
            CredentialConflict: true,
            ConflictReason: CredentialConflictReason.InactiveGenerationChanged);

        var state = UsagePresentationMapper.MapFromCache(entry, _profileId, DateTimeOffset.UtcNow);

        Assert.Equal(UsageVisualState.CredentialConflict, state.VisualState);
        Assert.True(state.HasNotice);
        Assert.Contains("vault during rotation", state.NoticeMessage);
        Assert.DoesNotContain("active account does not match vault profile", state.NoticeMessage);
    }

    private sealed class MockThrottleUsageProvider : ICodexUsageProvider
    {
        private readonly Func<Task<UsageFetchResult>> _fetchFunc;

        public MockThrottleUsageProvider(Func<Task<UsageFetchResult>> fetchFunc)
        {
            _fetchFunc = fetchFunc;
        }

        public Task<UsageFetchResult> FetchRateLimitsAsync(Guid profileId, byte[] authJsonBytes, CancellationToken cancellationToken = default) =>
            _fetchFunc();
    }

    private sealed class MockClientLifecycle : ICodexAppServerClient
    {
        private readonly bool _failRateLimits;
        private readonly bool _failUsage;

        public List<string> Calls { get; } = [];
        public bool IsRunning => true;

        public MockClientLifecycle(bool failRateLimits = false, bool failUsage = false)
        {
            _failRateLimits = failRateLimits;
            _failUsage = failUsage;
        }

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<JsonElement> RequestAsync(string method, object? parameters = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            Calls.Add(method);

            if (method == "initialize")
            {
                return Task.FromResult(JsonDocument.Parse("""{"status":"ok"}""").RootElement);
            }

            if (method == "account/read")
            {
                return Task.FromResult(JsonDocument.Parse("""{"account":{"type":"chatgpt","email":"test@org","planType":"plus"},"requiresOpenaiAuth":true}""").RootElement);
            }

            if (method == "account/rateLimits/read")
            {
                if (_failRateLimits) throw new InvalidOperationException("rate limits failed");
                return Task.FromResult(JsonDocument.Parse("""{"rateLimits":{"primary":{"windowDurationMins":300,"usedPercent":25,"resetsAt":1790000000}}}""").RootElement);
            }

            if (method == "account/usage/read")
            {
                if (_failUsage) throw new InvalidOperationException("usage failed");
                return Task.FromResult(JsonDocument.Parse("""{"summary":{"lifetimeTokens":5000},"dailyUsageBuckets":[]}""").RootElement);
            }

            return Task.FromResult(JsonDocument.Parse("{}").RootElement);
        }

        public Task NotifyAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
        {
            Calls.Add(method);
            return Task.CompletedTask;
        }

        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void Dispose() { }
    }

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
    }
}

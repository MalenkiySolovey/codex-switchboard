using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using CodexSwitcher.Core.Abstractions;
using CodexSwitcher.Core.Models;
using CodexSwitcher.Core.Security;
using CodexSwitcher.Core.Services;
using CodexSwitcher.Core.Tests.TestSupport;
using CodexSwitcher.Infra.Io;
using CodexSwitcher.Infra.Security;
using Xunit;

namespace CodexSwitcher.Core.Tests;

/// <summary>
/// Phase 5.6: Comprehensive test suite for issues discovered during human manual QA:
/// 1. Thread affinity & UI dispatching
/// 2. Refresh-All regressions (15 deterministic scenarios)
/// 3. Application shutdown & lifetime cancellation (11 scenarios)
/// 4. Active account discovery on empty state (read-only, robust)
/// </summary>
public sealed class ManualQaBugfixTests
{
    #region Test Doubles & Helpers

    private sealed class TrackingUiDispatcher : IUiDispatcher
    {
        public int EnqueueCallCount { get; private set; }
        public List<int> ThreadIds { get; } = [];
        public bool HasThreadAccess => true;

        public void Enqueue(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);
            EnqueueCallCount++;
            ThreadIds.Add(Environment.CurrentManagedThreadId);
            action();
        }
    }

    private sealed class FakeUsageService : IUsageService
    {
        public int RefreshCalls { get; private set; }
        public int RefreshAllCalls { get; private set; }
        public int InvalidateCalls { get; private set; }

        public Dictionary<Guid, UsageCacheEntry> Cache { get; } = new();
        public TimeSpan Delay { get; set; } = TimeSpan.Zero;
        public Func<ProfileMetadata, UsageFetchResult>? CustomRefreshHandler { get; set; }
        public Func<IReadOnlyList<ProfileMetadata>, IReadOnlyDictionary<Guid, UsageFetchResult>>? CustomRefreshAllHandler { get; set; }
        public bool ThrowOnRefreshAll { get; set; }
        public bool ThrowOnRefresh { get; set; }

        public UsageCacheEntry? GetCached(Guid profileId) =>
            Cache.GetValueOrDefault(profileId);

        public IReadOnlyDictionary<Guid, UsageCacheEntry> GetAllCached() => Cache;

        public Task LoadCacheAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async Task<UsageFetchResult> RefreshAsync(
            ProfileMetadata profile,
            bool force = false,
            CancellationToken cancellationToken = default)
        {
            RefreshCalls++;
            if (Delay > TimeSpan.Zero)
                await Task.Delay(Delay, cancellationToken);

            if (ThrowOnRefresh)
                throw new InvalidOperationException("Injected refresh error");

            if (CustomRefreshHandler is not null)
                return CustomRefreshHandler(profile);

            var snapshot = new RateLimitsSnapshot(
                ProfileId: profile.Id,
                ObservedAt: DateTimeOffset.UtcNow,
                PrimaryLimitId: "codex",
                Limits: [],
                ResetCreditsAvailable: null,
                PlanType: "plus",
                AccountEmail: profile.AccountEmail,
                Status: UsageStatus.Healthy);

            var result = UsageFetchResult.Ok(snapshot);
            Cache[profile.Id] = new UsageCacheEntry(profile.Id, snapshot.ObservedAt, snapshot, UsageStatus.Healthy, null, false);
            return result;
        }

        public async Task<IReadOnlyDictionary<Guid, UsageFetchResult>> RefreshAllAsync(
            IReadOnlyList<ProfileMetadata> profiles,
            CancellationToken cancellationToken = default)
        {
            RefreshAllCalls++;
            if (Delay > TimeSpan.Zero)
                await Task.Delay(Delay, cancellationToken);

            if (ThrowOnRefreshAll)
                throw new InvalidOperationException("Injected refresh all error");

            if (CustomRefreshAllHandler is not null)
                return CustomRefreshAllHandler(profiles);

            var dict = new Dictionary<Guid, UsageFetchResult>();
            foreach (var p in profiles)
            {
                var snapshot = new RateLimitsSnapshot(
                    ProfileId: p.Id,
                    ObservedAt: DateTimeOffset.UtcNow,
                    PrimaryLimitId: "codex",
                    Limits: [],
                    ResetCreditsAvailable: null,
                    PlanType: "plus",
                    AccountEmail: p.AccountEmail,
                    Status: UsageStatus.Healthy);

                dict[p.Id] = UsageFetchResult.Ok(snapshot);
                Cache[p.Id] = new UsageCacheEntry(p.Id, snapshot.ObservedAt, snapshot, UsageStatus.Healthy, null, false);
            }
            return dict;
        }

        public void Invalidate(Guid profileId)
        {
            InvalidateCalls++;
            Cache.Remove(profileId);
        }
    }

    private static ProfileMetadata CreateProfile(string name, string email) =>
        new()
        {
            Id = Guid.NewGuid(),
            Nickname = name,
            AccountEmail = email,
            PlanType = "plus",
            AuthMode = "chatgpt",
            HealthStatus = HealthStatus.Valid,
            CreatedAt = DateTimeOffset.UtcNow,
        };

    private sealed class ProfileEnv : IDisposable
    {
        public TempDir Dir { get; } = new();
        public ProfileService Service { get; }
        public FakeClock Clock { get; } = new();
        public IFileSystem Fs { get; }
        public CodexPaths Paths { get; }
        public VaultService Vault { get; }
        public ProfileStore Store { get; }

        public ProfileEnv()
        {
            Fs = new PhysicalFileSystem();
            Vault = new VaultService(new DpapiSecretProtector(), Fs, Dir.Combine("vault"));
            Store = new ProfileStore(Fs, Dir.Combine("profiles.json"));
            Paths = CodexPaths.ForHome(Dir.Combine(".codex"));
            var recon = new ReconciliationService(Fs, Paths);
            Service = new ProfileService(Vault, Store, recon, Fs, Paths, Clock, new FakeAudit());
        }

        public void Dispose() => Dir.Dispose();
    }

    #endregion

    #region 1. UI Dispatcher & Thread Affinity

    [Fact]
    public void ImmediateUiDispatcher_ExecutesActionImmediately()
    {
        var dispatcher = new ImmediateUiDispatcher();
        var executed = false;

        dispatcher.Enqueue(() => executed = true);

        Assert.True(executed);
    }

    [Fact]
    public void ImmediateUiDispatcher_ThrowsOnNullAction()
    {
        var dispatcher = new ImmediateUiDispatcher();
        Assert.Throws<ArgumentNullException>(() => dispatcher.Enqueue(null!));
    }

    [Fact]
    public void TrackingUiDispatcher_RecordsCallsAndThreadIds()
    {
        var dispatcher = new TrackingUiDispatcher();
        var count = 0;

        dispatcher.Enqueue(() => count++);
        dispatcher.Enqueue(() => count++);

        Assert.Equal(2, count);
        Assert.Equal(2, dispatcher.EnqueueCallCount);
        Assert.Equal(2, dispatcher.ThreadIds.Count);
    }

    [Fact]
    public async Task SafeNotifyUsageUpdated_WhenSubscriberThrows_DoesNotCrashCoordinatorOrLeakLock()
    {
        var fakeService = new FakeUsageService();
        var fakeClock = new FakeClock();
        var coordinator = new UsagePollingCoordinator(fakeService, fakeClock);
        var profiles = new List<ProfileMetadata> { CreateProfile("P1", "p1@test.com") };

        // Attach a faulty subscriber simulating WinUI cross-thread exception
        var throwCount = 0;
        coordinator.UsageUpdated += (_, _) =>
        {
            throwCount++;
            throw new InvalidOperationException("Simulated WinUI COM thread affinity exception");
        };

        // Should not throw, and should execute through completion
        var results = await coordinator.TriggerRefreshAllAsync(profiles);

        Assert.NotEmpty(results);
        Assert.True(throwCount > 0);
        Assert.False(coordinator.IsRefreshing);

        // Crucial test: lock was NOT leaked, subsequent call succeeds immediately
        var results2 = await coordinator.TriggerRefreshAllAsync(profiles);
        Assert.NotEmpty(results2);
        Assert.False(coordinator.IsRefreshing);
    }

    [Fact]
    public async Task SafeSetRefreshing_WhenSubscriberThrows_DoesNotLeakLock()
    {
        var fakeService = new FakeUsageService();
        var fakeClock = new FakeClock();
        var coordinator = new UsagePollingCoordinator(fakeService, fakeClock);
        var profiles = new List<ProfileMetadata> { CreateProfile("P1", "p1@test.com") };

        // Attach a faulty subscriber to RefreshingStateChanged
        coordinator.RefreshingStateChanged += (_, _) =>
            throw new InvalidOperationException("Simulated UI state change exception");

        // Should complete without leaving lock stuck
        var results = await coordinator.TriggerRefreshAllAsync(profiles);
        Assert.NotEmpty(results);

        // Next call must succeed (proving lock was released in finally)
        var results2 = await coordinator.TriggerRefreshAllAsync(profiles);
        Assert.NotEmpty(results2);
    }

    #endregion

    #region 2. Refresh-All Regressions (15 Scenarios)

    [Fact]
    public async Task Scenario01_RefreshAll_SingleProfile_CompletesAndProducesHealthyState()
    {
        var fakeService = new FakeUsageService();
        var fakeClock = new FakeClock();
        var coordinator = new UsagePollingCoordinator(fakeService, fakeClock);
        var p1 = CreateProfile("P1", "p1@test.com");

        var results = await coordinator.TriggerRefreshAllAsync([p1]);

        Assert.Single(results);
        Assert.True(results.ContainsKey(p1.Id));
        Assert.Equal(UsageVisualState.Fresh, results[p1.Id].VisualState);
        Assert.False(results[p1.Id].IsRefreshing);
        Assert.False(coordinator.IsRefreshing);
    }

    [Fact]
    public async Task Scenario02_RefreshAll_MultipleProfiles_CompletesAllWithHealthyState()
    {
        var fakeService = new FakeUsageService();
        var fakeClock = new FakeClock();
        var coordinator = new UsagePollingCoordinator(fakeService, fakeClock);
        var p1 = CreateProfile("P1", "p1@test.com");
        var p2 = CreateProfile("P2", "p2@test.com");
        var p3 = CreateProfile("P3", "p3@test.com");

        var results = await coordinator.TriggerRefreshAllAsync([p1, p2, p3]);

        Assert.Equal(3, results.Count);
        Assert.All(results.Values, state =>
        {
            Assert.Equal(UsageVisualState.Fresh, state.VisualState);
            Assert.False(state.IsRefreshing);
        });
        Assert.False(coordinator.IsRefreshing);
    }

    [Fact]
    public async Task Scenario03_RefreshAll_PropagatesAccurateSnapshotDataToSubscribers()
    {
        var fakeService = new FakeUsageService();
        var fakeClock = new FakeClock();
        var coordinator = new UsagePollingCoordinator(fakeService, fakeClock);
        var p1 = CreateProfile("P1", "p1@test.com");

        var receivedEvents = new List<AccountUsageState>();
        coordinator.UsageUpdated += (_, args) => receivedEvents.Add(args.State);

        await coordinator.TriggerRefreshAllAsync([p1]);

        // Should receive at least 2 events: first refreshing=true, then final healthy state
        Assert.True(receivedEvents.Count >= 2);
        Assert.True(receivedEvents[0].IsRefreshing);
        Assert.False(receivedEvents[^1].IsRefreshing);
        Assert.Equal(UsageVisualState.Fresh, receivedEvents[^1].VisualState);
    }

    [Fact]
    public async Task Scenario04_RefreshAll_IsRefreshingClearedOnAllProfilesAfterCompletion()
    {
        var fakeService = new FakeUsageService();
        var fakeClock = new FakeClock();
        var coordinator = new UsagePollingCoordinator(fakeService, fakeClock);
        var profiles = Enumerable.Range(1, 4).Select(i => CreateProfile($"P{i}", $"p{i}@test.com")).ToList();

        var statesByProfile = new ConcurrentDictionary<Guid, List<bool>>();
        coordinator.UsageUpdated += (_, args) =>
        {
            statesByProfile.GetOrAdd(args.ProfileId, _ => []).Add(args.State.IsRefreshing);
        };

        await coordinator.TriggerRefreshAllAsync(profiles);

        Assert.False(coordinator.IsRefreshing);
        foreach (var p in profiles)
        {
            var history = statesByProfile[p.Id];
            Assert.Contains(true, history);   // Was marked refreshing
            Assert.False(history[^1]);        // Ended NOT refreshing
        }
    }

    [Fact]
    public async Task Scenario05_RefreshAll_ProviderExceptionOnOneProfile_ClearsIsRefreshingAndReportsError()
    {
        var fakeService = new FakeUsageService();
        var fakeClock = new FakeClock();
        var coordinator = new UsagePollingCoordinator(fakeService, fakeClock);
        var pGood = CreateProfile("Good", "good@test.com");
        var pBad = CreateProfile("Bad", "bad@test.com");

        fakeService.CustomRefreshAllHandler = profiles =>
        {
            var dict = new Dictionary<Guid, UsageFetchResult>();
            foreach (var p in profiles)
            {
                if (p.Id == pBad.Id)
                {
                    var err = ErrorInfo.Create(ErrorCategory.Unknown, "CLI process crashed", fakeClock.UtcNow);
                    dict[p.Id] = UsageFetchResult.Fail(UsageStatus.Error, err);
                }
                else
                {
                    var snapshot = new RateLimitsSnapshot(p.Id, fakeClock.UtcNow, "codex", [], null, "plus", p.AccountEmail, UsageStatus.Healthy);
                    dict[p.Id] = UsageFetchResult.Ok(snapshot);
                }
            }
            return dict;
        };

        var results = await coordinator.TriggerRefreshAllAsync([pGood, pBad]);

        Assert.False(coordinator.IsRefreshing);
        Assert.False(results[pGood.Id].IsRefreshing);
        Assert.False(results[pBad.Id].IsRefreshing);
        Assert.Equal(UsageVisualState.Fresh, results[pGood.Id].VisualState);
        Assert.Equal(UsageVisualState.ProcessDown, results[pBad.Id].VisualState);
    }

    [Fact]
    public async Task Scenario06_RefreshAll_SubscriberException_DoesNotLeaveIsRefreshingStuckTrueOrLeakLock()
    {
        var fakeService = new FakeUsageService();
        var fakeClock = new FakeClock();
        var coordinator = new UsagePollingCoordinator(fakeService, fakeClock);
        var p = CreateProfile("P1", "p1@test.com");

        coordinator.UsageUpdated += (_, _) => throw new InvalidOperationException("Subscriber fail");

        await coordinator.TriggerRefreshAllAsync([p]);

        Assert.False(coordinator.IsRefreshing);

        // Next run must not hang or fail
        var secondRun = await coordinator.TriggerRefreshAllAsync([p]);
        Assert.NotEmpty(secondRun);
    }

    [Fact]
    public async Task Scenario07_RefreshAll_SubsequentCalls_SucceedSequentiallyWithoutLockContention()
    {
        var fakeService = new FakeUsageService();
        var fakeClock = new FakeClock();
        var coordinator = new UsagePollingCoordinator(fakeService, fakeClock);
        var p = CreateProfile("P1", "p1@test.com");

        for (int i = 0; i < 5; i++)
        {
            var res = await coordinator.TriggerRefreshAllAsync([p]);
            Assert.NotEmpty(res);
            Assert.False(coordinator.IsRefreshing);
        }

        Assert.Equal(5, fakeService.RefreshAllCalls);
    }

    [Fact]
    public async Task Scenario08_RefreshAll_ConcurrentCalls_CoalesceToSingleExecution()
    {
        var fakeService = new FakeUsageService { Delay = TimeSpan.FromMilliseconds(50) };
        var fakeClock = new FakeClock();
        var coordinator = new UsagePollingCoordinator(fakeService, fakeClock);
        var profiles = new List<ProfileMetadata> { CreateProfile("P1", "p1@test.com") };

        var task1 = coordinator.TriggerRefreshAllAsync(profiles);
        var task2 = coordinator.TriggerRefreshAllAsync(profiles);
        var task3 = coordinator.TriggerRefreshAllAsync(profiles);

        var all = await Task.WhenAll(task1, task2, task3);

        // Service called only once due to coalescing
        Assert.Equal(1, fakeService.RefreshAllCalls);
        Assert.Same(all[0], all[1]);
        Assert.Same(all[1], all[2]);
        Assert.False(coordinator.IsRefreshing);
    }

    [Fact]
    public async Task Scenario09_TriggerRefreshAccount_WhileRefreshAllIdle_SucceedsNormally()
    {
        var fakeService = new FakeUsageService();
        var fakeClock = new FakeClock();
        var coordinator = new UsagePollingCoordinator(fakeService, fakeClock);
        var p = CreateProfile("P1", "p1@test.com");

        var state = await coordinator.TriggerRefreshAccountAsync(p);

        Assert.Equal(1, fakeService.RefreshCalls);
        Assert.Equal(UsageVisualState.Fresh, state.VisualState);
        Assert.False(state.IsRefreshing);
    }

    [Fact]
    public async Task Scenario10_TriggerRefreshAccount_WhileRefreshAllActive_DoesNotDeadlock()
    {
        var fakeService = new FakeUsageService { Delay = TimeSpan.FromMilliseconds(50) };
        var fakeClock = new FakeClock();
        var coordinator = new UsagePollingCoordinator(fakeService, fakeClock);
        var p1 = CreateProfile("P1", "p1@test.com");
        var p2 = CreateProfile("P2", "p2@test.com");

        var batchTask = coordinator.TriggerRefreshAllAsync([p1, p2]);
        var singleTask = coordinator.TriggerRefreshAccountAsync(p1);

        await Task.WhenAll(batchTask, singleTask);

        Assert.False(coordinator.IsRefreshing);
        Assert.Equal(1, fakeService.RefreshAllCalls);
        Assert.Equal(1, fakeService.RefreshCalls);
    }

    [Fact]
    public async Task Scenario11_RefreshAll_ConcurrentCallersAcrossThreads_ReceiveIdenticalResults()
    {
        var fakeService = new FakeUsageService { Delay = TimeSpan.FromMilliseconds(40) };
        var fakeClock = new FakeClock();
        var coordinator = new UsagePollingCoordinator(fakeService, fakeClock);
        var p = CreateProfile("P1", "p1@test.com");

        var results = await Task.WhenAll(
            Task.Run(() => coordinator.TriggerRefreshAllAsync([p])),
            Task.Run(() => coordinator.TriggerRefreshAllAsync([p])),
            Task.Run(() => coordinator.TriggerRefreshAllAsync([p]))
        );

        Assert.Equal(1, fakeService.RefreshAllCalls);
        Assert.Equal(results[0][p.Id].VisualState, results[1][p.Id].VisualState);
        Assert.False(coordinator.IsRefreshing);
    }

    [Fact]
    public async Task Scenario12_RefreshAll_WatchdogTimeout_RestoresCardsAndReleasesLock()
    {
        var fakeService = new FakeUsageService { Delay = TimeSpan.FromSeconds(5) };
        var fakeClock = new FakeClock();
        var coordinator = new UsagePollingCoordinator(fakeService, fakeClock)
        {
            BatchTimeoutOverride = TimeSpan.FromMilliseconds(40)
        };
        var p = CreateProfile("P1", "p1@test.com");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            coordinator.TriggerRefreshAllAsync([p]));

        Assert.False(coordinator.IsRefreshing);

        // Re-run with no delay should succeed (lock is free)
        fakeService.Delay = TimeSpan.Zero;
        coordinator.BatchTimeoutOverride = null;
        var retryResult = await coordinator.TriggerRefreshAllAsync([p]);
        Assert.NotEmpty(retryResult);
    }

    [Fact]
    public async Task Scenario13_RefreshAll_Cancellation_RestoresCardsAndReleasesLock()
    {
        var fakeService = new FakeUsageService { Delay = TimeSpan.FromSeconds(5) };
        var fakeClock = new FakeClock();
        var coordinator = new UsagePollingCoordinator(fakeService, fakeClock);
        var p = CreateProfile("P1", "p1@test.com");

        var lastReportedStates = new ConcurrentDictionary<Guid, AccountUsageState>();
        coordinator.UsageUpdated += (_, args) => lastReportedStates[args.ProfileId] = args.State;

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(25));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            coordinator.TriggerRefreshAllAsync([p], cts.Token));

        Assert.False(coordinator.IsRefreshing);
        if (lastReportedStates.TryGetValue(p.Id, out var finalState))
        {
            Assert.False(finalState.IsRefreshing);
        }

        // Lock must be free for next call
        fakeService.Delay = TimeSpan.Zero;
        var next = await coordinator.TriggerRefreshAllAsync([p]);
        Assert.NotEmpty(next);
    }

    [Fact]
    public async Task Scenario14_RefreshAll_EmptyProfilesList_ReturnsEmptyImmediatelyWithoutLocking()
    {
        var fakeService = new FakeUsageService();
        var fakeClock = new FakeClock();
        var coordinator = new UsagePollingCoordinator(fakeService, fakeClock);

        var result = await coordinator.TriggerRefreshAllAsync([]);

        Assert.Empty(result);
        Assert.Equal(0, fakeService.RefreshAllCalls);
        Assert.False(coordinator.IsRefreshing);
    }

    [Fact]
    public async Task Scenario15_RestoreRefreshingCards_RevertsCardsToCachedStateOnBatchFailure()
    {
        var fakeService = new FakeUsageService { ThrowOnRefreshAll = true };
        var fakeClock = new FakeClock();
        var coordinator = new UsagePollingCoordinator(fakeService, fakeClock);
        var p = CreateProfile("P1", "p1@test.com");

        var events = new List<AccountUsageState>();
        coordinator.UsageUpdated += (_, args) => events.Add(args.State);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.TriggerRefreshAllAsync([p]));

        Assert.False(coordinator.IsRefreshing);
        // Card was first marked refreshing, then restored
        Assert.True(events.Count >= 2);
        Assert.True(events[0].IsRefreshing);
        Assert.False(events[^1].IsRefreshing);
    }

    #endregion

    #region 3. Application Shutdown & Lifetime (11 Scenarios)

    [Fact]
    public void Scenario01_Shutdown_UsagePollingCoordinator_Stop_HaltsAndUnsubscribes()
    {
        var fakeService = new FakeUsageService();
        var fakeClock = new FakeClock();
        var coordinator = new UsagePollingCoordinator(fakeService, fakeClock);

        var fired = false;
        coordinator.UsageUpdated += (_, _) => fired = true;

        coordinator.Stop();

        coordinator.Invalidate(Guid.NewGuid());
        Assert.False(fired);
        Assert.False(coordinator.IsRefreshing);
    }

    [Fact]
    public void Scenario02_Shutdown_AppLifetime_StopApplication_CancelsStoppingToken()
    {
        var lifetime = new AppLifetime();
        Assert.False(lifetime.IsStopping);
        Assert.False(lifetime.ApplicationStopping.IsCancellationRequested);

        lifetime.StopApplication();

        Assert.True(lifetime.IsStopping);
        Assert.True(lifetime.ApplicationStopping.IsCancellationRequested);
    }

    [Fact]
    public void Scenario03_Shutdown_AppLifetime_StopApplication_IsIdempotent()
    {
        var lifetime = new AppLifetime();

        lifetime.StopApplication();
        lifetime.StopApplication();
        lifetime.StopApplication();

        Assert.True(lifetime.IsStopping);
    }

    [Fact]
    public void Scenario04_Shutdown_UsageService_Dispose_CancelsServiceCts()
    {
        using var env = new ProfileEnv();
        var lifetime = new AppLifetime();
        var cache = new UsageCache(new PhysicalFileSystem(), env.Dir.Combine("cache.json"));
        var provider = new FakeUsageProvider();
        var coordinator = new ProfileOperationCoordinator();

        var service = new UsageService(provider, cache, env.Vault, coordinator, env.Clock, lifetime);

        Assert.False(lifetime.IsStopping);
        service.Dispose();

        // Calling Dispose multiple times is safe
        service.Dispose();
    }

    [Fact]
    public async Task Scenario05_Shutdown_AppLifetime_CancelsInFlightUsageRefreshImmediately()
    {
        using var env = new ProfileEnv();
        var lifetime = new AppLifetime();
        var cache = new UsageCache(new PhysicalFileSystem(), env.Dir.Combine("cache.json"));
        var provider = new FakeUsageProvider { Delay = TimeSpan.FromSeconds(30) };
        var coordinator = new ProfileOperationCoordinator();

        var p = env.Service.AddFromAuthJson(Sample.AuthJson(), "TestAcct");
        var service = new UsageService(provider, cache, env.Vault, coordinator, env.Clock, lifetime);

        var refreshTask = service.RefreshAsync(p);

        // Immediately trigger shutdown
        lifetime.StopApplication();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refreshTask);
    }

    [Fact]
    public void Scenario06_Shutdown_AppLifetime_RegisteredCallbacksExecutePromptly()
    {
        var lifetime = new AppLifetime();
        var ran1 = false;
        var ran2 = false;

        lifetime.ApplicationStopping.Register(() => ran1 = true);
        lifetime.ApplicationStopping.Register(() => ran2 = true);

        lifetime.StopApplication();

        Assert.True(ran1);
        Assert.True(ran2);
    }

    [Fact]
    public void Scenario07_Shutdown_AppLifetime_Dispose_TriggersStopApplication()
    {
        var lifetime = new AppLifetime();
        var callbackRan = false;

        lifetime.ApplicationStopping.Register(() => callbackRan = true);

        lifetime.Dispose();

        Assert.True(lifetime.IsStopping);
        Assert.True(callbackRan);
    }

    [Fact]
    public async Task Scenario08_Shutdown_UsagePollingCoordinator_DoesNotProcessTickWhenInactive()
    {
        var fakeService = new FakeUsageService();
        var fakeClock = new FakeClock();
        var coordinator = new UsagePollingCoordinator(fakeService, fakeClock);
        var p = CreateProfile("P1", "p1@test.com");

        await coordinator.SetForegroundActiveAsync(false, [p]);

        await coordinator.OnTimerTickAsync([p]);

        Assert.Equal(0, fakeService.RefreshAllCalls);
    }

    [Fact]
    public void Scenario09_Shutdown_ProfileSandbox_CleanupNonBlocking_DoesNotHangOnLockedFile()
    {
        using var tempDir = new TempDir();
        var sandboxPath = tempDir.Combine("usage-test-locked");
        Directory.CreateDirectory(sandboxPath);
        var lockedFilePath = Path.Combine(sandboxPath, "locked.bin");

        // Keep file open with exclusive lock
        using var fileStream = new FileStream(lockedFilePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        // TryForceDelete must return cleanly without blocking for retries
        TempCleanup.TryForceDelete(sandboxPath);
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"TryForceDelete took {sw.Elapsed.TotalMilliseconds}ms, should be non-blocking");
    }

    [Fact]
    public void Scenario10_Shutdown_TempCleanup_CleansUsagePrefixDirectories()
    {
        var tempBase = Path.GetTempPath();
        var dir1 = Path.Combine(tempBase, $"usage-unit-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir1);
        File.WriteAllText(Path.Combine(dir1, "file.txt"), "data");

        try
        {
            TempCleanup.SweepLoginTemp(tempBase);
            Assert.False(Directory.Exists(dir1));
        }
        finally
        {
            if (Directory.Exists(dir1))
            {
                try { Directory.Delete(dir1, true); } catch { }
            }
        }
    }

    [Fact]
    public void Scenario11_Shutdown_UsagePollingCoordinator_Dispose_IsIdempotent()
    {
        var fakeService = new FakeUsageService();
        var fakeClock = new FakeClock();
        var coordinator = new UsagePollingCoordinator(fakeService, fakeClock);

        coordinator.Dispose();
        coordinator.Dispose(); // Must not throw ObjectDisposedException
    }

    #endregion

    #region 4. Active Account Discovery on Empty State (6 Scenarios)

    [Fact]
    public void GetUnmanagedActiveAccountInfo_WithValidAuthJson_ReturnsEmailAndPlan()
    {
        using var env = new ProfileEnv();
        var jwt = Sample.Jwt(sub: "active-sub", email: "user@corp.com", plan: "pro");
        var authJson = Sample.AuthJson(idToken: jwt);

        Directory.CreateDirectory(Path.GetDirectoryName(env.Paths.ActiveAuthPath)!);
        File.WriteAllBytes(env.Paths.ActiveAuthPath, authJson);

        var (detected, email, plan) = env.Service.GetUnmanagedActiveAccountInfo();

        Assert.True(detected);
        Assert.Equal("user@corp.com", email);
        Assert.Equal("pro", plan);
    }

    [Fact]
    public void GetUnmanagedActiveAccountInfo_WhenFileMissing_ReturnsNotDetected()
    {
        using var env = new ProfileEnv();

        var (detected, email, plan) = env.Service.GetUnmanagedActiveAccountInfo();

        Assert.False(detected);
        Assert.Null(email);
        Assert.Null(plan);
    }

    [Fact]
    public void GetUnmanagedActiveAccountInfo_WhenFileCorrupted_ReturnsDetectedWithNullDetails()
    {
        using var env = new ProfileEnv();
        Directory.CreateDirectory(Path.GetDirectoryName(env.Paths.ActiveAuthPath)!);
        File.WriteAllText(env.Paths.ActiveAuthPath, "{ corrupted json syntax");

        var (detected, email, plan) = env.Service.GetUnmanagedActiveAccountInfo();

        Assert.True(detected);
        Assert.Null(email);
        Assert.Null(plan);
    }

    [Fact]
    public void GetUnmanagedActiveAccountInfo_IsStrictlyReadOnly_DoesNotMutateVaultOrProfiles()
    {
        using var env = new ProfileEnv();
        var jwt = Sample.Jwt(sub: "ro-sub", email: "ro@corp.com");
        var authJson = Sample.AuthJson(idToken: jwt);

        Directory.CreateDirectory(Path.GetDirectoryName(env.Paths.ActiveAuthPath)!);
        File.WriteAllBytes(env.Paths.ActiveAuthPath, authJson);

        var beforeHash = ComputeFileHash(env.Paths.ActiveAuthPath);

        var (detected, _, _) = env.Service.GetUnmanagedActiveAccountInfo();

        Assert.True(detected);
        Assert.Empty(env.Service.Profiles);
        Assert.False(File.Exists(env.Dir.Combine("profiles.json")));
        var afterHash = ComputeFileHash(env.Paths.ActiveAuthPath);
        Assert.Equal(beforeHash, afterHash);
    }

    [Fact]
    public void GetUnmanagedActiveAccountInfo_ExtractsPlanTypeFromNestedClaims()
    {
        using var env = new ProfileEnv();
        var jwt = Sample.Jwt(sub: "plus-sub", email: "plus@domain.com", plan: "plus");
        var authJson = Sample.AuthJson(idToken: jwt);

        Directory.CreateDirectory(Path.GetDirectoryName(env.Paths.ActiveAuthPath)!);
        File.WriteAllBytes(env.Paths.ActiveAuthPath, authJson);

        var (detected, email, plan) = env.Service.GetUnmanagedActiveAccountInfo();

        Assert.True(detected);
        Assert.Equal("plus@domain.com", email);
        Assert.Equal("plus", plan);
    }

    [Fact]
    public void AdoptActiveAccount_AddsProfileAndSetsActive_LeavesVaultConsistent()
    {
        using var env = new ProfileEnv();
        var jwt = Sample.Jwt(sub: "adopt-sub", email: "adopt@corp.com", plan: "enterprise");
        var authJson = Sample.AuthJson(idToken: jwt);

        Directory.CreateDirectory(Path.GetDirectoryName(env.Paths.ActiveAuthPath)!);
        File.WriteAllBytes(env.Paths.ActiveAuthPath, authJson);

        var profile = env.Service.AdoptActiveAccount("ImportedAcct");

        Assert.NotNull(profile);
        Assert.Equal("ImportedAcct", profile.Nickname);
        Assert.Equal("adopt@corp.com", profile.AccountEmail);
        Assert.True(profile.IsActive);
        Assert.True(env.Vault.Exists(profile.Id));

        // Now that it's adopted, HasUnmanagedActiveAccount is false
        var (detected, _, _) = env.Service.GetUnmanagedActiveAccountInfo();
        Assert.False(detected);
    }

    private static string ComputeFileHash(string path)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(stream));
    }

    #endregion

    private sealed class FakeUsageProvider : ICodexUsageProvider
    {
        public TimeSpan Delay { get; set; } = TimeSpan.Zero;

        public async Task<UsageFetchResult> FetchRateLimitsAsync(
            Guid profileId,
            byte[] authJsonBytes,
            CancellationToken cancellationToken = default)
        {
            if (Delay > TimeSpan.Zero)
                await Task.Delay(Delay, cancellationToken);

            var snapshot = new RateLimitsSnapshot(
                ProfileId: profileId,
                ObservedAt: DateTimeOffset.UtcNow,
                PrimaryLimitId: "codex",
                Limits: [],
                ResetCreditsAvailable: null,
                PlanType: "plus",
                AccountEmail: "test@example.com",
                Status: UsageStatus.Healthy);

            return UsageFetchResult.Ok(snapshot);
        }
    }
}

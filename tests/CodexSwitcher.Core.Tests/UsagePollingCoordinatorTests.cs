using CodexSwitcher.Core.Abstractions;
using CodexSwitcher.Core.Models;
using CodexSwitcher.Core.Services;
using CodexSwitcher.Core.Tests.TestSupport;

namespace CodexSwitcher.Core.Tests;

public sealed class UsagePollingCoordinatorTests
{
    private sealed class FakeUsageService : IUsageService
    {
        public int RefreshCalls { get; private set; }
        public int RefreshAllCalls { get; private set; }
        public int InvalidateCalls { get; private set; }

        public Dictionary<Guid, UsageCacheEntry> Cache { get; } = new();
        public TimeSpan Delay { get; set; } = TimeSpan.Zero;

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

    [Fact]
    public void InitialState_IsActiveAndNotRefreshing()
    {
        var fakeService = new FakeUsageService();
        var fakeClock = new FakeClock();
        var coordinator = new UsagePollingCoordinator(fakeService, fakeClock);

        Assert.True(coordinator.IsForegroundActive);
        Assert.False(coordinator.IsRefreshing);
        Assert.Null(coordinator.LastAuthoritativeRefreshAt);
    }

    [Fact]
    public async Task SetForegroundActive_SuspendsAndResumes()
    {
        var fakeService = new FakeUsageService();
        var fakeClock = new FakeClock();
        var coordinator = new UsagePollingCoordinator(fakeService, fakeClock);
        var profiles = new List<ProfileMetadata> { CreateProfile("Account1", "acc1@test.org") };

        await coordinator.SetForegroundActiveAsync(false, profiles);
        Assert.False(coordinator.IsForegroundActive);

        // While inactive, timer tick does not refresh
        await coordinator.OnTimerTickAsync(profiles);
        Assert.Equal(0, fakeService.RefreshAllCalls);

        // When resumed, since LastAuthoritativeRefreshAt is null, it should trigger refresh immediately
        await coordinator.SetForegroundActiveAsync(true, profiles);
        Assert.True(coordinator.IsForegroundActive);
        Assert.Equal(1, fakeService.RefreshAllCalls);
    }

    [Fact]
    public async Task OnTimerTick_WhenActive_TriggersRefresh()
    {
        var fakeService = new FakeUsageService();
        var fakeClock = new FakeClock();
        var coordinator = new UsagePollingCoordinator(fakeService, fakeClock);
        var profiles = new List<ProfileMetadata> { CreateProfile("Account1", "acc1@test.org") };

        await coordinator.OnTimerTickAsync(profiles);
        Assert.Equal(1, fakeService.RefreshAllCalls);
        Assert.NotNull(coordinator.LastAuthoritativeRefreshAt);
    }

    [Fact]
    public async Task SetForegroundActive_TriggersRefreshOnRestore_WhenElapsedExceedsInterval()
    {
        var fakeService = new FakeUsageService();
        var fakeClock = new FakeClock();
        var coordinator = new UsagePollingCoordinator(fakeService, fakeClock)
        {
            PollingInterval = TimeSpan.FromMinutes(5)
        };
        var profiles = new List<ProfileMetadata> { CreateProfile("Account1", "acc1@test.org") };

        // Initial tick
        await coordinator.OnTimerTickAsync(profiles);
        Assert.Equal(1, fakeService.RefreshAllCalls);

        // Minimize window
        await coordinator.SetForegroundActiveAsync(false, profiles);

        // Advance clock by 6 minutes (> 5 minutes interval)
        fakeClock.UtcNow = fakeClock.UtcNow.AddMinutes(6);

        // Restore window
        await coordinator.SetForegroundActiveAsync(true, profiles);
        Assert.Equal(2, fakeService.RefreshAllCalls);
    }

    [Fact]
    public async Task SetForegroundActive_SkipsRefreshOnRestore_WhenRecentlyRefreshed()
    {
        var fakeService = new FakeUsageService();
        var fakeClock = new FakeClock();
        var coordinator = new UsagePollingCoordinator(fakeService, fakeClock)
        {
            PollingInterval = TimeSpan.FromMinutes(5)
        };
        var profiles = new List<ProfileMetadata> { CreateProfile("Account1", "acc1@test.org") };

        // Initial tick
        await coordinator.OnTimerTickAsync(profiles);
        Assert.Equal(1, fakeService.RefreshAllCalls);

        // Minimize window
        await coordinator.SetForegroundActiveAsync(false, profiles);

        // Advance clock by only 2 minutes (< 5 minutes interval)
        fakeClock.UtcNow = fakeClock.UtcNow.AddMinutes(2);

        // Restore window
        await coordinator.SetForegroundActiveAsync(true, profiles);
        // RefreshAll should NOT be called again
        Assert.Equal(1, fakeService.RefreshAllCalls);
    }

    [Fact]
    public async Task TriggerRefreshAll_PreventsOverlappingReentrancy()
    {
        var fakeService = new FakeUsageService { Delay = TimeSpan.FromMilliseconds(50) };
        var fakeClock = new FakeClock();
        var coordinator = new UsagePollingCoordinator(fakeService, fakeClock);
        var profiles = new List<ProfileMetadata> { CreateProfile("Account1", "acc1@test.org") };

        var task1 = coordinator.TriggerRefreshAllAsync(profiles);
        var task2 = coordinator.TriggerRefreshAllAsync(profiles);

        await Task.WhenAll(task1, task2);

        // Second overlapping call was skipped by semaphore reentrancy guard
        Assert.Equal(1, fakeService.RefreshAllCalls);
    }

    [Fact]
    public async Task TriggerRefreshAccount_FiresUsageUpdatedEvent()
    {
        var fakeService = new FakeUsageService();
        var fakeClock = new FakeClock();
        var coordinator = new UsagePollingCoordinator(fakeService, fakeClock);
        var profile = CreateProfile("Account1", "acc1@test.org");

        var updatedEvents = new List<AccountUsageUpdatedEventArgs>();
        coordinator.UsageUpdated += (_, args) => updatedEvents.Add(args);

        var state = await coordinator.TriggerRefreshAccountAsync(profile);

        Assert.Equal(1, fakeService.RefreshCalls);
        Assert.Equal(UsageVisualState.Fresh, state.VisualState);
        // At least 2 events fired: refreshing and fresh
        Assert.Contains(updatedEvents, e => e.State.IsRefreshing);
        Assert.Contains(updatedEvents, e => !e.State.IsRefreshing && e.State.VisualState == UsageVisualState.Fresh);
    }

    [Fact]
    public void Invalidate_ClearsCacheAndFiresNeverLoadedState()
    {
        var fakeService = new FakeUsageService();
        var fakeClock = new FakeClock();
        var coordinator = new UsagePollingCoordinator(fakeService, fakeClock);
        var profile = CreateProfile("Account1", "acc1@test.org");

        AccountUsageUpdatedEventArgs? firedEvent = null;
        coordinator.UsageUpdated += (_, args) => firedEvent = args;

        coordinator.Invalidate(profile.Id);

        Assert.Equal(1, fakeService.InvalidateCalls);
        Assert.NotNull(firedEvent);
        Assert.Equal(profile.Id, firedEvent!.ProfileId);
        Assert.Equal(UsageVisualState.NeverLoaded, firedEvent.State.VisualState);
    }
}

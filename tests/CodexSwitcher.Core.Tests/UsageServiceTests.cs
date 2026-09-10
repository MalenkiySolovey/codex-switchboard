using CodexSwitcher.Core.Abstractions;
using CodexSwitcher.Core.Models;
using CodexSwitcher.Core.Services;
using CodexSwitcher.Core.Tests.TestSupport;
using CodexSwitcher.Infra.Io;
using CodexSwitcher.Infra.Security;

namespace CodexSwitcher.Core.Tests;

public sealed class UsageServiceTests
{
    private sealed class FakeUsageProvider : ICodexUsageProvider
    {
        public int InvocationCount { get; private set; }
        public int MaxConcurrentInvocations { get; private set; }
        private int _currentInvocations;

        public Func<Guid, byte[], CancellationToken, Task<UsageFetchResult>>? Implementation { get; set; }
        public TimeSpan Delay { get; set; } = TimeSpan.Zero;

        public async Task<UsageFetchResult> FetchRateLimitsAsync(
            Guid profileId,
            byte[] authJsonBytes,
            CancellationToken cancellationToken = default)
        {
            var current = Interlocked.Increment(ref _currentInvocations);
            lock (this)
            {
                if (current > MaxConcurrentInvocations)
                    MaxConcurrentInvocations = current;
                InvocationCount++;
            }

            try
            {
                if (Delay > TimeSpan.Zero)
                    await Task.Delay(Delay, cancellationToken).ConfigureAwait(false);

                if (Implementation is not null)
                    return await Implementation(profileId, authJsonBytes, cancellationToken).ConfigureAwait(false);

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
            finally
            {
                Interlocked.Decrement(ref _currentInvocations);
            }
        }
    }

    private static (UsageService Service, FakeUsageProvider Provider, VaultService Vault, UsageCache Cache, TempDir Dir) CreateFixture(
        int maxConcurrency = 1,
        IProfileOperationCoordinator? coordinator = null)
    {
        var dir = new TempDir();
        var fs = new PhysicalFileSystem();
        var coord = coordinator ?? new ProfileOperationCoordinator();
        var vault = new VaultService(new DpapiSecretProtector(), fs, dir.Combine("vault"), coord);
        var cache = new UsageCache(fs, dir.Combine("usage-cache.json"));
        var provider = new FakeUsageProvider();
        var clock = new FakeClock();
        var options = new UsageServiceOptions { MaxConcurrentUsageProcesses = maxConcurrency };

        var service = new UsageService(provider, cache, vault, coord, clock, options);
        return (service, provider, vault, cache, dir);
    }

    [Fact]
    public async Task Coalescing_WhenSameProfileRefreshedConcurrently_InvokesProviderOnce()
    {
        var (service, provider, vault, _, dir) = CreateFixture();
        using (dir)
        using (service)
        {
            var profileId = Guid.NewGuid();
            vault.SaveBlob(profileId, Sample.AuthJson());
            var profile = new ProfileMetadata { Id = profileId, Nickname = "Account 1" };

            provider.Delay = TimeSpan.FromMilliseconds(100);

            var t1 = service.RefreshAsync(profile);
            var t2 = service.RefreshAsync(profile);
            var t3 = service.RefreshAsync(profile);

            var results = await Task.WhenAll(t1, t2, t3);

            Assert.Equal(1, provider.InvocationCount);
            Assert.All(results, r => Assert.Equal(UsageStatus.Healthy, r.Status));
        }
    }

    [Fact]
    public async Task BoundedConcurrency_WhenNProfilesRefreshed_ActiveCountNeverExceedsThrottle()
    {
        var (service, provider, vault, _, dir) = CreateFixture(maxConcurrency: 1);
        using (dir)
        using (service)
        {
            var profiles = Enumerable.Range(1, 5).Select(i =>
            {
                var id = Guid.NewGuid();
                vault.SaveBlob(id, Sample.AuthJson(idToken: Sample.Jwt(email: $"user{i}@example.com")));
                return new ProfileMetadata { Id = id, Nickname = $"Account {i}" };
            }).ToList();

            provider.Delay = TimeSpan.FromMilliseconds(50);

            var results = await service.RefreshAllAsync(profiles);

            Assert.Equal(5, results.Count);
            Assert.Equal(1, provider.MaxConcurrentInvocations);
            Assert.Equal(5, provider.InvocationCount);
        }
    }

    [Fact]
    public async Task CallerCancellation_DoesNotKillSharedOperationForOtherCallers()
    {
        var (service, provider, vault, _, dir) = CreateFixture();
        using (dir)
        using (service)
        {
            var profileId = Guid.NewGuid();
            vault.SaveBlob(profileId, Sample.AuthJson());
            var profile = new ProfileMetadata { Id = profileId, Nickname = "Account 1" };

            var tcs = new TaskCompletionSource<UsageFetchResult>();
            provider.Implementation = (_, _, _) => tcs.Task;

            using var caller1Cts = new CancellationTokenSource();
            using var caller2Cts = new CancellationTokenSource();

            var task1 = service.RefreshAsync(profile, false, caller1Cts.Token);
            var task2 = service.RefreshAsync(profile, false, caller2Cts.Token);

            // Cancel caller 1
            caller1Cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task1);

            // Fulfill underlying operation
            var snapshot = new RateLimitsSnapshot(profileId, DateTimeOffset.UtcNow, "codex", [], null, "pro", "test@example.com", UsageStatus.Healthy);
            tcs.SetResult(UsageFetchResult.Ok(snapshot));

            // Caller 2 must still receive the completed result!
            var result2 = await task2;
            Assert.Equal(UsageStatus.Healthy, result2.Status);
        }
    }

    [Fact]
    public async Task InactiveProfile_RotatedCredential_CasSuccess()
    {
        var (service, provider, vault, cache, dir) = CreateFixture();
        using (dir)
        using (service)
        {
            var profileId = Guid.NewGuid();
            var initialBytes = Sample.AuthJson(lastRefresh: "2026-06-01T00:00:00Z");
            vault.SaveBlob(profileId, initialBytes);

            var profile = new ProfileMetadata { Id = profileId, Nickname = "Inactive Account", IsActive = false };
            var rotatedBytes = Sample.AuthJson(lastRefresh: "2026-06-01T12:00:00Z", refreshToken: "rotated-rt-123");

            provider.Implementation = (_, _, _) =>
            {
                var snap = new RateLimitsSnapshot(profileId, DateTimeOffset.UtcNow, "codex", [], null, "plus", "test@example.com", UsageStatus.Healthy);
                return Task.FromResult(UsageFetchResult.Ok(snap, sandboxAuthMutated: true, rotatedAuthJson: rotatedBytes));
            };

            var res = await service.RefreshAsync(profile);

            Assert.Equal(UsageStatus.Healthy, res.Status);

            // Assert vault updated with rotated bytes
            var loaded = vault.LoadBlob(profileId);
            Assert.Equal(rotatedBytes, loaded);

            // Assert cache updated without conflict
            var cached = cache.Get(profileId);
            Assert.NotNull(cached);
            Assert.False(cached.CredentialConflict);
        }
    }

    [Fact]
    public async Task InactiveProfile_RotatedCredential_CasConflict_RefusesToOverwriteVault()
    {
        var (service, provider, vault, cache, dir) = CreateFixture();
        using (dir)
        using (service)
        {
            var profileId = Guid.NewGuid();
            var initialBytes = Sample.AuthJson(lastRefresh: "2026-06-01T00:00:00Z");
            vault.SaveBlob(profileId, initialBytes);

            var profile = new ProfileMetadata { Id = profileId, Nickname = "Inactive Account", IsActive = false };
            var concurrentBytes = Sample.AuthJson(lastRefresh: "2026-06-02T00:00:00Z", refreshToken: "concurrent-rt");
            var rotatedBytes = Sample.AuthJson(lastRefresh: "2026-06-01T06:00:00Z", refreshToken: "stale-rotated-rt");

            provider.Implementation = (_, _, _) =>
            {
                // Simulate concurrent mutation in vault while provider was executing
                vault.SaveBlob(profileId, concurrentBytes);

                var snap = new RateLimitsSnapshot(profileId, DateTimeOffset.UtcNow, "codex", [], null, "plus", "test@example.com", UsageStatus.Healthy);
                return Task.FromResult(UsageFetchResult.Ok(snap, sandboxAuthMutated: true, rotatedAuthJson: rotatedBytes));
            };

            var res = await service.RefreshAsync(profile);

            // Conflict detected: must NOT overwrite concurrent vault data!
            Assert.Equal(UsageStatus.Error, res.Status);
            var loaded = vault.LoadBlob(profileId);
            Assert.Equal(concurrentBytes, loaded);

            var cached = cache.Get(profileId);
            Assert.NotNull(cached);
            Assert.True(cached.CredentialConflict);
        }
    }

    [Fact]
    public async Task ActiveProfile_SandboxMutation_NeverWritesActiveAuthJsonOrVault()
    {
        var (service, provider, vault, cache, dir) = CreateFixture();
        using (dir)
        using (service)
        {
            var profileId = Guid.NewGuid();
            var initialBytes = Sample.AuthJson(lastRefresh: "2026-06-01T00:00:00Z");
            vault.SaveBlob(profileId, initialBytes);

            // Setup active file on disk
            var activePath = dir.Combine(".codex", "auth.json");
            Directory.CreateDirectory(Path.GetDirectoryName(activePath)!);
            File.WriteAllBytes(activePath, initialBytes);

            var profile = new ProfileMetadata { Id = profileId, Nickname = "Active Account", IsActive = true };
            var rotatedBytes = Sample.AuthJson(lastRefresh: "2026-06-01T12:00:00Z", refreshToken: "sandbox-rotated-rt");

            provider.Implementation = (_, _, _) =>
            {
                var snap = new RateLimitsSnapshot(profileId, DateTimeOffset.UtcNow, "codex", [], null, "plus", "test@example.com", UsageStatus.Healthy);
                return Task.FromResult(UsageFetchResult.Ok(snap, sandboxAuthMutated: true, rotatedAuthJson: rotatedBytes));
            };

            var res = await service.RefreshAsync(profile);

            // Active file must NOT be written to!
            var activeOnDisk = File.ReadAllBytes(activePath);
            Assert.Equal(initialBytes, activeOnDisk);

            // Vault must NOT be blindly updated!
            var vaultBytes = vault.LoadBlob(profileId);
            Assert.Equal(initialBytes, vaultBytes);

            // Conflict flagged in cache
            var cached = cache.Get(profileId);
            Assert.NotNull(cached);
            Assert.True(cached.CredentialConflict);
        }
    }

    [Fact]
    public async Task UsageRefresh_Vs_Switch_SerializesSafelyWithoutDeadlocks()
    {
        var sharedCoordinator = new ProfileOperationCoordinator();
        var (usageService, _, vault, _, dir) = CreateFixture(coordinator: sharedCoordinator);
        using (dir)
        using (usageService)
        {
            var profileAId = Guid.NewGuid();
            var profileBId = Guid.NewGuid();

            var bytesA = Sample.AuthJson(idToken: Sample.Jwt(email: "a@example.com"));
            var bytesB = Sample.AuthJson(idToken: Sample.Jwt(email: "b@example.com"));

            vault.SaveBlob(profileAId, bytesA);
            vault.SaveBlob(profileBId, bytesB);

            var profileA = new ProfileMetadata { Id = profileAId, Nickname = "Profile A", IsActive = true };
            var profileB = new ProfileMetadata { Id = profileBId, Nickname = "Profile B", IsActive = false };

            var profileStore = new ProfileStore(new PhysicalFileSystem(), dir.Combine("profiles.json"));
            profileStore.SaveAll([profileA, profileB]);

            var codexPaths = new CodexPaths(dir.Combine(".codex", "auth.json"), dir.Combine(".codex", "config.toml"), dir.Combine(".codex"));
            Directory.CreateDirectory(dir.Combine(".codex"));
            File.WriteAllBytes(codexPaths.ActiveAuthPath, bytesA);

            var switchService = new SwitchService(
                vault,
                profileStore,
                new PhysicalFileSystem(),
                new FakeProcessManager(),
                new FakeConfigStore(),
                new FakeClock(),
                new FakeAudit(),
                codexPaths,
                dir.Combine("backups"),
                sharedCoordinator);

            var switchOptions = new SwitchExecutionOptions(CloseReopenMode.DoNothing, TimeSpan.Zero, 3);

            // Run concurrent refresh and switch operations touching profile A
            var refreshTask = Task.Run(() => usageService.RefreshAsync(profileA));
            var switchTask = Task.Run(() => switchService.SwitchAsync([profileA, profileB], profileBId, switchOptions));

            await Task.WhenAll(refreshTask, switchTask);

            var refreshResult = await refreshTask;
            var switchResult = await switchTask;

            Assert.Equal(UsageStatus.Healthy, refreshResult.Status);
            Assert.Equal(SwitchOutcome.Success, switchResult.Outcome);
        }
    }

    [Fact]
    public async Task UsageRefresh_Vs_Switch_IndependentProfile_ProceedsConcurrently()
    {
        var sharedCoordinator = new ProfileOperationCoordinator();
        var (usageService, provider, vault, _, dir) = CreateFixture(coordinator: sharedCoordinator);
        using (dir)
        using (usageService)
        {
            var profileAId = Guid.NewGuid();
            var profileBId = Guid.NewGuid();
            var profileCId = Guid.NewGuid();

            vault.SaveBlob(profileAId, Sample.AuthJson(idToken: Sample.Jwt(email: "a@example.com")));
            vault.SaveBlob(profileBId, Sample.AuthJson(idToken: Sample.Jwt(email: "b@example.com")));
            vault.SaveBlob(profileCId, Sample.AuthJson(idToken: Sample.Jwt(email: "c@example.com")));

            var profileC = new ProfileMetadata { Id = profileCId, Nickname = "Profile C" };

            // Simulate long lock on profiles A and B (as in switch A->B)
            using (await sharedCoordinator.LockTwoAsync(profileAId, profileBId))
            {
                // Refresh on independent Profile C must NOT be blocked!
                var refreshTask = usageService.RefreshAsync(profileC);
                var completedTask = await Task.WhenAny(refreshTask, Task.Delay(1000));

                Assert.Same(refreshTask, completedTask);
                var result = await refreshTask;
                Assert.Equal(UsageStatus.Healthy, result.Status);
            }
        }
    }
}


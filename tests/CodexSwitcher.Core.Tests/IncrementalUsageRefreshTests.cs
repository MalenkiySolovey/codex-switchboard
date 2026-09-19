using CodexSwitcher.Core.Tests.TestSupport;
using Xunit;

namespace CodexSwitcher.Core.Tests;

public class IncrementalUsageRefreshTests
{
    private sealed class DelayUsageProvider : ICodexUsageProvider
    {
        public List<(Guid ProfileId, UsageFetchOptions? Options)> Calls { get; } = new();

        public Task<UsageFetchResult> FetchRateLimitsAsync(
            Guid profileId, byte[] authJsonBytes, CancellationToken cancellationToken = default) =>
            FetchRateLimitsAsync(profileId, authJsonBytes, null, cancellationToken);

        public async Task<UsageFetchResult> FetchRateLimitsAsync(
            Guid profileId, byte[] authJsonBytes, UsageFetchOptions? options, CancellationToken cancellationToken = default)
        {
            lock (Calls)
            {
                Calls.Add((profileId, options));
            }

            await Task.Delay(20, cancellationToken);
            var now = DateTimeOffset.UtcNow;
            var snapshot = new RateLimitsSnapshot(
                ProfileId: profileId,
                ObservedAt: now,
                PrimaryLimitId: "codex",
                Limits: [
                    new LimitBucket("codex", "Codex", [
                        new UsageWindow("primary", 300, "5h", 20.0, 80.0, now.AddHours(3))
                    ], null, "plus")
                ],
                ResetCreditsAvailable: null,
                PlanType: "plus",
                AccountEmail: "test@example.com",
                Status: UsageStatus.Healthy,
                OrdinaryUsageAllowed: true);

            return UsageFetchResult.Ok(snapshot);
        }
    }

    [Fact]
    public async Task RefreshAllAsync_InvokesProgressiveCallback_AsEachFinishes()
    {
        using var dir = new TempDir();
        var fs = new PhysicalFileSystem();
        var coord = new ProfileOperationCoordinator();
        var vault = new VaultService(new DpapiSecretProtector(), fs, dir.Combine("vault"), coord);
        var cache = new UsageCache(fs, dir.Combine("usage-cache.json"));
        var clock = new FakeClock();
        var provider = new DelayUsageProvider();

        var p1 = new ProfileMetadata { Id = Guid.NewGuid(), Nickname = "P1" };
        var p2 = new ProfileMetadata { Id = Guid.NewGuid(), Nickname = "P2" };
        var p3 = new ProfileMetadata { Id = Guid.NewGuid(), Nickname = "P3" };

        var dummyAuth = """{"auth_mode":"chatgpt"}"""u8.ToArray();
        vault.SaveBlob(p1.Id, dummyAuth);
        vault.SaveBlob(p2.Id, dummyAuth);
        vault.SaveBlob(p3.Id, dummyAuth);

        using var usageService = new UsageService(provider, cache, vault, coord, clock);

        var completedOrder = new List<Guid>();

        var results = await usageService.RefreshAllAsync(
            [p1, p2, p3],
            onAccountCompleted: (id, res) =>
            {
                lock (completedOrder)
                {
                    completedOrder.Add(id);
                }
            });

        Assert.Equal(3, results.Count);
        Assert.Equal(3, completedOrder.Count);
        Assert.Contains(p1.Id, completedOrder);
        Assert.Contains(p2.Id, completedOrder);
        Assert.Contains(p3.Id, completedOrder);

        // Verify provider received background fetch options
        Assert.All(provider.Calls, call =>
        {
            Assert.NotNull(call.Options);
            Assert.True(call.Options.ExcludeResetCreditDetails);
            Assert.False(call.Options.IncludeActivity);
        });
    }

    [Fact]
    public async Task UsagePollingCoordinator_NotifiesProgressiveUpdates()
    {
        using var dir = new TempDir();
        var fs = new PhysicalFileSystem();
        var coord = new ProfileOperationCoordinator();
        var vault = new VaultService(new DpapiSecretProtector(), fs, dir.Combine("vault"), coord);
        var cache = new UsageCache(fs, dir.Combine("usage-cache.json"));
        var clock = new FakeClock();
        var provider = new DelayUsageProvider();

        var p1 = new ProfileMetadata { Id = Guid.NewGuid(), Nickname = "P1" };
        var p2 = new ProfileMetadata { Id = Guid.NewGuid(), Nickname = "P2" };

        var dummyAuth = """{"auth_mode":"chatgpt"}"""u8.ToArray();
        vault.SaveBlob(p1.Id, dummyAuth);
        vault.SaveBlob(p2.Id, dummyAuth);

        using var usageService = new UsageService(provider, cache, vault, coord, clock);
        using var pollingCoordinator = new UsagePollingCoordinator(usageService, clock);

        var notifiedProfiles = new List<Guid>();
        pollingCoordinator.UsageUpdated += (sender, args) =>
        {
            if (args.State.Status == UsageStatus.Healthy)
            {
                lock (notifiedProfiles)
                {
                    notifiedProfiles.Add(args.ProfileId);
                }
            }
        };

        var finalResults = await pollingCoordinator.TriggerRefreshAllAsync([p1, p2]);

        Assert.Equal(2, finalResults.Count);
        Assert.Contains(p1.Id, notifiedProfiles);
        Assert.Contains(p2.Id, notifiedProfiles);
    }
}

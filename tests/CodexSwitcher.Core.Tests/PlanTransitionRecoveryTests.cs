using System.Text.Json;
using CodexSwitcher.Core.Tests.TestSupport;
using Xunit;

namespace CodexSwitcher.Core.Tests;

public class PlanTransitionRecoveryTests
{
    [Fact]
    public void Parser_ReadsOrdinaryUsageAllowed()
    {
        var json = """
        {
            "planType": "free",
            "ordinaryUsageAllowed": false,
            "rateLimits": {
                "primary": { "usedPercent": 100, "windowMinutes": 180, "resetsAt": "2026-09-20T00:00:00Z" }
            }
        }
        """;

        using var doc = JsonDocument.Parse(json);
        var detail = CodexUsageResponseParser.ParseRateLimitsDetail(doc.RootElement);

        Assert.False(detail.OrdinaryUsageAllowed);
    }

    [Fact]
    public void Parser_HandlesMissingOrdinaryUsageAllowedAsNull()
    {
        var json = """
        {
            "planType": "plus",
            "rateLimits": {
                "primary": { "usedPercent": 40, "windowMinutes": 180, "resetsAt": "2026-09-20T00:00:00Z" }
            }
        }
        """;

        using var doc = JsonDocument.Parse(json);
        var detail = CodexUsageResponseParser.ParseRateLimitsDetail(doc.RootElement);

        Assert.Null(detail.OrdinaryUsageAllowed);
    }

    [Fact]
    public void PresentationMapper_HandlesOrdinaryUsageDisallowed()
    {
        var now = DateTimeOffset.UtcNow;
        var profileId = Guid.NewGuid();
        var snapshot = new RateLimitsSnapshot(
            ProfileId: profileId,
            ObservedAt: now,
            PrimaryLimitId: "codex",
            Limits: [
                new LimitBucket("codex", "Codex", [
                    new UsageWindow("primary", 300, "5h", 100.0, 0.0, now.AddHours(1))
                ], null, "free")
            ],
            ResetCreditsAvailable: 0,
            PlanType: "free",
            AccountEmail: "user@example.com",
            Status: UsageStatus.Healthy,
            OrdinaryUsageAllowed: false);

        var fetchResult = UsageFetchResult.Ok(snapshot);
        var state = UsagePresentationMapper.MapFromFetchResult(fetchResult, profileId, now);

        Assert.Equal(UsageStatus.Healthy, state.Status);
        Assert.False(state.OrdinaryUsageAllowed);
        Assert.Equal("free", state.PlanType);
    }

    [Fact]
    public void PresentationMapper_RetainsStaleSnapshotOnTransientError()
    {
        var now = DateTimeOffset.UtcNow;
        var profileId = Guid.NewGuid();
        var snapshot = new RateLimitsSnapshot(
            ProfileId: profileId,
            ObservedAt: now.AddMinutes(-5),
            PrimaryLimitId: "codex",
            Limits: [
                new LimitBucket("codex", "Codex", [
                    new UsageWindow("primary", 300, "5h", 45.0, 55.0, now.AddHours(2))
                ], null, "plus")
            ],
            ResetCreditsAvailable: 0,
            PlanType: "plus",
            AccountEmail: "user@example.com",
            Status: UsageStatus.Healthy,
            OrdinaryUsageAllowed: true);

        var previousCached = new UsageCacheEntry(
            ProfileId: profileId,
            ObservedAt: now.AddMinutes(-5),
            Snapshot: snapshot,
            Status: UsageStatus.Healthy,
            LastError: null,
            IsStale: false);

        var staleEntry = previousCached with { IsStale = true, Status = UsageStatus.ProcessDown };
        var state = UsagePresentationMapper.MapFromCache(staleEntry, profileId, now);

        Assert.True(state.IsStale);
        Assert.Single(state.Windows);
        Assert.Equal(45.0, state.Windows[0].UsedPercent);
    }

    [Fact]
    public void ProfileService_Reauthenticate_UpdatesCredentialsAndActiveFile()
    {
        using var dir = new TempDir();
        var fs = new PhysicalFileSystem();
        var coord = new ProfileOperationCoordinator();
        var vault = new VaultService(new DpapiSecretProtector(), fs, dir.Combine("vault"), coord);
        var store = new ProfileStore(fs, dir.Combine("profiles.json"));
        var clock = new FakeClock();
        var audit = new FakeAudit();
        var paths = new CodexPaths(dir.Combine(".codex"), dir.Combine(".codex/auth.json"), dir.Combine(".codex/config.toml"));

        fs.CreateDirectory(paths.CodexHome);

        var profileId = Guid.NewGuid();
        var oldAuth = """{"auth_mode":"chatgpt","id_token":"eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiJ1c2VyMTIzIiwiZW1haWwiOiJvbGRAZXhhbXBsZS5jb20ifQ.signature"}"""u8.ToArray();
        vault.SaveBlob(profileId, oldAuth);
        fs.WriteAllBytesAtomic(paths.ActiveAuthPath, oldAuth);

        var profile = new ProfileMetadata
        {
            Id = profileId,
            Nickname = "My Account",
            AccountEmail = "old@example.com",
            AccountSub = "user123",
            IsActive = true,
            HealthStatus = HealthStatus.NeedsReLogin,
            BlobFingerprint = Fingerprint.Compute(oldAuth),
        };
        store.SaveAll([profile]);

        var reconciliation = new ReconciliationService(fs, paths);
        var service = new ProfileService(vault, store, reconciliation, fs, paths, clock, audit);
        service.Load();

        var newAuth = """{"auth_mode":"chatgpt","id_token":"eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiJ1c2VyMTIzIiwiZW1haWwiOiJuZXdAZXhhbXBsZS5jb20iLCJwbGFuVHlwZSI6InBsdXMifQ.signature"}"""u8.ToArray();

        var updated = service.Reauthenticate(profileId, newAuth);

        Assert.Equal(HealthStatus.Valid, updated.HealthStatus);
        Assert.Null(updated.LastError);
        Assert.Equal(Fingerprint.Compute(newAuth), updated.BlobFingerprint);
        Assert.Equal(newAuth, vault.LoadBlob(profileId));
        Assert.True(fs.FileExists(paths.ActiveAuthPath));
        Assert.Equal(newAuth, fs.ReadAllBytes(paths.ActiveAuthPath));
    }
}

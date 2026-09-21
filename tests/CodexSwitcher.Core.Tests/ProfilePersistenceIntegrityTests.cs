using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using CodexSwitcher.Core.Accounts.Contracts;
using CodexSwitcher.Core.Accounts.Models;
using CodexSwitcher.Core.Accounts.Services;
using CodexSwitcher.Core.Common.Errors;
using CodexSwitcher.Core.Common.Storage;
using CodexSwitcher.Core.Tests.TestSupport;
using CodexSwitcher.Core.Usage.Contracts;
using CodexSwitcher.Core.Usage.Models;
using CodexSwitcher.Core.Usage.Services;
using CodexSwitcher.Infra.Accounts.Storage;
using CodexSwitcher.Infra.Common.Paths;
using CodexSwitcher.Infra.Common.Storage;
using CodexSwitcher.Infra.Security.Dpapi;
using Xunit;

namespace CodexSwitcher.Core.Tests;

/// <summary>
/// Regression and safety invariant tests verifying protection against destructive truncation,
/// lost updates, silent corrupt-state wiping, and orphan credential retention.
/// </summary>
public sealed class ProfilePersistenceIntegrityTests
{
    private static readonly JsonSerializerOptions CaseInsensitiveJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private sealed class TestFixture : IDisposable
    {
        public TempDir TempDir { get; } = new();
        public PhysicalFileSystem Fs { get; } = new();
        public FakeClock Clock { get; } = new();
        public FakeAudit Audit { get; } = new();
        public string ProfilesPath => TempDir.Combine("profiles.json");
        public string VaultDir => TempDir.Combine("vault");
        public string BackupsDir => TempDir.Combine("backups", "profiles");
        public VaultService Vault { get; }
        public ProfileStore Store { get; }
        public CodexPaths Paths { get; }
        public ProfileService ProfilesService { get; }

        public TestFixture()
        {
            Vault = new VaultService(new DpapiSecretProtector(), Fs, VaultDir);
            Store = new ProfileStore(Fs, ProfilesPath);
            Paths = CodexPaths.ForHome(TempDir.Combine(".codex"));
            var recon = new ReconciliationService(Fs, Paths);
            ProfilesService = new ProfileService(Vault, Store, recon, Fs, Paths, Clock, Audit);
        }

        public List<ProfileMetadata> CreateProfiles(int count)
        {
            var list = new List<ProfileMetadata>();
            for (int i = 0; i < count; i++)
            {
                var id = Guid.NewGuid();
                var token = Sample.Jwt(sub: $"sub-{i}", email: $"user{i}@example.com", plan: i < 3 ? "plus" : "free");
                var authBytes = Sample.AuthJson(idToken: token);
                var fp = Vault.SaveBlob(id, authBytes);

                list.Add(new ProfileMetadata
                {
                    Id = id,
                    Nickname = $"Account {i}",
                    AccountEmail = $"user{i}@example.com",
                    AccountSub = $"sub-{i}",
                    AuthMode = "chatgpt",
                    PlanType = i < 3 ? "plus" : "free",
                    CreatedAt = Clock.UtcNow.AddMinutes(-i),
                    HealthStatus = HealthStatus.Valid,
                    BlobFingerprint = fp,
                    SortOrder = i,
                    IsActive = i == 0
                });
            }
            return list;
        }

        public void Dispose() => TempDir.Dispose();
    }

    [Fact]
    public void TruncationGuard_ThrowsAndPreservesDisk_WhenShrinkingUnderNormalUpdate()
    {
        using var f = new TestFixture();
        var twelve = f.CreateProfiles(12);
        f.Store.SaveAll(twelve, ProfileSaveIntent.NormalUpdate);

        var loaded = f.Store.LoadAll();
        Assert.Equal(12, loaded.Count);

        // Attempt to save only 1 profile under NormalUpdate (reproducing the exact preview.2 regression)
        var onlyOne = new List<ProfileMetadata> { twelve[0] };
        var ex = Assert.Throws<ProfileTruncationException>(() =>
        {
            f.Store.SaveAll(onlyOne, ProfileSaveIntent.NormalUpdate);
        });

        Assert.Equal(12, ex.ExistingCount);
        Assert.Equal(1, ex.AttemptedCount);

        // Verify disk was untouched and still contains all 12 profiles
        var reloaded = f.Store.LoadAll();
        Assert.Equal(12, reloaded.Count);
        Assert.Equal(twelve.Select(p => p.Id), reloaded.Select(p => p.Id));
    }

    [Fact]
    public void ExplicitDelete_AllowsCountToDecrease()
    {
        using var f = new TestFixture();
        var twelve = f.CreateProfiles(12);
        f.Store.SaveAll(twelve, ProfileSaveIntent.NormalUpdate);

        // Legitimate user deletion of an account
        var eleven = twelve.Take(11).ToList();
        f.Store.SaveAll(eleven, ProfileSaveIntent.ExplicitDelete);

        var loaded = f.Store.LoadAll();
        Assert.Equal(11, loaded.Count);
    }

    [Fact]
    public void ProfileService_Remove_UsesExplicitDelete_DecreasingCountLegitimately()
    {
        using var f = new TestFixture();
        var twelve = f.CreateProfiles(12);
        f.Store.SaveAll(twelve, ProfileSaveIntent.NormalUpdate);

        f.ProfilesService.Load();
        Assert.Equal(12, f.ProfilesService.Profiles.Count);

        var toRemove = f.ProfilesService.Profiles.Last().Id;
        f.ProfilesService.Remove(toRemove);

        Assert.Equal(11, f.ProfilesService.Profiles.Count);
        var loaded = f.Store.LoadAll();
        Assert.Equal(11, loaded.Count);
        Assert.DoesNotContain(loaded, p => p.Id == toRemove);
    }

    [Fact]
    public void RollingBackup_CreatedBeforeModifyingExistingProfilesJson()
    {
        using var f = new TestFixture();
        var profiles = f.CreateProfiles(12);
        f.Store.SaveAll(profiles);

        // Initial save should not create a backup since there was no previous file
        Assert.False(Directory.Exists(f.BackupsDir) && Directory.GetFiles(f.BackupsDir, "profiles.*.json").Length > 0);

        // Modify metadata and save again
        profiles[0].Nickname = "Modified Nickname";
        f.Store.SaveAll(profiles);

        // A rolling backup of the previous 12-account index must now exist
        Assert.True(Directory.Exists(f.BackupsDir));
        var backups = Directory.GetFiles(f.BackupsDir, "profiles.*.json");
        Assert.Single(backups);

        var backupJson = File.ReadAllText(backups[0]);
        var backupList = JsonSerializer.Deserialize<List<ProfileMetadata>>(backupJson, CaseInsensitiveJsonOptions);
        Assert.NotNull(backupList);
        Assert.Equal(12, backupList.Count);
        Assert.Equal("Account 0", backupList[0].Nickname); // Old nickname preserved in backup
    }

    [Fact]
    public void CorruptIndex_FailsSafe_RecoversFromValidBackup()
    {
        using var f = new TestFixture();
        var profiles = f.CreateProfiles(12);
        f.Store.SaveAll(profiles);

        // Trigger backup creation by modifying
        profiles[1].Nickname = "Account 1 Updated";
        f.Store.SaveAll(profiles);

        // Simulate destructive corruption on profiles.json
        File.WriteAllText(f.ProfilesPath, "{ invalid json content truncated abruptly: [");

        // LoadAll must not crash or return empty list; it must restore from backup
        var loaded = f.Store.LoadAll();
        Assert.Equal(12, loaded.Count);

        // Disk must also have been atomically repaired
        var repairedJson = File.ReadAllText(f.ProfilesPath);
        var repairedList = JsonSerializer.Deserialize<List<ProfileMetadata>>(repairedJson, CaseInsensitiveJsonOptions);
        Assert.NotNull(repairedList);
        Assert.Equal(12, repairedList.Count);
    }

    [Fact]
    public void CorruptIndex_WithNoBackup_ThrowsCorruptProfileIndexException()
    {
        using var f = new TestFixture();
        // File exists but is corrupt and no backups exist
        Directory.CreateDirectory(Path.GetDirectoryName(f.ProfilesPath)!);
        File.WriteAllText(f.ProfilesPath, "corrupt non-json data");

        Assert.Throws<CorruptProfileIndexException>(() =>
        {
            f.Store.LoadAll();
        });
    }

    [Fact]
    public void MissingFile_ReturnsEmptyList_Legitimately()
    {
        using var f = new TestFixture();
        // Genuinely missing file (first launch)
        Assert.False(File.Exists(f.ProfilesPath));
        var loaded = f.Store.LoadAll();
        Assert.Empty(loaded);
    }

    [Fact]
    public async Task ExactIncidentRepro_UsageService_DoesNotTruncate12ProfilesTo1()
    {
        using var f = new TestFixture();
        var twelve = f.CreateProfiles(12);
        f.Store.SaveAll(twelve);

        // Put active credentials in active slot so Reconcile marks twelve[0] as active
        var activeBytes = f.Vault.LoadBlob(twelve[0].Id);
        f.Fs.CreateDirectory(Path.GetDirectoryName(f.Paths.ActiveAuthPath)!);
        f.Fs.WriteAllBytesAtomic(f.Paths.ActiveAuthPath, activeBytes);

        f.ProfilesService.Load();
        Assert.Equal(12, f.ProfilesService.Profiles.Count);

        var active = f.ProfilesService.Profiles.First(p => p.IsActive);
        var fakeProvider = new FakeCodexUsageProvider();

        // Simulate Plus -> Free plan change returned from live account/read
        var freeLimits = new RateLimitsSnapshot(
            ProfileId: active.Id,
            ObservedAt: f.Clock.UtcNow,
            PrimaryLimitId: "codex",
            Limits: [new("codex", null, [new("primary", 300, "5h", 10, 90, f.Clock.UtcNow.AddHours(5))], null, "free")],
            ResetCreditsAvailable: 0,
            PlanType: "free",
            AccountEmail: active.AccountEmail,
            Status: UsageStatus.Healthy);
        fakeProvider.SetResult(active.Id, UsageFetchResult.Ok(freeLimits));

        var cache = new Infra.Codex.Usage.UsageCache(f.Fs, f.TempDir.Combine("usage-cache.json"));
        var coordinator = new ProfileOperationCoordinator();

        using var usageService = new UsageService(
            provider: fakeProvider,
            cache: cache,
            vault: f.Vault,
            coordinator: coordinator,
            clock: f.Clock,
            options: new UsageServiceOptions { MaxConcurrentUsageProcesses = 1 },
            appLifetime: null,
            fs: f.Fs,
            codexPaths: f.Paths,
            profileStore: null,
            onProfilesPersistNeeded: () => f.ProfilesService.Save());

        // Execute refresh for the active profile
        var result = await usageService.RefreshAsync(active, force: true);
        Assert.Equal(UsageStatus.Healthy, result.Status);

        // Verify that in memory and on disk, all 12 profiles remain completely intact!
        Assert.Equal(12, f.ProfilesService.Profiles.Count);
        var loaded = f.Store.LoadAll();
        Assert.Equal(12, loaded.Count);

        // Verify active profile plan was synchronized to free
        var reloadedActive = loaded.First(p => p.Id == active.Id);
        Assert.Equal("free", reloadedActive.PlanType);
    }

    [Fact]
    public void OrphanVaultBlobs_AreNeverDeletedOnStartup_AndCanBeRecovered()
    {
        using var f = new TestFixture();
        var twelve = f.CreateProfiles(12);

        // Simulate incident state: 12 vault blobs exist, but profiles.json has only 1 surviving profile
        var surviving = twelve.Take(1).ToList();
        f.Store.SaveAll(surviving, ProfileSaveIntent.NormalUpdate);

        // Load profile service (startup)
        f.ProfilesService.Load();
        Assert.Single(f.ProfilesService.Profiles);

        // Invariant: all 12 vault blobs MUST remain intact in vault directory
        var blobsOnDisk = f.Vault.EnumerateBlobs();
        Assert.Equal(12, blobsOnDisk.Count);

        // 11 orphans detected
        var orphans = f.ProfilesService.DetectOrphanVaultBlobs();
        Assert.Equal(11, orphans.Count);

        // Exercise recovery of orphans
        var recoveredCount = f.ProfilesService.RecoverOrphanProfiles();
        Assert.Equal(11, recoveredCount);

        // All 12 profiles now exist with their original GUIDs
        Assert.Equal(12, f.ProfilesService.Profiles.Count);
        var recoveredIds = f.ProfilesService.Profiles.Select(p => p.Id).ToHashSet();
        foreach (var original in twelve)
        {
            Assert.Contains(original.Id, recoveredIds);
        }

        var diskLoaded = f.Store.LoadAll();
        Assert.Equal(12, diskLoaded.Count);
    }

    [Fact]
    public async Task ConcurrentWriters_SerializedAndConsistent()
    {
        using var f = new TestFixture();
        var twelve = f.CreateProfiles(12);
        f.Store.SaveAll(twelve);

        // Run 20 concurrent tasks updating profiles
        var tasks = Enumerable.Range(0, 20).Select(i => Task.Run(() =>
        {
            var list = f.Store.LoadAll();
            var target = list[i % list.Count];
            target.Nickname = $"Concurrently Updated {i}";
            f.Store.SaveAll(list, ProfileSaveIntent.NormalUpdate);
        }));

        await Task.WhenAll(tasks);

        var finalProfiles = f.Store.LoadAll();
        Assert.Equal(12, finalProfiles.Count);
        Assert.Equal(twelve.Select(p => p.Id).OrderBy(x => x), finalProfiles.Select(p => p.Id).OrderBy(x => x));
    }

    private sealed class FakeCodexUsageProvider : ICodexUsageProvider
    {
        private readonly Dictionary<Guid, UsageFetchResult> _results = new();

        public void SetResult(Guid profileId, UsageFetchResult result) => _results[profileId] = result;

        public Task<UsageFetchResult> FetchRateLimitsAsync(
            Guid profileId,
            byte[] authJsonBytes,
            System.Threading.CancellationToken cancellationToken = default)
        {
            return FetchRateLimitsAsync(profileId, authJsonBytes, null, cancellationToken);
        }

        public Task<UsageFetchResult> FetchRateLimitsAsync(
            Guid profileId,
            byte[] authJsonBytes,
            UsageFetchOptions? options,
            System.Threading.CancellationToken cancellationToken = default)
        {
            if (_results.TryGetValue(profileId, out var result))
                return Task.FromResult(result);

            return Task.FromResult(UsageFetchResult.Fail(
                UsageStatus.Error,
                ErrorInfo.Create(ErrorCategory.Unknown, "No mock configured", DateTimeOffset.UtcNow)));
        }
    }
}

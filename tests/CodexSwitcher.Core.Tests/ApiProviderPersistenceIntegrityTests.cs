using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodexSwitcher.Core.Common.Errors;
using CodexSwitcher.Core.Providers.Contracts;
using CodexSwitcher.Core.Providers.Models;
using CodexSwitcher.Core.Tests.TestSupport;
using CodexSwitcher.Infra.Common.Storage;
using CodexSwitcher.Infra.Providers.Secrets;
using CodexSwitcher.Infra.Providers.Storage;
using CodexSwitcher.Infra.Security.Dpapi;
using Xunit;

namespace CodexSwitcher.Core.Tests;

/// <summary>
/// Regression and safety invariant tests verifying API provider persistence protection
/// against silent destructive truncation, corrupt-index wiping, and orphan credential retention.
/// </summary>
public sealed class ApiProviderPersistenceIntegrityTests
{
    private sealed class TestFixture : IDisposable
    {
        public TempDir TempDir { get; } = new();
        public PhysicalFileSystem Fs { get; } = new();
        public string ApiProvidersPath => TempDir.Combine("api-providers.json");
        public string ApiKeysDir => TempDir.Combine("api-keys");
        public string BackupsDir => TempDir.Combine("backups", "api-providers");
        public ApiKeySecretStore SecretStore { get; }
        public ApiProviderStore Store { get; }

        public TestFixture()
        {
            SecretStore = new ApiKeySecretStore(new DpapiSecretProtector(), Fs, ApiKeysDir);
            Store = new ApiProviderStore(Fs, ApiProvidersPath, SecretStore);
        }

        public List<ApiProviderProfile> CreateProfiles(int count)
        {
            var list = new List<ApiProviderProfile>();
            for (int i = 0; i < count; i++)
            {
                var id = Guid.NewGuid();
                SecretStore.SaveApiKey(id, $"sk-test-key-{i}");
                list.Add(new ApiProviderProfile
                {
                    Id = id,
                    Nickname = $"Provider {i}",
                    CatalogProviderId = "router-cheap",
                    StableCodexProviderId = ApiProviderProfile.GenerateStableCodexProviderId(id),
                    BaseUrl = "https://router.cheap/v1",
                    SelectedRouteId = "primary",
                    SelectedModel = "gpt-5.6-sol",
                    KeyPreview = $"sk-...{i}",
                    Status = ApiProviderProfileStatus.Active,
                    SortOrder = i
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
        var three = f.CreateProfiles(3);
        f.Store.SaveAll(three, ApiProviderSaveIntent.NormalUpdate);

        var loaded = f.Store.GetAll();
        Assert.Equal(3, loaded.Count);

        // Attempt to save only 1 profile under NormalUpdate
        var onlyOne = new List<ApiProviderProfile> { three[0] };
        var ex = Assert.Throws<ApiProviderTruncationException>(() =>
        {
            f.Store.SaveAll(onlyOne, ApiProviderSaveIntent.NormalUpdate);
        });

        Assert.Equal(3, ex.ExistingCount);
        Assert.Equal(1, ex.AttemptedCount);

        // Verify disk was untouched and still contains all 3 profiles
        var reloaded = f.Store.GetAll();
        Assert.Equal(3, reloaded.Count);
        Assert.Equal(three.Select(p => p.Id), reloaded.Select(p => p.Id));
    }

    [Fact]
    public void ExplicitDelete_AllowsCountToDecrease()
    {
        using var f = new TestFixture();
        var three = f.CreateProfiles(3);
        f.Store.SaveAll(three, ApiProviderSaveIntent.NormalUpdate);

        // Explicit deletion of one profile
        var deleted = f.Store.Delete(three[1].Id);
        Assert.True(deleted);

        var loaded = f.Store.GetAll();
        Assert.Equal(2, loaded.Count);
        Assert.DoesNotContain(loaded, p => p.Id == three[1].Id);
    }

    [Fact]
    public void RollingBackups_CreatedOnWrite_AndPrunedToMax10()
    {
        using var f = new TestFixture();
        var profiles = f.CreateProfiles(2);
        f.Store.SaveAll(profiles, ApiProviderSaveIntent.NormalUpdate);

        // Perform 12 subsequent mutations to trigger backup creation and rotation
        for (int i = 0; i < 12; i++)
        {
            profiles[0].Nickname = $"Updated Name {i}";
            f.Store.SaveAll(profiles, ApiProviderSaveIntent.NormalUpdate);
        }

        Assert.True(Directory.Exists(f.BackupsDir));
        var backupFiles = Directory.GetFiles(f.BackupsDir, "api-providers.*.json");
        Assert.InRange(backupFiles.Length, 1, 10);
    }

    [Fact]
    public void CorruptIndex_RecoversFromKnownGoodBackup()
    {
        using var f = new TestFixture();
        var profiles = f.CreateProfiles(3);
        f.Store.SaveAll(profiles, ApiProviderSaveIntent.NormalUpdate);

        // Cause a backup to be created by saving an edit
        profiles[0].Nickname = "Modified Nickname";
        f.Store.SaveAll(profiles, ApiProviderSaveIntent.NormalUpdate);

        // Corrupt the primary api-providers.json file
        File.WriteAllText(f.ApiProvidersPath, "{ this is invalid corrupt json ::: ");

        // Re-read should recover from the rolling backup
        var loaded = f.Store.GetAll();
        Assert.Equal(3, loaded.Count);

        // Primary file should have been healed on disk
        var reloaded = f.Store.GetAll();
        Assert.Equal(3, reloaded.Count);
    }

    [Fact]
    public void CorruptIndex_WithoutBackups_ThrowsCorruptApiProviderIndexException()
    {
        using var f = new TestFixture();
        // Create an unparseable corrupt file with no prior backups
        File.WriteAllText(f.ApiProvidersPath, "{ invalid corrupt json }");

        Assert.Throws<CorruptApiProviderIndexException>(() =>
        {
            f.Store.GetAll();
        });
    }

    [Fact]
    public void OrphanSecretPreservation_DoesNotDeleteOrphanKeyFiles()
    {
        using var f = new TestFixture();
        var profiles = f.CreateProfiles(2);
        f.Store.SaveAll(profiles, ApiProviderSaveIntent.NormalUpdate);

        // Manually place an unreferenced orphan API key blob in the api-keys directory
        var orphanId = Guid.NewGuid();
        f.SecretStore.SaveApiKey(orphanId, "sk-orphan-key-do-not-delete");

        var orphanPath = Path.Combine(f.ApiKeysDir, $"{orphanId:N}.bin");
        Assert.True(File.Exists(orphanPath));

        // Perform store queries and saves
        f.Store.GetAll();
        profiles[0].Nickname = "New Name";
        f.Store.Save(profiles[0]);

        // Invariant: Orphan key file MUST NOT be deleted
        Assert.True(File.Exists(orphanPath));
        Assert.True(f.SecretStore.HasApiKey(orphanId));
    }
}

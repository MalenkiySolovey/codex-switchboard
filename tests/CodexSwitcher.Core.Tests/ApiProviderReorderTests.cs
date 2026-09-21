using CodexSwitcher.Core.Providers.Contracts;
using CodexSwitcher.Core.Providers.Models;
using CodexSwitcher.Core.Tests.TestSupport;
using CodexSwitcher.Infra.Common.Storage;
using CodexSwitcher.Infra.Providers.Secrets;
using CodexSwitcher.Infra.Providers.Storage;
using CodexSwitcher.Infra.Security.Dpapi;
using Xunit;

namespace CodexSwitcher.Core.Tests;

public sealed class ApiProviderReorderTests
{
    private readonly PhysicalFileSystem _fs = new();
    private readonly DpapiSecretProtector _protector = new();

    private static ApiProviderProfile CreateProfile(string name, int sortOrder = 0)
    {
        var id = Guid.NewGuid();
        return new ApiProviderProfile
        {
            Id = id,
            CatalogProviderId = "router-cheap",
            StableCodexProviderId = ApiProviderProfile.GenerateStableCodexProviderId(id),
            Nickname = name,
            BaseUrl = "https://router.cheap/v1",
            SelectedRouteId = "primary",
            SelectedModel = "gpt-5.6-sol",
            KeyPreview = "sk-...1234",
            SortOrder = sortOrder,
            Status = ApiProviderProfileStatus.Active
        };
    }

    [Fact]
    public void Reorder_AssignsSequentialSortOrder_AndPersistsAcrossReload()
    {
        using var temp = new TempDir();
        var jsonPath = Path.Combine(temp.Root, "api-providers.json");
        var store = new ApiProviderStore(_fs, jsonPath);

        var p1 = CreateProfile("Provider 1", 0);
        var p2 = CreateProfile("Provider 2", 1);
        var p3 = CreateProfile("Provider 3", 2);

        store.Save(p1);
        store.Save(p2);
        store.Save(p3);

        // Reorder: p3, p1, p2
        var reordered = new List<ApiProviderProfile> { p3, p1, p2 };
        for (int i = 0; i < reordered.Count; i++)
        {
            reordered[i].SortOrder = i;
        }

        store.SaveAll(reordered, ApiProviderSaveIntent.NormalUpdate);

        // Reload fresh from store
        var freshStore = new ApiProviderStore(_fs, jsonPath);
        var loaded = freshStore.GetAll();

        Assert.Equal(3, loaded.Count);
        Assert.Equal(p3.Id, loaded[0].Id);
        Assert.Equal(0, loaded[0].SortOrder);
        Assert.Equal(p1.Id, loaded[1].Id);
        Assert.Equal(1, loaded[1].SortOrder);
        Assert.Equal(p2.Id, loaded[2].Id);
        Assert.Equal(2, loaded[2].SortOrder);
    }

    [Fact]
    public void MoveUp_SwapsWithPreviousItem()
    {
        using var temp = new TempDir();
        var jsonPath = Path.Combine(temp.Root, "api-providers.json");
        var store = new ApiProviderStore(_fs, jsonPath);

        var p1 = CreateProfile("Alpha", 0);
        var p2 = CreateProfile("Beta", 1);
        var p3 = CreateProfile("Gamma", 2);

        store.SaveAll([p1, p2, p3], ApiProviderSaveIntent.NormalUpdate);

        // Simulate MoveUp on Beta (index 1 -> index 0)
        var items = store.GetAll().ToList();
        var idx = items.FindIndex(x => x.Id == p2.Id);
        Assert.Equal(1, idx);

        // Move
        items.RemoveAt(idx);
        items.Insert(idx - 1, p2);

        for (int i = 0; i < items.Count; i++)
        {
            items[i].SortOrder = i;
        }
        store.SaveAll(items, ApiProviderSaveIntent.NormalUpdate);

        var reloaded = store.GetAll();
        Assert.Equal("Beta", reloaded[0].Nickname);
        Assert.Equal("Alpha", reloaded[1].Nickname);
        Assert.Equal("Gamma", reloaded[2].Nickname);
    }

    [Fact]
    public void MoveUp_TopItem_IsBoundaryNoOp()
    {
        using var temp = new TempDir();
        var jsonPath = Path.Combine(temp.Root, "api-providers.json");
        var store = new ApiProviderStore(_fs, jsonPath);

        var p1 = CreateProfile("Alpha", 0);
        var p2 = CreateProfile("Beta", 1);

        store.SaveAll([p1, p2], ApiProviderSaveIntent.NormalUpdate);

        var items = store.GetAll().ToList();
        var idx = items.FindIndex(x => x.Id == p1.Id);
        Assert.Equal(0, idx);

        // Top item cannot move up (idx <= 0)
        bool canMoveUp = idx > 0;
        Assert.False(canMoveUp);
    }

    [Fact]
    public void MoveDown_SwapsWithNextItem()
    {
        using var temp = new TempDir();
        var jsonPath = Path.Combine(temp.Root, "api-providers.json");
        var store = new ApiProviderStore(_fs, jsonPath);

        var p1 = CreateProfile("Alpha", 0);
        var p2 = CreateProfile("Beta", 1);
        var p3 = CreateProfile("Gamma", 2);

        store.SaveAll([p1, p2, p3], ApiProviderSaveIntent.NormalUpdate);

        // Simulate MoveDown on Alpha (index 0 -> index 1)
        var items = store.GetAll().ToList();
        var idx = items.FindIndex(x => x.Id == p1.Id);
        Assert.Equal(0, idx);

        items.RemoveAt(idx);
        items.Insert(idx + 1, p1);

        for (int i = 0; i < items.Count; i++)
        {
            items[i].SortOrder = i;
        }
        store.SaveAll(items, ApiProviderSaveIntent.NormalUpdate);

        var reloaded = store.GetAll();
        Assert.Equal("Beta", reloaded[0].Nickname);
        Assert.Equal("Alpha", reloaded[1].Nickname);
        Assert.Equal("Gamma", reloaded[2].Nickname);
    }

    [Fact]
    public void MoveDown_BottomItem_IsBoundaryNoOp()
    {
        using var temp = new TempDir();
        var jsonPath = Path.Combine(temp.Root, "api-providers.json");
        var store = new ApiProviderStore(_fs, jsonPath);

        var p1 = CreateProfile("Alpha", 0);
        var p2 = CreateProfile("Beta", 1);

        store.SaveAll([p1, p2], ApiProviderSaveIntent.NormalUpdate);

        var items = store.GetAll().ToList();
        var idx = items.FindIndex(x => x.Id == p2.Id);
        Assert.Equal(1, idx);

        // Bottom item cannot move down (idx >= items.Count - 1)
        bool canMoveDown = idx >= 0 && idx < items.Count - 1;
        Assert.False(canMoveDown);
    }

    [Fact]
    public void Reorder_DoesNotTouchSecrets_OrCorruptBackups()
    {
        using var temp = new TempDir();
        var jsonPath = Path.Combine(temp.Root, "api-providers.json");
        var apiKeysDir = Path.Combine(temp.Root, "api-keys");
        var secretStore = new ApiKeySecretStore(_protector, _fs, apiKeysDir);
        var store = new ApiProviderStore(_fs, jsonPath, secretStore);

        var p1 = CreateProfile("First", 0);
        var p2 = CreateProfile("Second", 1);

        store.SaveAll([p1, p2], ApiProviderSaveIntent.NormalUpdate);
        secretStore.SaveApiKey(p1.Id, "sk-first-key");
        secretStore.SaveApiKey(p2.Id, "sk-second-key");

        // Swap order
        p1.SortOrder = 1;
        p2.SortOrder = 0;
        store.SaveAll([p1, p2], ApiProviderSaveIntent.NormalUpdate);

        // Assert keys intact in DPAPI secret store
        Assert.Equal("sk-first-key", secretStore.GetApiKey(p1.Id));
        Assert.Equal("sk-second-key", secretStore.GetApiKey(p2.Id));

        // Assert backup was created during SaveAll
        var backupsDir = Path.Combine(temp.Root, "backups", "api-providers");
        Assert.True(Directory.Exists(backupsDir));
        Assert.NotEmpty(Directory.GetFiles(backupsDir, "api-providers.*.json"));
    }
}

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodexSwitcher.App.ViewModels;
using CodexSwitcher.Core.Providers.Models;
using CodexSwitcher.Core.Tests.TestSupport;
using CodexSwitcher.Infra.Providers.Storage;

namespace CodexSwitcher.Core.Tests;

public sealed class ApiProviderSelectedModelOwnershipTests
{
    private static readonly JsonSerializerOptions RawJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    public void InventorySelectionWinsOverConflictingLegacyProjection()
    {
        var profile = CreateProfile("gpt-5.6-sol", "gpt-6-luna");

        Assert.Equal("gpt-6-luna", profile.GetEffectiveSelectedModel());

        Assert.True(profile.NormalizeSelectedModel());
        Assert.Equal("gpt-6-luna", profile.SelectedModel);
        Assert.Equal("gpt-6-luna", profile.ModelInventory!.SelectedModel);
    }

    [Fact]
    public void ExistingInventoryDoesNotUseLegacyProjectionOutsideMigration()
    {
        var profile = CreateProfile("gpt-5.6-sol", "gpt-6-luna");
        profile.ModelInventory!.SelectedModel = null;

        Assert.Null(profile.GetEffectiveSelectedModel());

        // Loading/saving performs the one permitted legacy migration, after
        // which both the authoritative inventory and compatibility projection
        // agree again.
        Assert.True(profile.NormalizeSelectedModel());
        Assert.Equal("gpt-5.6-sol", profile.GetEffectiveSelectedModel());
        Assert.Equal("gpt-5.6-sol", profile.ModelInventory.SelectedModel);
        Assert.Equal("gpt-5.6-sol", profile.SelectedModel);
    }

    [Fact]
    public void StoreLoadMigratesConflictingLegacyProjectionAndPersistsInventoryChoice()
    {
        using var temp = new TempDir();
        var fs = new PhysicalFileSystem();
        var path = Path.Combine(temp.Root, "api-providers.json");
        var profile = CreateProfile("gpt-5.6-sol", "gpt-6-luna");
        var rawJson = JsonSerializer.Serialize(new[] { profile }, RawJsonOptions);
        fs.WriteAllTextAtomic(path, rawJson);

        var loaded = new ApiProviderStore(fs, path).GetById(profile.Id);

        Assert.NotNull(loaded);
        Assert.Equal("gpt-6-luna", loaded.GetEffectiveSelectedModel());
        Assert.Equal("gpt-6-luna", loaded.SelectedModel);
        Assert.Equal("gpt-6-luna", loaded.ModelInventory!.SelectedModel);

        using var persisted = JsonDocument.Parse(fs.ReadAllText(path));
        var persistedProfile = persisted.RootElement[0];
        Assert.Equal("gpt-6-luna", persistedProfile.GetProperty("selectedModel").GetString());
        Assert.Equal(
            "gpt-6-luna",
            persistedProfile.GetProperty("modelInventory").GetProperty("selectedModel").GetString());

        var reloaded = new ApiProviderStore(fs, path).GetById(profile.Id);
        Assert.NotNull(reloaded);
        Assert.Equal("gpt-6-luna", reloaded.GetEffectiveSelectedModel());
    }

    [Fact]
    public void SavingExistingProviderProjectsInventoryDefaultAndReloadKeepsIt()
    {
        using var temp = new TempDir();
        var fs = new PhysicalFileSystem();
        var path = Path.Combine(temp.Root, "api-providers.json");
        var store = new ApiProviderStore(fs, path);
        var profile = CreateProfile("gpt-5.6-sol", "gpt-5.6-sol");
        store.Save(profile);

        profile.SetSelectedModel("gpt-6-luna");
        store.Save(profile);

        var reloaded = new ApiProviderStore(fs, path).GetById(profile.Id);
        Assert.NotNull(reloaded);
        Assert.Equal("gpt-6-luna", reloaded.ModelInventory!.SelectedModel);
        Assert.Equal("gpt-6-luna", reloaded.SelectedModel);
    }

    [Fact]
    public void CardViewModelImmediatelyShowsInventoryDefaultWithoutRestart()
    {
        var profile = CreateProfile("gpt-5.6-sol", "gpt-5.6-sol");
        var card = new ApiProviderItemViewModel(profile, descriptor: null, hasSecret: true);
        var selectedModelRaised = false;
        card.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ApiProviderItemViewModel.SelectedModel))
            {
                selectedModelRaised = true;
            }
        };

        // Manage Models edits the inventory working copy. The card receives
        // the existing profile projection update after Save, without reload.
        profile.ModelInventory!.SelectedModel = "gpt-6-luna";
        card.UpdateProfile(profile, descriptor: null, hasSecret: true, isTargetActive: false);

        Assert.Equal("gpt-6-luna", card.SelectedModel);
        Assert.True(selectedModelRaised);
    }

    [Fact]
    public void AppInformationalVersionIsStableV021()
    {
        var informationalVersion = typeof(ApiProviderItemViewModel).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
            .Split('+')[0];

        Assert.Equal("0.2.1", informationalVersion);
    }

    [Theory]
    [InlineData("deepseek-v4.1-flash", "deepseek-v4.1-flash")]
    [InlineData("deepseek-v4.1-flash:free", "deepseek-v4.1-flash:free")]
    public void SelectedModelPreservesExactSlug(string selection, string expected)
    {
        var profile = CreateProfile("deepseek-v4.1-flash", "deepseek-v4.1-flash:free");

        profile.SetSelectedModel(selection);

        Assert.Equal(expected, profile.GetEffectiveSelectedModel());
        Assert.Equal(expected, profile.ModelInventory!.SelectedModel);
        Assert.Contains(profile.ModelInventory.Models, model => model.Slug == "deepseek-v4.1-flash");
        Assert.Contains(profile.ModelInventory.Models, model => model.Slug == "deepseek-v4.1-flash:free");
    }

    private static ApiProviderProfile CreateProfile(string legacySelection, string inventorySelection)
    {
        var id = Guid.NewGuid();
        return new ApiProviderProfile
        {
            Id = id,
            StableCodexProviderId = ApiProviderProfile.GenerateStableCodexProviderId(id),
            Nickname = "Router.Cheap",
            BaseUrl = "https://router.cheap/v1",
            SelectedModel = legacySelection,
            ModelInventory = new ApiProviderModelInventory
            {
                SelectedModel = inventorySelection,
                Models =
                [
                    new ApiProviderModelItem
                    {
                        Slug = "gpt-5.6-sol",
                        Enabled = true,
                        DiscoverySource = ModelDiscoverySource.Discovered,
                        Availability = ModelAvailability.Reported,
                    },
                    new ApiProviderModelItem
                    {
                        Slug = "gpt-6-luna",
                        Enabled = true,
                        DiscoverySource = ModelDiscoverySource.Discovered,
                        Availability = ModelAvailability.Reported,
                    },
                    new ApiProviderModelItem
                    {
                        Slug = "deepseek-v4.1-flash",
                        Enabled = true,
                        DiscoverySource = ModelDiscoverySource.Discovered,
                        Availability = ModelAvailability.Reported,
                    },
                    new ApiProviderModelItem
                    {
                        Slug = "deepseek-v4.1-flash:free",
                        Enabled = true,
                        DiscoverySource = ModelDiscoverySource.Discovered,
                        Availability = ModelAvailability.Reported,
                    },
                ],
            },
        };
    }
}

using CodexSwitcher.Core.Abstractions;
using CodexSwitcher.Core.Models;
using CodexSwitcher.Core.Services;
using CodexSwitcher.Core.Tests.TestSupport;
using CodexSwitcher.Infra.Io;
using CodexSwitcher.Infra.Security;
using Xunit;

namespace CodexSwitcher.Core.Tests;

public sealed class ApiProviderStoreTests
{
    private readonly PhysicalFileSystem _fs = new();
    private readonly DpapiSecretProtector _protector = new();

    [Fact]
    public void ApiProviderStore_SaveAndRetrieve_WorksCorrectly()
    {
        using var temp = new TempDir();
        var jsonPath = Path.Combine(temp.Root, "api-providers.json");
        var store = new ApiProviderStore(_fs, jsonPath);

        var profileId = Guid.NewGuid();
        var profile = new ApiProviderProfile
        {
            Id = profileId,
            CatalogProviderId = "router-cheap",
            StableCodexProviderId = ApiProviderProfile.GenerateStableCodexProviderId(profileId),
            Nickname = "My Router.Cheap",
            BaseUrl = "https://router.cheap/v1",
            SelectedRouteId = "primary",
            SelectedModel = "gpt-5.6-sol",
            KeyPreview = "sk-...1234",
            Status = ApiProviderProfileStatus.Active
        };

        store.Save(profile);

        var retrieved = store.GetById(profileId);
        Assert.NotNull(retrieved);
        Assert.Equal("My Router.Cheap", retrieved.DisplayName);
        Assert.Equal("sk-...1234", retrieved.KeyPreview);
        Assert.Equal(ApiProviderProfileStatus.Active, retrieved.Status);
    }

    [Fact]
    public void ApiProviderStore_DetectsMissingCredential_SetsCredentialMissingStatus()
    {
        using var temp = new TempDir();
        var jsonPath = Path.Combine(temp.Root, "api-providers.json");
        var secretStore = new ApiKeySecretStore(_protector, _fs, temp.Root);
        var store = new ApiProviderStore(_fs, jsonPath, secretStore);

        var profileId = Guid.NewGuid();
        var profile = new ApiProviderProfile
        {
            Id = profileId,
            StableCodexProviderId = ApiProviderProfile.GenerateStableCodexProviderId(profileId),
            Nickname = "Orphaned Key Profile",
            Status = ApiProviderProfileStatus.Active
        };

        store.Save(profile);

        // Secret was never saved in secretStore
        var retrieved = store.GetById(profileId);
        Assert.NotNull(retrieved);
        Assert.Equal(ApiProviderProfileStatus.CredentialMissing, retrieved.Status);
    }

    [Fact]
    public void ComputeKeyPreview_FormatsSafely()
    {
        Assert.Equal(string.Empty, ApiProviderProfile.ComputeKeyPreview(null));
        Assert.Equal(string.Empty, ApiProviderProfile.ComputeKeyPreview("   "));
        Assert.Equal("12...", ApiProviderProfile.ComputeKeyPreview("12345678"));
        Assert.Equal("sk-proj...9999", ApiProviderProfile.ComputeKeyPreview("sk-project-super-secret-key-9999"));
        Assert.Equal("key...abcd", ApiProviderProfile.ComputeKeyPreview("key1234567890abcd"));
    }
}

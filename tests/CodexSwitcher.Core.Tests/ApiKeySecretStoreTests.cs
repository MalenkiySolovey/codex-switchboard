using CodexSwitcher.Core.Services;
using CodexSwitcher.Core.Tests.TestSupport;
using CodexSwitcher.Infra.Io;
using CodexSwitcher.Infra.Security;
using Xunit;

namespace CodexSwitcher.Core.Tests;

public sealed class ApiKeySecretStoreTests
{
    private readonly PhysicalFileSystem _fs = new();
    private readonly DpapiSecretProtector _protector = new();

    [Fact]
    public void SaveAndGetApiKey_RoundTrip_ReturnsOriginalKey()
    {
        using var temp = new TempDir();
        var store = new ApiKeySecretStore(_protector, _fs, temp.Root);
        var profileId = Guid.NewGuid();
        var rawKey = "sk-test-secret-api-key-1234567890abcdef";

        Assert.False(store.HasApiKey(profileId));

        store.SaveApiKey(profileId, rawKey);

        Assert.True(store.HasApiKey(profileId));
        var retrieved = store.GetApiKey(profileId);
        Assert.Equal(rawKey, retrieved);
    }

    [Fact]
    public void StoredBlob_IsEncryptedAndDoesNotContainPlaintext()
    {
        using var temp = new TempDir();
        var store = new ApiKeySecretStore(_protector, _fs, temp.Root);
        var profileId = Guid.NewGuid();
        var rawKey = "sk-live-ultra-sensitive-key-never-plain";

        store.SaveApiKey(profileId, rawKey);

        var keyFilePath = store.KeyPath(profileId);
        Assert.True(File.Exists(keyFilePath));

        var rawDiskBytes = File.ReadAllBytes(keyFilePath);
        var rawDiskString = System.Text.Encoding.UTF8.GetString(rawDiskBytes);

        // Plaintext key must NOT appear anywhere in the raw encrypted binary file
        Assert.DoesNotContain(rawKey, rawDiskString);
    }

    [Fact]
    public void DeleteApiKey_RemovesFile_HasApiKeyReturnsFalse()
    {
        using var temp = new TempDir();
        var store = new ApiKeySecretStore(_protector, _fs, temp.Root);
        var profileId = Guid.NewGuid();

        store.SaveApiKey(profileId, "sk-test-to-delete");
        Assert.True(store.HasApiKey(profileId));

        var deleted = store.DeleteApiKey(profileId);
        Assert.True(deleted);
        Assert.False(store.HasApiKey(profileId));
        Assert.Null(store.GetApiKey(profileId));
    }

    [Fact]
    public void MultipleProfiles_AreIsolatedFromEachOther()
    {
        using var temp = new TempDir();
        var store = new ApiKeySecretStore(_protector, _fs, temp.Root);

        var profile1 = Guid.NewGuid();
        var profile2 = Guid.NewGuid();

        store.SaveApiKey(profile1, "sk-key-profile-1");
        store.SaveApiKey(profile2, "sk-key-profile-2");

        Assert.Equal("sk-key-profile-1", store.GetApiKey(profile1));
        Assert.Equal("sk-key-profile-2", store.GetApiKey(profile2));

        store.DeleteApiKey(profile1);
        Assert.Null(store.GetApiKey(profile1));
        Assert.Equal("sk-key-profile-2", store.GetApiKey(profile2));
    }

    [Fact]
    public void SaveApiKey_EmptyOrWhitespace_ThrowsArgumentException()
    {
        using var temp = new TempDir();
        var store = new ApiKeySecretStore(_protector, _fs, temp.Root);

        Assert.Throws<ArgumentException>(() => store.SaveApiKey(Guid.NewGuid(), "   "));
        Assert.Throws<ArgumentException>(() => store.SaveApiKey(Guid.NewGuid(), ""));
    }
}

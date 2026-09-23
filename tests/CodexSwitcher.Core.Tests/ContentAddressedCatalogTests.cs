using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using CodexSwitcher.Core.Providers.Models;
using CodexSwitcher.Core.Routing.Models;
using CodexSwitcher.Core.Tests.TestSupport;
using CodexSwitcher.Infra.Codex.Runtime;
using CodexSwitcher.Infra.Common.Paths;
using CodexSwitcher.Infra.Common.Storage;
using Xunit;

namespace CodexSwitcher.Core.Tests;

public sealed class ContentAddressedCatalogTests
{
    private readonly PhysicalFileSystem _fs = new();

    private static ApiProviderProfile CreateTestProfile(long contextWindow = 500_000)
    {
        var profile = new ApiProviderProfile
        {
            Id = Guid.NewGuid(),
            Nickname = "Test Grok Profile",
            BaseUrl = "https://api.testprovider.com/v1",
            SelectedModel = "grok-4.6",
            ModelInventory = new ApiProviderModelInventory
            {
                Models = [
                    new ApiProviderModelItem
                    {
                        Slug = "grok-4.6",
                        DisplayName = "Grok 4.6",
                        Enabled = true,
                        ContextWindow = contextWindow,
                        DiscoverySource = ModelDiscoverySource.Discovered,
                        Availability = ModelAvailability.Reported,
                        LastSeenAt = DateTimeOffset.UtcNow
                    }
                ]
            }
        };
        return profile;
    }

    [Fact]
    public void CatalogSerialization_IsDeterministicAcrossCalls()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Root);
        var service = new CodexModelCatalogService(_fs, paths);

        var profile = CreateTestProfile();
        var path1 = service.EnsureProfileModelCatalog(profile);
        var bytes1 = File.ReadAllBytes(path1!);

        // Second generation in fresh directory
        using var temp2 = new TempDir();
        var paths2 = new AppPaths(temp2.Root);
        var service2 = new CodexModelCatalogService(_fs, paths2);

        var path2 = service2.EnsureProfileModelCatalog(profile);
        var bytes2 = File.ReadAllBytes(path2!);

        Assert.Equal(bytes1, bytes2);
        Assert.Equal(
            CodexModelCatalogService.ComputeCatalogContentHash(File.ReadAllText(path1!)),
            CodexModelCatalogService.ComputeCatalogContentHash(File.ReadAllText(path2!)));
    }

    [Fact]
    public void ContextWindowChange_ProducesNewContentHashAndNewDirectory()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Root);
        var service = new CodexModelCatalogService(_fs, paths);

        var profile = CreateTestProfile(350_000);
        var path1 = service.EnsureProfileModelCatalog(profile);

        // Modify context window tokens (both > 272k ceiling and <= 500k max documented)
        profile.ModelInventory!.Models[0].ContextWindow = 500_000;
        var path2 = service.EnsureProfileModelCatalog(profile);

        Assert.NotNull(path1);
        Assert.NotNull(path2);
        Assert.NotEqual(path1, path2);

        var hash1 = Path.GetFileName(Path.GetDirectoryName(path1));
        var hash2 = Path.GetFileName(Path.GetDirectoryName(path2));
        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void ToolCapabilityChange_ProducesNewContentHashAndNewDirectory()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Root);
        var service = new CodexModelCatalogService(_fs, paths);

        var profile = CreateTestProfile();
        var defaultPolicy = EffectiveToolPolicy.ForOpenAiNative();
        var restrictedPolicy = new EffectiveToolPolicy(
            AllowStandardFunctionTools: true,
            AllowCustomFreeformApplyPatch: false,
            AllowToolSearch: false,
            AllowHostedWebSearch: false,
            AllowStandaloneWebSearch: false,
            AllowNamespaceTools: false,
            AllowMultiAgent: false);

        var path1 = service.EnsureProfileModelCatalog(profile, toolPolicy: defaultPolicy);
        var path2 = service.EnsureProfileModelCatalog(profile, toolPolicy: restrictedPolicy);

        Assert.NotNull(path1);
        Assert.NotNull(path2);
        Assert.NotEqual(path1, path2);

        var hash1 = Path.GetFileName(Path.GetDirectoryName(path1));
        var hash2 = Path.GetFileName(Path.GetDirectoryName(path2));
        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void EnabledModelSetChange_ProducesNewContentHashAndNewDirectory()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Root);
        var service = new CodexModelCatalogService(_fs, paths);

        var profile = CreateTestProfile();
        var path1 = service.EnsureProfileModelCatalog(profile);

        // Add a second enabled model
        profile.ModelInventory!.Models.Add(new ApiProviderModelItem
        {
            Slug = "claude-3-5-sonnet",
            DisplayName = "Claude 3.5 Sonnet",
            Enabled = true,
            ContextWindow = 200_000
        });

        var path2 = service.EnsureProfileModelCatalog(profile);

        Assert.NotNull(path1);
        Assert.NotNull(path2);
        Assert.NotEqual(path1, path2);

        // Disable the second model
        profile.ModelInventory.Models[1].Enabled = false;
        var path3 = service.EnsureProfileModelCatalog(profile);

        // Disabling it should revert the content hash to match path1
        Assert.Equal(path1, path3);
    }

    [Fact]
    public void NonEmittedMetadataChange_DoesNotChangeContentHashOrPath()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Root);
        var service = new CodexModelCatalogService(_fs, paths);

        var profile = CreateTestProfile();
        var path1 = service.EnsureProfileModelCatalog(profile);

        // Mutate non-emitted metadata: LastSeenAt, DiscoveryStatus
        profile.ModelInventory!.Models[0].LastSeenAt = DateTimeOffset.UtcNow.AddHours(5);
        profile.ModelInventory.LastDiscoveryAt = DateTimeOffset.UtcNow.AddHours(5);
        profile.ModelInventory.DiscoveryStatus = ModelDiscoveryStatus.Discovered;

        var path2 = service.EnsureProfileModelCatalog(profile);

        Assert.Equal(path1, path2);
    }

    [Fact]
    public void ExistingFileWithMatchingContent_IsReusedWithoutError()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Root);
        var service = new CodexModelCatalogService(_fs, paths);

        var profile = CreateTestProfile();
        var path1 = service.EnsureProfileModelCatalog(profile);
        Assert.NotNull(path1);
        Assert.True(File.Exists(path1));

        // Call again on new service instance with empty memory cache
        var service2 = new CodexModelCatalogService(_fs, paths);
        var path2 = service2.EnsureProfileModelCatalog(profile);

        Assert.Equal(path1, path2);
        Assert.True(File.Exists(path2));
    }

    [Fact]
    public void ExistingFileWithMismatchedContent_ThrowsInvalidOperationExceptionCorruptionGuard()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Root);
        var service = new CodexModelCatalogService(_fs, paths);

        var profile = CreateTestProfile();
        var path1 = service.EnsureProfileModelCatalog(profile);
        Assert.NotNull(path1);
        Assert.True(File.Exists(path1));

        // Corrupt or overwrite the file at that content-addressed path
        File.WriteAllText(path1, "{\"models\": [{\"slug\": \"corrupted-model\"}]}");

        // Create new service instance with clear memory cache
        var service2 = new CodexModelCatalogService(_fs, paths);
        var ex = Assert.Throws<InvalidOperationException>(() => service2.EnsureProfileModelCatalog(profile));

        Assert.Contains("Catalog content collision or corruption detected", ex.Message);
    }
}

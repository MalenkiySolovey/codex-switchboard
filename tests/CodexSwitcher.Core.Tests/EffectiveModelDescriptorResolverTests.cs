using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using CodexSwitcher.Core.Providers.Contracts;
using CodexSwitcher.Core.Providers.Models;
using CodexSwitcher.Core.Providers.Services;
using CodexSwitcher.Core.Routing.Models;
using CodexSwitcher.Core.Tests.TestSupport;
using CodexSwitcher.Infra.Codex.Runtime;
using CodexSwitcher.Infra.Common.Paths;
using CodexSwitcher.Infra.Common.Storage;
using Xunit;

namespace CodexSwitcher.Core.Tests;

public sealed class EffectiveModelDescriptorResolverTests
{
    private readonly PhysicalFileSystem _fs = new();

    [Fact]
    public void ResolveDescriptor_ModelflareGrok46_OmitsUnsupportedTools()
    {
        var resolver = new EffectiveModelDescriptorResolver();
        var profile = new ApiProviderProfile
        {
            Id = Guid.NewGuid(),
            Nickname = "Modelflare Grok",
            BaseUrl = "https://modelflare.dev/v1",
            SelectedModel = "grok-4.6"
        };
        var modelItem = new ApiProviderModelItem
        {
            Slug = "grok-4.6",
            DisplayName = "Grok 4.6",
            Enabled = true
        };

        var descriptor = resolver.ResolveDescriptor(modelItem, profile, isSelectedModel: true);

        Assert.False(descriptor.AllowFreeformApplyPatch, "Modelflare Grok must not allow freeform apply_patch.");
        Assert.False(descriptor.AllowToolSearch, "Modelflare Grok must not allow tool_search.");
        Assert.False(descriptor.AllowHostedWebSearch, "Modelflare Grok must not allow hosted web search.");

        var catalogEntry = resolver.BuildCatalogEntry(descriptor, isLegacyRuntime: false);

        Assert.False(catalogEntry.ContainsKey("apply_patch_tool_type"), "Catalog entry must NOT advertise apply_patch_tool_type: freeform.");
        Assert.Equal(false, catalogEntry["supports_search_tool"]);
        Assert.Equal("none", catalogEntry["web_search_tool_type"]);
    }

    [Fact]
    public void ResolveDescriptor_GenericResponses_PermitsStandardTools()
    {
        var resolver = new EffectiveModelDescriptorResolver();
        var profile = new ApiProviderProfile
        {
            Id = Guid.NewGuid(),
            Nickname = "Generic Responses",
            BaseUrl = "https://api.openai.com/v1",
            SelectedModel = "gpt-5.6-sol"
        };
        var modelItem = new ApiProviderModelItem
        {
            Slug = "gpt-5.6-sol",
            DisplayName = "GPT 5.6 Sol",
            Enabled = true
        };

        var descriptor = resolver.ResolveDescriptor(modelItem, profile, isSelectedModel: true);

        Assert.True(descriptor.AllowFreeformApplyPatch);
        Assert.True(descriptor.AllowToolSearch);
        Assert.True(descriptor.AllowHostedWebSearch);

        var catalogEntry = resolver.BuildCatalogEntry(descriptor, isLegacyRuntime: false);

        Assert.Equal("freeform", catalogEntry["apply_patch_tool_type"]);
        Assert.Equal(true, catalogEntry["supports_search_tool"]);
        Assert.Equal("text_and_image", catalogEntry["web_search_tool_type"]);
    }

    [Fact]
    public void ResolveDescriptor_UserOverrides_TakePrecedence()
    {
        var resolver = new EffectiveModelDescriptorResolver();
        var profile = new ApiProviderProfile
        {
            Id = Guid.NewGuid(),
            Nickname = "Custom Profile",
            BaseUrl = "https://api.example.com/v1",
            SelectedModel = "custom-model"
        };
        var modelItem = new ApiProviderModelItem
        {
            Slug = "custom-model",
            ContextWindow = 100_000,
            UserOverrides = new CodexModelOverrides
            {
                ContextWindowTokens = 400_000,
                ReasoningEffort = CodexReasoningEffort.High,
                Verbosity = CodexVerbosity.High
            }
        };

        var descriptor = resolver.ResolveDescriptor(modelItem, profile, isSelectedModel: true);

        Assert.Equal(400_000, descriptor.ContextWindow);
        Assert.Equal(CodexReasoningEffort.High, descriptor.ReasoningEffort);
        Assert.Equal(CodexVerbosity.High, descriptor.Verbosity);

        var catalogEntry = resolver.BuildCatalogEntry(descriptor, isLegacyRuntime: false);
        Assert.Equal(400_000L, catalogEntry["context_window"]);
        Assert.Equal("high", catalogEntry["default_reasoning_level"]);
        Assert.Equal("high", catalogEntry["default_verbosity"]);
        Assert.Equal(true, catalogEntry["support_verbosity"]);
    }

    [Fact]
    public void ApiProviderModelInventory_EnsureSelectedModelMigrated_CreatesEnabledItem()
    {
        var inventory = new ApiProviderModelInventory();
        Assert.Empty(inventory.Models);

        inventory.EnsureSelectedModelMigrated("grok-4.6", "Grok 4.6", 500_000);

        Assert.Single(inventory.Models);
        var item = inventory.Models[0];
        Assert.Equal("grok-4.6", item.Slug);
        Assert.Equal("Grok 4.6", item.DisplayName);
        Assert.True(item.Enabled);
        Assert.Equal(500_000, item.ContextWindow);
        Assert.Equal(ModelDiscoverySource.Manual, item.DiscoverySource);
    }

    [Fact]
    public void ApiProviderModelInventory_EnsureSelectedModelMigrated_RestoresProfileDefaultWhenInventoryValueIsBlank()
    {
        var inventory = new ApiProviderModelInventory
        {
            SelectedModel = "",
            Models =
            [
                new ApiProviderModelItem
                {
                    Slug = "deepseek-v4.1-flash:free",
                    DisplayName = "DeepSeek Free",
                    Enabled = true,
                    DiscoverySource = ModelDiscoverySource.Discovered,
                }
            ],
        };

        inventory.EnsureSelectedModelMigrated("deepseek-v4.1-flash:free");

        Assert.Equal("deepseek-v4.1-flash:free", inventory.SelectedModel);
        Assert.Single(inventory.Models);
        Assert.True(inventory.Models[0].Enabled);
    }

    [Fact]
    public void ApiProviderModelInventory_ComputeInventoryHash_DeterministicAndDifferentiates()
    {
        var inv1 = new ApiProviderModelInventory();
        inv1.EnsureSelectedModelMigrated("grok-4.6", "Grok 4.6", 500_000);

        var inv2 = new ApiProviderModelInventory();
        inv2.EnsureSelectedModelMigrated("grok-4.6", "Grok 4.6", 500_000);

        var inv3 = new ApiProviderModelInventory();
        inv3.EnsureSelectedModelMigrated("grok-4.6", "Grok 4.6", 300_000);

        var hash1 = inv1.ComputeInventoryHash();
        var hash2 = inv2.ComputeInventoryHash();
        var hash3 = inv3.ComputeInventoryHash();

        Assert.Equal(16, hash1.Length);
        Assert.Equal(hash1, hash2);
        Assert.NotEqual(hash1, hash3);
    }

    [Fact]
    public void CodexModelCatalogService_EnsureProfileModelCatalog_CreatesImmutableHashDirectory()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Root);
        var service = new CodexModelCatalogService(_fs, paths);

        var profile = new ApiProviderProfile
        {
            Id = Guid.NewGuid(),
            Nickname = "Modelflare Grok",
            BaseUrl = "https://modelflare.dev/v1",
            SelectedModel = "grok-4.6",
            ModelInventory = new ApiProviderModelInventory()
        };
        profile.ModelInventory.EnsureSelectedModelMigrated("grok-4.6", "Grok 4.6", 500_000);

        var hash = profile.ModelInventory.ComputeInventoryHash();
        var catalogPath = service.EnsureProfileModelCatalog(profile);

        Assert.NotNull(catalogPath);
        Assert.True(File.Exists(catalogPath));

        // Immutable content-addressed path invariant: %LOCALAPPDATA%\CodexSwitchboard\catalogs\<profile-id>\<runtime-fp>\<content-fingerprint>\models.json
        var json = File.ReadAllText(catalogPath);
        var contentHash = CodexModelCatalogService.ComputeCatalogContentHash(json);
        Assert.Contains(profile.Id.ToString("D"), catalogPath);
        Assert.Contains(contentHash, catalogPath);
        Assert.EndsWith("models.json", catalogPath);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("models", out var modelsProp));
        Assert.Equal(1, modelsProp.GetArrayLength());

        var model = modelsProp[0];
        Assert.Equal("grok-4.6", model.GetProperty("slug").GetString());
        Assert.Equal(500_000L, model.GetProperty("context_window").GetInt64());
        Assert.False(model.TryGetProperty("apply_patch_tool_type", out _), "Modelflare Grok must not contain apply_patch_tool_type: freeform.");
    }
}

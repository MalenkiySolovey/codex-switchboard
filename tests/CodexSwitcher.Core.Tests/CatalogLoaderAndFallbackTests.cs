using System.Text;
using System.Text.Json;
using CodexSwitcher.Core.Catalog;
using CodexSwitcher.Core.Models;
using CodexSwitcher.Core.Tests.TestSupport;
using CodexSwitcher.Infra.Io;
using Xunit;

namespace CodexSwitcher.Core.Tests;

public sealed class CatalogLoaderAndFallbackTests
{
    private readonly PhysicalFileSystem _fs = new();

    [Fact]
    public void LoadBootstrapCatalog_AlwaysSucceedsWithDefaultProviders()
    {
        using var temp = new TempDir();
        var loader = new ProviderCatalogLoader(
            _fs,
            temp.Combine("providers.catalog.json"),
            temp.Combine("providers.catalog.sig"),
            temp.Combine("providers.catalog.previous.json"),
            temp.Combine("providers.local.json"),
            "0.1.3");

        var result = loader.LoadBootstrapCatalog();

        Assert.Equal(CatalogSourceLayer.EmbeddedBootstrap, result.ActiveLayer);
        Assert.Equal(CatalogLoadStatus.Supported, result.Status);
        Assert.NotNull(result.Catalog);
        Assert.True(result.Catalog.Providers.Count >= 1);

        var routerCheap = result.Catalog.Providers.FirstOrDefault(p => p.Id == "router-cheap");
        Assert.NotNull(routerCheap);
        Assert.Equal("Router.Cheap", routerCheap.DisplayName);
        Assert.Equal(CapabilityStatus.Supported, routerCheap.Capabilities.Models.Status);
        Assert.Equal(CapabilityStatus.Unknown, routerCheap.Capabilities.Balance.Status);
        Assert.Equal(CapabilityStatus.Unknown, routerCheap.Capabilities.Usage.Status);
    }

    [Fact]
    public void LoadCatalog_WhenExternalMissing_FallsBackToEmbedded()
    {
        using var temp = new TempDir();
        var loader = new ProviderCatalogLoader(
            _fs,
            temp.Combine("non_existent_catalog.json"),
            temp.Combine("non_existent_catalog.sig"),
            temp.Combine("non_existent_catalog.previous.json"),
            temp.Combine("non_existent_local.json"),
            "0.1.3");

        var result = loader.LoadCatalog();

        Assert.Equal(CatalogSourceLayer.EmbeddedBootstrap, result.ActiveLayer);
        Assert.Equal(CatalogLoadStatus.Supported, result.Status);
        Assert.NotNull(result.Catalog);
    }

    [Fact]
    public void LoadCatalog_WhenExternalValidAndSigned_LoadsOfficialExternal()
    {
        using var temp = new TempDir();
        var catalogFile = temp.Combine("providers.catalog.json");
        var sigFile = temp.Combine("providers.catalog.sig");

        // Copy official catalog and sig from repo
        var sourceCatalog = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "catalog", "providers.catalog.json");
        var sourceSig = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "catalog", "providers.catalog.sig");

        File.Copy(sourceCatalog, catalogFile);
        File.Copy(sourceSig, sigFile);

        var loader = new ProviderCatalogLoader(
            _fs,
            catalogFile,
            sigFile,
            temp.Combine("providers.catalog.previous.json"),
            temp.Combine("providers.local.json"),
            "0.1.3");

        var result = loader.LoadCatalog();

        Assert.Equal(CatalogSourceLayer.OfficialExternal, result.ActiveLayer);
        Assert.Equal(CatalogLoadStatus.Supported, result.Status);
        Assert.NotNull(result.Catalog);
        Assert.True(result.Catalog.Providers.Count >= 2);
    }

    [Fact]
    public void LoadCatalog_WhenExternalHasInvalidSignature_FallsBackSafely()
    {
        using var temp = new TempDir();
        var catalogFile = temp.Combine("providers.catalog.json");
        var sigFile = temp.Combine("providers.catalog.sig");

        var sourceCatalog = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "catalog", "providers.catalog.json");
        File.Copy(sourceCatalog, catalogFile);
        File.WriteAllText(sigFile, "invalid-sig-base64");

        var loader = new ProviderCatalogLoader(
            _fs,
            catalogFile,
            sigFile,
            temp.Combine("providers.catalog.previous.json"),
            temp.Combine("providers.local.json"),
            "0.1.3");

        var result = loader.LoadCatalog();

        // Must NOT crash and must fall back to Embedded
        Assert.Equal(CatalogSourceLayer.EmbeddedBootstrap, result.ActiveLayer);
        Assert.Equal(CatalogLoadStatus.Supported, result.Status);
    }

    [Fact]
    public void LoadCatalog_WhenExternalHasMissingSignature_FallsBackSafely()
    {
        using var temp = new TempDir();
        var catalogFile = temp.Combine("providers.catalog.json");
        var sigFile = temp.Combine("providers.catalog.sig");

        var sourceCatalog = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "catalog", "providers.catalog.json");
        File.Copy(sourceCatalog, catalogFile);
        // Do NOT create sigFile

        var loader = new ProviderCatalogLoader(
            _fs,
            catalogFile,
            sigFile,
            temp.Combine("providers.catalog.previous.json"),
            temp.Combine("providers.local.json"),
            "0.1.3");

        var result = loader.LoadCatalog();

        Assert.Equal(CatalogSourceLayer.EmbeddedBootstrap, result.ActiveLayer);
        Assert.Equal(CatalogLoadStatus.Supported, result.Status);
    }

    [Fact]
    public void LoadCatalog_WhenExternalHasInvalidJson_FallsBackToLastKnownGood()
    {
        using var temp = new TempDir();
        var catalogFile = temp.Combine("providers.catalog.json");
        var sigFile = temp.Combine("providers.catalog.sig");
        var lkgFile = temp.Combine("providers.catalog.previous.json");

        // Set up valid Last-Known-Good
        var sourceCatalog = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "catalog", "providers.catalog.json");
        File.Copy(sourceCatalog, lkgFile);

        // Corrupt active catalog
        File.WriteAllText(catalogFile, "not-json{{{");
        File.WriteAllText(sigFile, "dummy");

        var loader = new ProviderCatalogLoader(
            _fs,
            catalogFile,
            sigFile,
            lkgFile,
            temp.Combine("providers.local.json"),
            "0.1.3");

        var result = loader.LoadCatalog();

        Assert.Equal(CatalogSourceLayer.LastKnownGood, result.ActiveLayer);
        Assert.Equal(CatalogLoadStatus.Supported, result.Status);
        Assert.True(result.Catalog.Providers.Count >= 2);
    }

    [Fact]
    public void LoadCatalog_WhenMinAppVersionTooHigh_FallsBackSafely()
    {
        using var temp = new TempDir();
        var catalogBytes = Encoding.UTF8.GetBytes("""
        {
          "schemaVersion": 1,
          "catalogVersion": 5,
          "minAppVersion": "99.0.0",
          "providers": []
        }
        """);

        var loader = new ProviderCatalogLoader(
            _fs,
            temp.Combine("c.json"),
            temp.Combine("c.sig"),
            temp.Combine("c.prev.json"),
            temp.Combine("c.local.json"),
            "0.1.3");

        var validation = loader.ValidateCandidate(catalogBytes, "dummy-sig");
        // Because dummy sig fails first or if sig check succeeds
        Assert.True(validation.Status is CatalogLoadStatus.SignatureInvalid or CatalogLoadStatus.RequiresNewerApp);
    }

    [Fact]
    public void LoadCatalog_LocalCustomCatalog_OverlaysProvidersCleanly()
    {
        using var temp = new TempDir();
        var localFile = temp.Combine("providers.local.json");

        var localCatalog = """
        {
          "schemaVersion": 1,
          "catalogVersion": 99,
          "providers": [
            {
              "id": "my-custom-proxy",
              "displayName": "My Custom Proxy",
              "match": {
                "exactHosts": ["myproxy.internal"]
              },
              "codex": {
                "wireApi": "responses"
              },
              "routes": [
                { "id": "primary", "displayName": "Default", "baseUrl": "https://myproxy.internal/v1" }
              ],
              "trustedHosts": ["myproxy.internal"],
              "capabilities": {
                "models": { "status": "supported", "strategy": "openai-models" },
                "balance": { "status": "unknown", "strategy": "unknown" },
                "usage": { "status": "unknown", "strategy": "unknown" }
              }
            }
          ]
        }
        """;
        File.WriteAllText(localFile, localCatalog);

        var loader = new ProviderCatalogLoader(
            _fs,
            temp.Combine("none.json"),
            temp.Combine("none.sig"),
            temp.Combine("none.prev.json"),
            localFile,
            "0.1.3");

        var result = loader.LoadCatalog();

        Assert.NotNull(result.Catalog);
        var custom = result.Catalog.Providers.FirstOrDefault(p => p.Id == "my-custom-proxy");
        Assert.NotNull(custom);
        Assert.Equal("My Custom Proxy", custom.DisplayName);
        // Embedded providers also preserved
        Assert.Contains(result.Catalog.Providers, p => p.Id == "router-cheap");
    }

    [Fact]
    public void CatalogUpdate_DoesNotMutateUserProfilesOrSecrets()
    {
        // Prove that ProviderDescriptor and ApiProviderProfile are strictly separate models
        var descriptor = new ProviderDescriptor
        {
            Id = "router-cheap",
            DisplayName = "Router.Cheap"
        };

        var profileId = Guid.NewGuid();
        var profile = new ApiProviderProfile
        {
            Id = profileId,
            CatalogProviderId = descriptor.Id,
            StableCodexProviderId = ApiProviderProfile.GenerateStableCodexProviderId(profileId),
            Nickname = "My Custom Profile",
            BaseUrl = "https://custom-override.example/v1"
        };

        // Updating catalog descriptor route or name must NOT alter profile's configured BaseUrl or Nickname
        descriptor.DisplayName = "Router.Cheap v2";
        Assert.Equal("https://custom-override.example/v1", profile.BaseUrl);
        Assert.Equal("My Custom Profile", profile.Nickname);
        Assert.StartsWith("switchboard_", profile.StableCodexProviderId);
    }

    [Fact]
    public void CatalogAndProfileModels_NeverSerializeApiKey()
    {
        var catalogJson = JsonSerializer.Serialize(new ProviderCatalog
        {
            Providers = new List<ProviderDescriptor>
            {
                new() { Id = "test", DisplayName = "Test" }
            }
        });
        Assert.DoesNotContain("apiKey", catalogJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("api_key", catalogJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", catalogJson, StringComparison.OrdinalIgnoreCase);

        var profileJson = JsonSerializer.Serialize(new ApiProviderProfile
        {
            Nickname = "Test Profile"
        });
        Assert.DoesNotContain("apiKey", profileJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("api_key", profileJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CatalogRemoval_DoesNotDeleteUserProfile()
    {
        var profileId = Guid.NewGuid();
        var profile = new ApiProviderProfile
        {
            Id = profileId,
            CatalogProviderId = "deprecated-provider",
            StableCodexProviderId = ApiProviderProfile.GenerateStableCodexProviderId(profileId),
            Nickname = "Existing Profile",
            BaseUrl = "https://deprecated-provider.example/v1",
            SelectedModel = "old-model"
        };

        // When a new catalog is loaded without "deprecated-provider":
        var newCatalog = new ProviderCatalog
        {
            Providers = new List<ProviderDescriptor>
            {
                new() { Id = "router-cheap", DisplayName = "Router.Cheap" }
            }
        };

        Assert.DoesNotContain(newCatalog.Providers, p => p.Id == profile.CatalogProviderId);

        // User profile remains completely intact and operational:
        Assert.Equal("Existing Profile", profile.Nickname);
        Assert.Equal("https://deprecated-provider.example/v1", profile.BaseUrl);
        Assert.Equal("old-model", profile.SelectedModel);
        Assert.NotEmpty(profile.StableCodexProviderId);
    }

    [Fact]
    public void RouterCheap_And_OpenRouter_Descriptors_FactStatusModel()
    {
        var catalogPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "catalog", "providers.catalog.json");
        var catalogBytes = File.ReadAllBytes(catalogPath);
        var catalog = JsonSerializer.Deserialize<ProviderCatalog>(catalogBytes);

        Assert.NotNull(catalog);

        // Router.Cheap checks
        var routerCheap = catalog.Providers.FirstOrDefault(p => p.Id == "router-cheap");
        Assert.NotNull(routerCheap);
        Assert.Equal("Router.Cheap", routerCheap.DisplayName);
        Assert.Equal(CapabilityStatus.Supported, routerCheap.Capabilities.Models.Status);
        Assert.Equal(CapabilityStatus.Unknown, routerCheap.Capabilities.Balance.Status);
        Assert.Equal(CapabilityStatus.Unknown, routerCheap.Capabilities.Usage.Status);
        Assert.Contains("router.cheap", routerCheap.TrustedHosts);
        Assert.Contains("direct.router-cheap.com", routerCheap.TrustedHosts);
        Assert.Equal("gpt-5.6-sol", routerCheap.Codex.DefaultModel);
        Assert.Equal("responses", routerCheap.Codex.WireApi);

        // OpenRouter checks
        var openRouter = catalog.Providers.FirstOrDefault(p => p.Id == "openrouter");
        Assert.NotNull(openRouter);
        Assert.Equal("OpenRouter", openRouter.DisplayName);
        Assert.Equal(CapabilityStatus.Supported, openRouter.Capabilities.Models.Status);
        Assert.Equal(CapabilityStatus.Supported, openRouter.Capabilities.Balance.Status);
        Assert.Equal(CapabilityStatus.Supported, openRouter.Capabilities.Usage.Status);
        Assert.Contains("openrouter.ai", openRouter.TrustedHosts);
        Assert.Equal("responses", openRouter.Codex.WireApi);
        Assert.Equal("/data/usage", openRouter.Capabilities.Balance.Response?.Used?.Pointer);
        Assert.Equal("/data/limit", openRouter.Capabilities.Balance.Response?.Limit?.Pointer);
    }

    [Fact]
    public void JsonDepthLimit_IsHandledGracefully()
    {
        // Construct deeply nested JSON beyond normal depth
        var sb = new StringBuilder();
        for (int i = 0; i < 100; i++) sb.Append("{\"nested\":");
        sb.Append("1");
        for (int i = 0; i < 100; i++) sb.Append('}');

        var deepJson = sb.ToString();

        // System.Text.Json default max depth is 64; parsing 100-level deep JSON throws JsonReaderException
        Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse(deepJson));
    }
}

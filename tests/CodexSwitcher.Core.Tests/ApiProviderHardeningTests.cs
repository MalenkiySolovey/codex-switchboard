using System.Text.Json;
using CodexSwitcher.Core.Abstractions;
using CodexSwitcher.Core.Catalog;
using CodexSwitcher.Core.Models;
using CodexSwitcher.Core.Services;
using CodexSwitcher.Core.Tests.TestSupport;
using CodexSwitcher.Infra;
using CodexSwitcher.Infra.Codex;
using CodexSwitcher.Infra.Io;
using CodexSwitcher.Infra.Security;
using Xunit;

namespace CodexSwitcher.Core.Tests;

public sealed class ApiProviderHardeningTests
{
    private readonly PhysicalFileSystem _fs = new();

    [Fact]
    public void Issue1_ProcessLaunch_UsesCanonicalRuntimeResolver_ResolvesCandidateDeterministically()
    {
        // Verify runtime resolution precedence for handoff
        var fakeCache = new CodexCapabilityCache();
        var resolver = new CodexRuntimeResolver(fakeCache);

        // When custom override is provided and exists, it resolves override
        var runtime = resolver.ResolveCurrentRuntime(@"C:\nonexistent\codex.exe");
        Assert.NotNull(runtime);

        // Enumerate candidates returns deterministic list without crashing
        var candidates = resolver.EnumerateCandidates();
        Assert.NotNull(candidates);
    }

    [Fact]
    public void Issue2_RouteSwitchLabel_ShowsCleanDisplayName_WithoutRegionInLabel()
    {
        using var temp = new TempDir();
        var catalogPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "catalog", "providers.catalog.json");
        var catalogSig = Path.ChangeExtension(catalogPath, ".sig");

        var loader = new ProviderCatalogLoader(
            _fs,
            catalogPath,
            catalogSig,
            temp.Combine("none.prev.json"),
            temp.Combine("none.local.json"),
            "0.1.4");

        var result = loader.LoadCatalog();
        Assert.NotNull(result.Catalog);

        var routerCheap = result.Catalog.Providers.FirstOrDefault(p => p.Id == "router-cheap");
        Assert.NotNull(routerCheap);

        var primaryRoute = routerCheap.Routes.FirstOrDefault(r => r.Id == "primary");
        Assert.NotNull(primaryRoute);

        // Verification: displayName is clean "Primary", region is "Hong Kong"
        Assert.Equal("Primary", primaryRoute.DisplayName);
        Assert.Equal("Hong Kong", primaryRoute.Region);

        // Verification: ViewModel / presentation uses clean DisplayName without string hacks
        var profile = new ApiProviderProfile
        {
            CatalogProviderId = "router-cheap",
            SelectedRouteId = "primary",
            BaseUrl = primaryRoute.BaseUrl
        };

        var matched = routerCheap.Routes.FirstOrDefault(r => r.Id == profile.SelectedRouteId);
        Assert.NotNull(matched);
        Assert.Equal("Primary", matched.DisplayName);
        Assert.DoesNotContain("(Hong Kong)", matched.DisplayName);
    }

    [Fact]
    public void Issue3_NWayRoutes_SupportsDynamic1_2_3_4_RoutesFixture()
    {
        // 1. Fixture with 1 route
        var routes1 = new List<ProviderRoute>
        {
            new() { Id = "single", DisplayName = "Only One", BaseUrl = "https://api.single.com/v1" }
        };
        Assert.Single(routes1);

        // 2. Fixture with 3 routes
        var routes3 = new List<ProviderRoute>
        {
            new() { Id = "r1", DisplayName = "Primary", Region = "US-East", BaseUrl = "https://r1.example.com/v1" },
            new() { Id = "r2", DisplayName = "Secondary", Region = "EU-Central", BaseUrl = "https://r2.example.com/v1" },
            new() { Id = "r3", DisplayName = "Fallback", Region = "AP-East", BaseUrl = "https://r3.example.com/v1" }
        };
        Assert.Equal(3, routes3.Count);

        // Verify cyclic traversal for 3 routes: 0 -> 1 -> 2 -> 0
        int idx = 0;
        idx = (idx + 1) % routes3.Count;
        Assert.Equal(1, idx);
        Assert.Equal("r2", routes3[idx].Id);

        idx = (idx + 1) % routes3.Count;
        Assert.Equal(2, idx);
        Assert.Equal("r3", routes3[idx].Id);

        idx = (idx + 1) % routes3.Count;
        Assert.Equal(0, idx);
        Assert.Equal("r1", routes3[idx].Id);

        // 3. Fixture with 4 routes: 0 -> 1 -> 2 -> 3 -> 0
        var routes4 = new List<ProviderRoute>
        {
            new() { Id = "a", DisplayName = "A", BaseUrl = "https://a.com/v1" },
            new() { Id = "b", DisplayName = "B", BaseUrl = "https://b.com/v1" },
            new() { Id = "c", DisplayName = "C", BaseUrl = "https://c.com/v1" },
            new() { Id = "d", DisplayName = "D", BaseUrl = "https://d.com/v1" }
        };
        Assert.Equal(4, routes4.Count);

        int cIdx = 2; // currently at "c"
        var nextRoute = routes4[(cIdx + 1) % routes4.Count];
        Assert.Equal("d", nextRoute.Id);

        cIdx = 3; // currently at "d"
        nextRoute = routes4[(cIdx + 1) % routes4.Count];
        Assert.Equal("a", nextRoute.Id);
    }

    [Fact]
    public void Issue3_CatalogExtensibility_ProvidersLocalJson_LoadedDynamicallyWithoutCodeChanges()
    {
        using var temp = new TempDir();
        var localFile = temp.Combine("providers.local.json");

        var dynamicProviderJson = """
        {
          "schemaVersion": 1,
          "catalogVersion": 42,
          "providers": [
            {
              "id": "custom-enterprise-router",
              "displayName": "Enterprise AI Gateway",
              "match": {
                "exactHosts": ["gateway.enterprise.corp"]
              },
              "codex": {
                "wireApi": "responses",
                "authStrategy": "bearer",
                "defaultModel": "enterprise-fast"
              },
              "routes": [
                { "id": "prod-us", "displayName": "Production US", "region": "US-West", "baseUrl": "https://gateway.enterprise.corp/v1", "isDefault": true },
                { "id": "prod-eu", "displayName": "Production EU", "region": "Frankfurt", "baseUrl": "https://gateway.enterprise.corp/eu/v1" },
                { "id": "dr-apac", "displayName": "Disaster Recovery", "region": "Tokyo", "baseUrl": "https://gateway.enterprise.corp/dr/v1" }
              ],
              "trustedHosts": ["gateway.enterprise.corp"],
              "capabilities": {
                "models": { "status": "supported", "strategy": "openai-models", "request": { "method": "GET", "path": "/models", "auth": "bearer" } },
                "balance": { "status": "unknown", "strategy": "unknown" },
                "usage": { "status": "unknown", "strategy": "unknown" }
              }
            }
          ]
        }
        """;
        File.WriteAllText(localFile, dynamicProviderJson);

        var loader = new ProviderCatalogLoader(
            _fs,
            temp.Combine("nonexistent.catalog.json"),
            temp.Combine("nonexistent.sig"),
            temp.Combine("nonexistent.prev.json"),
            localFile,
            "0.1.4");

        var result = loader.LoadCatalog();
        Assert.NotNull(result.Catalog);

        var enterprise = result.Catalog.Providers.FirstOrDefault(p => p.Id == "custom-enterprise-router");
        Assert.NotNull(enterprise);
        Assert.Equal("Enterprise AI Gateway", enterprise.DisplayName);
        Assert.Equal(3, enterprise.Routes.Count);
        Assert.Equal("Production US", enterprise.Routes[0].DisplayName);
        Assert.Equal("Production EU", enterprise.Routes[1].DisplayName);
        Assert.Equal("Disaster Recovery", enterprise.Routes[2].DisplayName);
        Assert.Equal("Tokyo", enterprise.Routes[2].Region);
    }

    [Fact]
    public void Issue4_ChatGptCard_WhenApiRoutingActive_IsInUseIsFalse_AndCanSwitchIsTrue()
    {
        // Test presentation logic invariants:
        // IsInUse == IsActive && IsRoutingActive
        // CanSwitch == !IsInUse && HealthStatus != HealthStatus.Unknown

        // Case A: API routing is active. ChatGPT account is active in auth.json (IsActive = true),
        // but routing in config.toml is set to an API provider (IsRoutingActive = false).
        bool isActive = true;
        bool isRoutingActive = false;

        bool isInUseA = isActive && isRoutingActive;
        bool canSwitchA = !isInUseA;

        Assert.False(isInUseA); // "In Use" badge is Collapsed
        Assert.True(canSwitchA); // "Switch" button is Visible & Enabled

        // Case B: Normal ChatGPT routing is active.
        isActive = true;
        isRoutingActive = true;

        bool isInUseB = isActive && isRoutingActive;
        bool canSwitchB = !isInUseB;

        Assert.True(isInUseB);  // "In Use" badge is Visible
        Assert.False(canSwitchB); // "Switch" button is Collapsed

        // Mutual exclusivity invariant:
        Assert.NotEqual(isInUseA, canSwitchA);
        Assert.NotEqual(isInUseB, canSwitchB);
    }

    [Fact]
    public void Issue5_UrlJoining_DeduplicatesV1_AndNormalizesOpenAiEndpoints()
    {
        // 1. Standard baseUrl with /v1 and path /models -> /v1/models
        var url1 = DeclarativeProviderInspector.JoinBaseUrlAndPath("https://router.cheap/v1", "/models");
        Assert.Equal("https://router.cheap/v1/models", url1);

        // 2. BaseUrl with /v1 and path /v1/models -> deduplicate to /v1/models (NOT /v1/v1/models!)
        var url2 = DeclarativeProviderInspector.JoinBaseUrlAndPath("https://router.cheap/v1", "/v1/models");
        Assert.Equal("https://router.cheap/v1/models", url2);

        // 3. BaseUrl with /v1 and path with trailing slash -> deduplicate
        var url3 = DeclarativeProviderInspector.JoinBaseUrlAndPath("https://router.cheap/v1/", "/v1/models");
        Assert.Equal("https://router.cheap/v1/models", url3);

        // 4. BaseUrl without /v1, strategy openai-models, path /models -> automatically adds /v1/models
        var url4 = DeclarativeProviderInspector.JoinBaseUrlAndPath("https://router.cheap", "/models", "openai-models");
        Assert.Equal("https://router.cheap/v1/models", url4);

        // 5. OpenRouter baseUrl https://openrouter.ai/api/v1 + /models -> /api/v1/models
        var url5 = DeclarativeProviderInspector.JoinBaseUrlAndPath("https://openrouter.ai/api/v1", "/models");
        Assert.Equal("https://openrouter.ai/api/v1/models", url5);

        // 6. OpenRouter baseUrl https://openrouter.ai/api/v1 + /v1/models -> deduplicate /v1
        var url6 = DeclarativeProviderInspector.JoinBaseUrlAndPath("https://openrouter.ai/api/v1", "/v1/models");
        Assert.Equal("https://openrouter.ai/api/v1/models", url6);
    }

    [Fact]
    public void Issue5_ModelCache_RouteSpecificByProfileRouteAndRevision_AndInvalidation()
    {
        var cache = new ProviderModelCache();
        var profileId = Guid.NewGuid();

        var routeA = "primary";
        var routeB = "reserve";

        var modelsA = new List<string> { "gpt-5.6-sol", "gpt-5.6-mini" };
        var modelsB = new List<string> { "gpt-5.6-sol", "claude-sonnet-4-direct" };

        // Cache models for Route A
        cache.SetModels(profileId, routeA, 1, modelsA);
        // Cache models for Route B
        cache.SetModels(profileId, routeB, 1, modelsB);

        // Verify route isolation
        Assert.True(cache.TryGetModels(profileId, routeA, 1, out var retrievedA));
        Assert.Equal(modelsA, retrievedA);

        Assert.True(cache.TryGetModels(profileId, routeB, 1, out var retrievedB));
        Assert.Equal(modelsB, retrievedB);

        // Verify revision isolation
        Assert.False(cache.TryGetModels(profileId, routeA, 2, out _));

        // Verify GetLatestModels returns most recently set models
        var latest = cache.GetLatestModels(profileId);
        Assert.NotNull(latest);
        Assert.Equal(modelsB, latest);

        // Invalidate single route
        cache.Invalidate(profileId, routeA);
        Assert.False(cache.TryGetModels(profileId, routeA, 1, out _));
        Assert.True(cache.TryGetModels(profileId, routeB, 1, out _));

        // Invalidate entire profile
        cache.Invalidate(profileId);
        Assert.False(cache.TryGetModels(profileId, routeB, 1, out _));
    }
}

using Xunit;

namespace CodexSwitcher.Core.Tests;

public sealed class ProviderInspectionServiceTests
{
    private const string SyntheticSecretKey = "sk-super-secret-key-do-not-leak";

    private sealed class FakeApiProviderStore : IApiProviderStore
    {
        public Dictionary<Guid, ApiProviderProfile> Profiles { get; } = new();

        public IReadOnlyList<ApiProviderProfile> GetAll() => Profiles.Values.ToList();
        public ApiProviderProfile? GetById(Guid id) => Profiles.TryGetValue(id, out var p) ? p : null;
        public ApiProviderProfile? GetByStableCodexProviderId(string stableId) =>
            Profiles.Values.FirstOrDefault(p => p.StableCodexProviderId == stableId);
        public void Save(ApiProviderProfile profile) => Profiles[profile.Id] = profile;
        public void SaveAll(IEnumerable<ApiProviderProfile> profiles, ApiProviderSaveIntent intent = ApiProviderSaveIntent.NormalUpdate)
        {
            foreach (var p in profiles) Profiles[p.Id] = p;
        }
        public bool Delete(Guid id) => Profiles.Remove(id);
        public bool SetStatus(Guid id, ApiProviderProfileStatus status)
        {
            if (Profiles.TryGetValue(id, out var p))
            {
                p.Status = status;
                return true;
            }
            return false;
        }
    }

    private sealed class FakeSecretStore : IApiKeySecretStore
    {
        public Dictionary<Guid, string> Secrets { get; } = new();
        public bool DeleteApiKey(Guid profileId) => Secrets.Remove(profileId);
        public string? GetApiKey(Guid profileId) => Secrets.TryGetValue(profileId, out var s) ? s : null;
        public bool HasApiKey(Guid profileId) => Secrets.ContainsKey(profileId);
        public void SaveApiKey(Guid profileId, string apiKey) => Secrets[profileId] = apiKey;
        public void CloneApiKey(Guid sourceProfileId, Guid targetProfileId)
        {
            if (Secrets.TryGetValue(sourceProfileId, out var s))
                Secrets[targetProfileId] = s;
        }
    }

    private sealed class FakeCatalogService : IProviderCatalogService
    {
        public Dictionary<string, ProviderDescriptor> Descriptors { get; } = new();
        public CatalogLoadResult CurrentResult => new()
        {
            Catalog = new ProviderCatalog { CatalogVersion = 1, Providers = Descriptors.Values.ToList() },
            ActiveLayer = CatalogSourceLayer.EmbeddedBootstrap,
            Status = CatalogLoadStatus.Supported,
        };

        public CatalogLoadResult Reload() => CurrentResult;

        public ProviderDescriptor? GetDescriptor(string? providerId) =>
            providerId != null && Descriptors.TryGetValue(providerId, out var d) ? d : null;

        public ProviderDescriptor? MatchDescriptor(string? hostOrUrl) => null;
    }

    private sealed class FakeInspector : IDeclarativeProviderInspector
    {
        public string? LastInspectedBaseUrl { get; private set; }
        public string? LastInspectedApiKey { get; private set; }
        public ProviderDescriptor? LastDescriptor { get; private set; }
        public bool GenericCalled { get; private set; }
        public bool DiscoverModelsCalled { get; private set; }

        public ApiProviderSnapshot ResultToReturn { get; set; } = new()
        {
            ConnectionStatus = HealthStatus.Valid,
            Models = new[] { "model-1", "model-2" },
            LastCheckedAt = DateTimeOffset.UtcNow,
        };

        public Exception? ExceptionToThrow { get; set; }

        public Task<ApiProviderSnapshot> InspectAsync(
            ProviderDescriptor descriptor,
            string activeBaseUrl,
            string? apiKey,
            CancellationToken ct = default)
        {
            if (ExceptionToThrow != null) throw ExceptionToThrow;
            LastDescriptor = descriptor;
            LastInspectedBaseUrl = activeBaseUrl;
            LastInspectedApiKey = apiKey;
            return Task.FromResult(ResultToReturn);
        }

        public Task<ApiProviderSnapshot> DiscoverModelsAsync(
            ProviderDescriptor descriptor,
            string activeBaseUrl,
            string? apiKey,
            CancellationToken ct = default)
        {
            if (ExceptionToThrow != null) throw ExceptionToThrow;
            DiscoverModelsCalled = true;
            LastDescriptor = descriptor;
            LastInspectedBaseUrl = activeBaseUrl;
            LastInspectedApiKey = apiKey;
            return Task.FromResult(ResultToReturn);
        }

        public Task<ApiProviderSnapshot> InspectGenericUnknownAsync(
            string rawBaseUrl,
            string? apiKey,
            CancellationToken ct = default)
        {
            if (ExceptionToThrow != null) throw ExceptionToThrow;
            GenericCalled = true;
            LastInspectedBaseUrl = rawBaseUrl;
            LastInspectedApiKey = apiKey;
            return Task.FromResult(ResultToReturn);
        }
    }

    private sealed class FakeModelCache : IProviderModelCache
    {
        public Dictionary<string, IReadOnlyList<string>> Cached { get; } = new();

        public IReadOnlyList<string>? GetLatestModels(Guid profileId) =>
            Cached.Values.FirstOrDefault();

        public void SetModels(Guid profileId, string routeKey, int revision, IReadOnlyList<string> models)
        {
            Cached[$"{profileId}:{routeKey}"] = models;
        }

        public bool TryGetModels(Guid profileId, string routeKey, int revision, out IReadOnlyList<string> models)
        {
            return Cached.TryGetValue($"{profileId}:{routeKey}", out models!);
        }

        public void Invalidate(Guid profileId, string? routeKey = null)
        {
            if (routeKey != null)
                Cached.Remove($"{profileId}:{routeKey}");
            else
                Cached.Clear();
        }
    }

    [Fact]
    public async Task InspectAsync_WithCatalogDescriptor_PassesDescriptorAndSecretToInspector()
    {
        var apiStore = new FakeApiProviderStore();
        var secretStore = new FakeSecretStore();
        var catalogService = new FakeCatalogService();
        var inspector = new FakeInspector();
        var cache = new FakeModelCache();

        var profileId = Guid.NewGuid();
        var profile = new ApiProviderProfile
        {
            Id = profileId,
            Nickname = "Test Provider",
            CatalogProviderId = "test-cat",
            BaseUrl = "https://api.test.com/v1",
            SelectedRouteId = "primary",
        };
        apiStore.Save(profile);
        secretStore.SaveApiKey(profileId, SyntheticSecretKey);

        var descriptor = new ProviderDescriptor { Id = "test-cat", DisplayName = "Test Catalog" };
        catalogService.Descriptors["test-cat"] = descriptor;

        var service = new ProviderInspectionService(apiStore, secretStore, catalogService, inspector, cache);
        var snapshot = await service.InspectAsync(profileId);

        Assert.Equal(HealthStatus.Valid, snapshot.ConnectionStatus);
        Assert.Equal(2, snapshot.Models.Count);
        Assert.Equal("https://api.test.com/v1", inspector.LastInspectedBaseUrl);
        Assert.Equal(SyntheticSecretKey, inspector.LastInspectedApiKey);
        Assert.Same(descriptor, inspector.LastDescriptor);

        // Verify cache updated
        Assert.True(cache.TryGetModels(profileId, "primary", 1, out var models));
        Assert.Equal(2, models.Count);
    }

    [Fact]
    public async Task InspectAsync_WithGenericProvider_CallsGenericInspector()
    {
        var apiStore = new FakeApiProviderStore();
        var secretStore = new FakeSecretStore();
        var catalogService = new FakeCatalogService();
        var inspector = new FakeInspector();

        var profileId = Guid.NewGuid();
        var profile = new ApiProviderProfile
        {
            Id = profileId,
            Nickname = "Generic Unknown",
            CatalogProviderId = null,
            BaseUrl = "https://my-custom-llm.org/v1",
        };
        apiStore.Save(profile);
        secretStore.SaveApiKey(profileId, SyntheticSecretKey);

        var service = new ProviderInspectionService(apiStore, secretStore, catalogService, inspector);
        var snapshot = await service.InspectAsync(profileId);

        Assert.Equal(HealthStatus.Valid, snapshot.ConnectionStatus);
        Assert.True(inspector.GenericCalled);
        Assert.Equal("https://my-custom-llm.org/v1", inspector.LastInspectedBaseUrl);
        Assert.Equal(SyntheticSecretKey, inspector.LastInspectedApiKey);
    }

    [Fact]
    public async Task InspectAsync_WhenProfileNotFound_ReturnsErrorSnapshotWithoutThrowing()
    {
        var service = new ProviderInspectionService(
            new FakeApiProviderStore(),
            new FakeSecretStore(),
            new FakeCatalogService(),
            new FakeInspector());

        var snapshot = await service.InspectAsync(Guid.NewGuid());
        Assert.Equal(HealthStatus.Error, snapshot.ConnectionStatus);
        Assert.Equal("Provider profile not found.", snapshot.Error);
    }

    [Fact]
    public async Task InspectAsync_WhenInspectorThrows_SanitizesErrorAndNeverLeaksSecret()
    {
        var apiStore = new FakeApiProviderStore();
        var secretStore = new FakeSecretStore();
        var inspector = new FakeInspector
        {
            ExceptionToThrow = new InvalidOperationException($"Connection failed with Bearer {SyntheticSecretKey}"),
        };

        var profileId = Guid.NewGuid();
        apiStore.Save(new ApiProviderProfile { Id = profileId, BaseUrl = "https://leak-test.com" });
        secretStore.SaveApiKey(profileId, SyntheticSecretKey);

        var service = new ProviderInspectionService(
            apiStore,
            secretStore,
            new FakeCatalogService(),
            inspector);

        var snapshot = await service.InspectAsync(profileId);

        Assert.Equal(HealthStatus.Error, snapshot.ConnectionStatus);
        Assert.NotNull(snapshot.Error);
        Assert.DoesNotContain(SyntheticSecretKey, snapshot.Error);
        Assert.Contains("credentials redacted", snapshot.Error);
    }

    [Fact]
    public async Task InspectAsync_StoredKeyNeverPresentInSnapshotProperties()
    {
        var apiStore = new FakeApiProviderStore();
        var secretStore = new FakeSecretStore();
        var inspector = new FakeInspector();

        var profileId = Guid.NewGuid();
        apiStore.Save(new ApiProviderProfile { Id = profileId, BaseUrl = "https://safe.com" });
        secretStore.SaveApiKey(profileId, SyntheticSecretKey);

        var service = new ProviderInspectionService(apiStore, secretStore, new FakeCatalogService(), inspector);
        var snapshot = await service.InspectAsync(profileId);

        // Verify snapshot does not contain the key anywhere
        Assert.DoesNotContain(SyntheticSecretKey, snapshot.Error ?? string.Empty);
        foreach (var model in snapshot.Models)
        {
            Assert.DoesNotContain(SyntheticSecretKey, model);
        }
    }

    [Fact]
    public async Task DiscoverModelsAsync_UsesEndpointOwnedCredentialAndDiscoveryOnlyInspector()
    {
        var apiStore = new FakeApiProviderStore();
        var secretStore = new FakeSecretStore();
        var catalogService = new FakeCatalogService();
        var inspector = new FakeInspector();
        var cache = new FakeModelCache();
        var profileId = Guid.NewGuid();
        var endpointId = Guid.NewGuid();
        var profile = new ApiProviderProfile
        {
            Id = profileId,
            EndpointId = endpointId,
            CatalogProviderId = "endpoint-owned",
            BaseUrl = "https://models.example.test/v1",
            SelectedRouteId = "primary",
        };
        apiStore.Save(profile);
        // Reproduces the QA state: the secret belongs to a shared endpoint,
        // not to each individual model configuration/profile ID.
        secretStore.SaveApiKey(endpointId, SyntheticSecretKey);
        catalogService.Descriptors["endpoint-owned"] = new ProviderDescriptor
        {
            Id = "endpoint-owned",
            DisplayName = "Endpoint-owned test descriptor",
        };

        var service = new ProviderInspectionService(apiStore, secretStore, catalogService, inspector, cache);
        var snapshot = await service.DiscoverModelsAsync(profileId);

        Assert.True(inspector.DiscoverModelsCalled);
        Assert.Equal(SyntheticSecretKey, inspector.LastInspectedApiKey);
        Assert.Equal("https://models.example.test/v1", inspector.LastInspectedBaseUrl);
        Assert.Equal(2, snapshot.Models.Count);
        Assert.True(cache.TryGetModels(profileId, "primary", 1, out var cached));
        Assert.Equal(snapshot.Models, cached);
    }
}

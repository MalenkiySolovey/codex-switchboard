using System.Text.Json;
using CodexSwitcher.Core.Tests.TestSupport;
using Xunit;

namespace CodexSwitcher.Core.Tests;

/// <summary>
/// Regression coverage for preview.19's one-owner model refresh pipeline.
/// The persistence double stores JSON rather than object references so every
/// assertion exercises a genuine serialize/reload boundary.
/// </summary>
public sealed class ApiModelInventoryRefreshServiceTests
{
    private sealed class CallbackProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    private sealed class JsonProfileStore : IApiProviderStore
    {
        private readonly Dictionary<Guid, string> _records;

        public JsonProfileStore()
            : this(new Dictionary<Guid, string>())
        {
        }

        private JsonProfileStore(Dictionary<Guid, string> records)
        {
            _records = records;
        }

        public int SaveCount { get; private set; }

        public JsonProfileStore Reopen() =>
            new(new Dictionary<Guid, string>(_records, EqualityComparer<Guid>.Default));

        public IReadOnlyList<ApiProviderProfile> GetAll() =>
            _records.Values.Select(Deserialize).ToList();

        public ApiProviderProfile? GetById(Guid id) =>
            _records.TryGetValue(id, out var json) ? Deserialize(json) : null;

        public ApiProviderProfile? GetByStableCodexProviderId(string stableId) =>
            GetAll().FirstOrDefault(profile =>
                string.Equals(profile.StableCodexProviderId, stableId, StringComparison.Ordinal));

        public void Save(ApiProviderProfile profile)
        {
            _records[profile.Id] = JsonSerializer.Serialize(profile);
            SaveCount++;
        }

        public void SaveAll(IEnumerable<ApiProviderProfile> profiles, ApiProviderSaveIntent intent = ApiProviderSaveIntent.NormalUpdate)
        {
            foreach (var profile in profiles)
            {
                Save(profile);
            }
        }

        public bool Delete(Guid id) => _records.Remove(id);

        public bool SetStatus(Guid id, ApiProviderProfileStatus status)
        {
            var profile = GetById(id);
            if (profile is null)
            {
                return false;
            }

            profile.Status = status;
            Save(profile);
            return true;
        }

        private static ApiProviderProfile Deserialize(string json) =>
            JsonSerializer.Deserialize<ApiProviderProfile>(json)
            ?? throw new InvalidOperationException("Test profile JSON unexpectedly deserialized to null.");
    }

    private sealed class ScriptedInspectionService : IProviderInspectionService
    {
        private readonly Queue<Func<CancellationToken, Task<ApiProviderSnapshot>>> _discoveries = [];

        public int DiscoverCalls { get; private set; }

        public void Enqueue(ApiProviderSnapshot result) =>
            _discoveries.Enqueue(_ => Task.FromResult(result));

        public void Enqueue(Func<CancellationToken, Task<ApiProviderSnapshot>> operation) =>
            _discoveries.Enqueue(operation);

        public Task<ApiProviderSnapshot> DiscoverModelsAsync(Guid providerProfileId, CancellationToken cancellationToken = default)
        {
            DiscoverCalls++;
            if (_discoveries.Count == 0)
            {
                throw new InvalidOperationException("No scripted GET /models result was configured.");
            }

            return _discoveries.Dequeue()(cancellationToken);
        }

        public Task<ApiProviderSnapshot> InspectAsync(Guid providerProfileId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The refresh pipeline must use DiscoverModelsAsync, not full inspection.");
    }

    private sealed class MemoryModelCache : IProviderModelCache
    {
        private readonly Dictionary<string, IReadOnlyList<string>> _entries = new(StringComparer.Ordinal);

        public IReadOnlyList<string>? GetLatestModels(Guid profileId) =>
            _entries.Where(entry => entry.Key.StartsWith($"{profileId:D}:", StringComparison.Ordinal))
                .Select(entry => entry.Value)
                .LastOrDefault();

        public void SetModels(Guid profileId, string routeKey, int revision, IReadOnlyList<string> models) =>
            _entries[$"{profileId:D}:{routeKey}:{revision}"] = models.ToList();

        public bool TryGetModels(Guid profileId, string routeKey, int revision, out IReadOnlyList<string> models) =>
            _entries.TryGetValue($"{profileId:D}:{routeKey}:{revision}", out models!);

        public void Invalidate(Guid profileId, string? routeKey = null)
        {
            foreach (var key in _entries.Keys
                         .Where(key => key.StartsWith($"{profileId:D}:", StringComparison.Ordinal) &&
                             (routeKey is null || key.Contains($":{routeKey}:", StringComparison.Ordinal)))
                         .ToList())
            {
                _entries.Remove(key);
            }
        }
    }

    private sealed class RecordingCatalogService : ICodexModelCatalogService
    {
        public int ProfileCatalogCalls { get; private set; }
        public ApiProviderProfile? LastProfile { get; private set; }

        public string? EnsureModelCatalog(
            string modelSlug,
            long? contextWindowTokens,
            CodexModelOverrides? modelOverrides = null,
            EffectiveToolPolicy? toolPolicy = null) =>
            throw new NotSupportedException("Only profile catalogs are valid in preview.19.");

        public string? EnsureProfileModelCatalog(
            ApiProviderProfile profile,
            string? modelSlug = null,
            long? contextWindowTokens = null,
            CodexModelOverrides? modelOverrides = null,
            EffectiveToolPolicy? toolPolicy = null)
        {
            ProfileCatalogCalls++;
            LastProfile = profile;
            return Path.Combine("C:", "catalogs", profile.Id.ToString("D"), "models.json");
        }
    }

    private sealed class RecordingRoutingStore : ICodexRoutingConfigStore
    {
        public CodexRoutingState State { get; set; } =
            new(null, null, new Dictionary<string, CodexProviderBlock>(), "initial");

        public int ApplySwitchboardRoutingCalls { get; private set; }

        public string ComputeFingerprint(string configTomlPath) => State.Fingerprint;

        public CodexRoutingState ReadRoutingState(string configTomlPath) => State;

        public string ApplySwitchboardRouting(
            string configTomlPath,
            CodexProviderBlock providerBlock,
            string model,
            CodexModelOverrides? modelOverrides = null,
            string? modelCatalogJson = null,
            string? expectedFingerprint = null)
        {
            ApplySwitchboardRoutingCalls++;
            State = new CodexRoutingState(
                providerBlock.ProviderId,
                model,
                new Dictionary<string, CodexProviderBlock> { [providerBlock.ProviderId] = providerBlock },
                "changed",
                modelCatalogJson);
            return State.Fingerprint;
        }

        public string UpdateProviderRoute(string configTomlPath, string providerId, string newBaseUrl, string? expectedFingerprint = null) =>
            State.Fingerprint;

        public string ReturnToOpenAi(string configTomlPath, string? model = null, string? expectedFingerprint = null)
        {
            State = new CodexRoutingState("openai", model, new Dictionary<string, CodexProviderBlock>(), "openai");
            return State.Fingerprint;
        }

        public void RestoreExactBytes(string configTomlPath, byte[] exactBytes)
        {
        }

        public void CleanOrphanProviderBlocks(string configTomlPath, IReadOnlySet<string> knownProviderIds)
        {
        }

        public void ReconcileAndRepairContaminatedConfig(string configTomlPath, IReadOnlySet<string> knownProviderIds)
        {
        }
    }

    private sealed class RecordingTargetSwitchService : ICodexTargetSwitchService
    {
        private readonly JsonProfileStore _profiles;
        private readonly RecordingRoutingStore _routing;
        private readonly RecordingCatalogService _catalogs;

        public RecordingTargetSwitchService(
            JsonProfileStore profiles,
            RecordingRoutingStore routing,
            RecordingCatalogService catalogs)
        {
            _profiles = profiles;
            _routing = routing;
            _catalogs = catalogs;
        }

        public int ApiSwitchCalls { get; private set; }
        public bool ReturnFailure { get; set; }

        public Task<TargetSwitchResult> SwitchToApiProviderAsync(
            Guid apiProfileId,
            SwitchExecutionOptions options,
            CancellationToken cancellationToken = default)
        {
            ApiSwitchCalls++;
            options.Progress?.Report("Restarting Codex...");
            options.Progress?.Report("Verifying models...");
            var profile = _profiles.GetById(apiProfileId)
                ?? throw new InvalidOperationException("Test profile was not persisted.");

            if (ReturnFailure)
            {
                return Task.FromResult(new TargetSwitchResult(
                    TargetSwitchOutcome.Failed,
                    "Injected switch failure",
                    new ActiveTarget.Api(profile)));
            }

            var catalogPath = _catalogs.EnsureProfileModelCatalog(profile);
            _routing.State = new CodexRoutingState(
                profile.StableCodexProviderId,
                profile.SelectedModel,
                new Dictionary<string, CodexProviderBlock>(),
                "active-reconciled",
                catalogPath);
            return Task.FromResult(new TargetSwitchResult(
                TargetSwitchOutcome.Success,
                "Reconciled",
                new ActiveTarget.Api(profile),
                DiagnosticTrace: new SwitchDiagnosticTrace
                {
                    RuntimeRestartRequired = true,
                    RuntimeRestartCompleted = true,
                    ActiveModelListVerified = true,
                }));
        }

        public Task<TargetSwitchResult> SwitchToChatGptAsync(
            Guid? targetChatGptProfileId,
            List<ProfileMetadata> allChatGptProfiles,
            SwitchExecutionOptions options,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<TargetSwitchResult> SwitchApiRouteAsync(
            Guid apiProfileId,
            string routeId,
            string newBaseUrl,
            SwitchExecutionOptions options,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task RefreshAsync_PersistsMergedInventoryAcrossStoreReload_AndPreservesManualAndDisabledEntries()
    {
        var profile = CreateProfile("deepseek-v4.1-flash");
        profile.ModelInventory = new ApiProviderModelInventory
        {
            SelectedModel = profile.SelectedModel,
            Models =
            [
                new ApiProviderModelItem
                {
                    Slug = "manual-only",
                    Enabled = false,
                    DiscoverySource = ModelDiscoverySource.Manual,
                    Availability = ModelAvailability.Unknown,
                },
                new ApiProviderModelItem
                {
                    Slug = "deepseek-v4.1-flash",
                    Enabled = false,
                    DiscoverySource = ModelDiscoverySource.Discovered,
                    Availability = ModelAvailability.Reported,
                },
                new ApiProviderModelItem
                {
                    Slug = "vanished-discovered",
                    Enabled = true,
                    DiscoverySource = ModelDiscoverySource.Discovered,
                    Availability = ModelAvailability.Reported,
                },
            ],
        };

        var store = new JsonProfileStore();
        store.Save(profile);
        var inspection = new ScriptedInspectionService();
        inspection.Enqueue(Snapshot("deepseek-v4.1-flash", "deepseek-v4.1-flash:free"));
        var (service, _, routing, catalogs, switches) = CreateService(store, inspection);

        var result = await service.RefreshAsync(profile.Id);

        Assert.True(result.Succeeded);
        Assert.True(result.InventoryPersisted);
        Assert.Equal(2, result.ModelsReportedCount);
        Assert.Equal(1, result.ModelsAdded);
        Assert.Equal(1, result.ManualModelsPreserved);
        Assert.Equal(0, switches.ApiSwitchCalls);
        Assert.Equal(0, routing.ApplySwitchboardRoutingCalls);
        Assert.Equal(1, catalogs.ProfileCatalogCalls);

        // Simulates a process restart: only serialized profile data survives.
        var reloaded = store.Reopen().GetById(profile.Id)!;
        Assert.Equal("deepseek-v4.1-flash", reloaded.SelectedModel);
        Assert.NotNull(reloaded.ModelInventory);
        Assert.Contains(reloaded.ModelInventory!.Models, item =>
            item.Slug == "manual-only" &&
            item.DiscoverySource == ModelDiscoverySource.Manual &&
            !item.Enabled);
        Assert.Contains(reloaded.ModelInventory.Models, item =>
            item.Slug == "deepseek-v4.1-flash" && !item.Enabled);
        Assert.Contains(reloaded.ModelInventory.Models, item =>
            item.Slug == "deepseek-v4.1-flash:free" &&
            item.Enabled &&
            item.DiscoverySource == ModelDiscoverySource.Discovered);
        Assert.Contains(reloaded.ModelInventory.Models, item =>
            item.Slug == "vanished-discovered" &&
            item.Availability == ModelAvailability.NotReported);
    }

    [Fact]
    public async Task RefreshAsync_NewerRequestWins_AndOlderResultCannotPersistOrReplaceSuccess()
    {
        var profile = CreateProfile("model-new");
        var store = new JsonProfileStore();
        store.Save(profile);
        var inspection = new ScriptedInspectionService();
        var firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource<ApiProviderSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        inspection.Enqueue(async _ =>
        {
            firstStarted.TrySetResult(true);
            return await releaseFirst.Task.ConfigureAwait(false);
        });
        inspection.Enqueue(Snapshot("model-new", "only-new"));

        var (service, _, _, _, _) = CreateService(store, inspection);
        var older = service.RefreshAsync(profile.Id);
        await firstStarted.Task;
        var newer = service.RefreshAsync(profile.Id);
        releaseFirst.TrySetResult(Snapshot("model-old", "must-not-persist"));

        var olderResult = await older;
        var newerResult = await newer;

        Assert.Equal(ModelInventoryRefreshOutcome.Superseded, olderResult.Outcome);
        Assert.True(newerResult.Succeeded);
        Assert.Equal(newerResult, service.GetLatestResult(profile.Id));
        var persisted = store.GetById(profile.Id)!;
        Assert.Contains(persisted.ModelInventory!.Models, item => item.Slug == "only-new");
        Assert.DoesNotContain(persisted.ModelInventory.Models, item => item.Slug == "must-not-persist");
    }

    [Fact]
    public async Task RefreshAsync_NewerSuccessClearsEarlierSanitized401State()
    {
        var profile = CreateProfile("model-1");
        var store = new JsonProfileStore();
        store.Save(profile);
        var inspection = new ScriptedInspectionService();
        inspection.Enqueue(new ApiProviderSnapshot
        {
            ConnectionStatus = HealthStatus.Error,
            Error = "HTTP 401 Unauthorized",
            Models = [],
        });
        inspection.Enqueue(Snapshot("model-1", "model-2"));
        var (service, _, _, _, _) = CreateService(store, inspection);

        var failed = await service.RefreshAsync(profile.Id);
        var succeeded = await service.RefreshAsync(profile.Id);

        Assert.Equal(ModelInventoryRefreshOutcome.NoModelsReported, failed.Outcome);
        Assert.Contains("401", failed.ErrorMessageSanitized, StringComparison.Ordinal);
        Assert.True(succeeded.Succeeded);
        Assert.Null(succeeded.ErrorMessageSanitized);
        Assert.Equal(succeeded, service.GetLatestResult(profile.Id));
    }

    [Fact]
    public async Task RefreshAsync_InactiveProfileDoesNotMutateCurrentRouting_AndPrebuildsOnlyItsOwnCatalog()
    {
        var profile = CreateProfile("deepseek-v4.1-flash");
        var store = new JsonProfileStore();
        store.Save(profile);
        var inspection = new ScriptedInspectionService();
        inspection.Enqueue(Snapshot("deepseek-v4.1-flash", "model-extra"));
        var (service, _, routing, catalogs, switches) = CreateService(store, inspection);
        var unchanged = new CodexRoutingState(
            "switchboard_other",
            "other-model",
            new Dictionary<string, CodexProviderBlock>(),
            "unrelated-fingerprint",
            "C:\\catalogs\\other\\models.json");
        routing.State = unchanged;

        var result = await service.RefreshAsync(profile.Id);

        Assert.True(result.Succeeded);
        Assert.False(result.ActiveTargetReconciled);
        Assert.False(result.RuntimeRestarted);
        Assert.Equal(ModelCatalogVerificationStatus.NotRequired, result.VerificationStatus);
        Assert.Equal(0, switches.ApiSwitchCalls);
        Assert.Equal(0, routing.ApplySwitchboardRoutingCalls);
        Assert.Equal(unchanged, routing.State);
        Assert.Equal(1, catalogs.ProfileCatalogCalls);
        Assert.Equal(profile.Id, catalogs.LastProfile!.Id);
    }

    [Fact]
    public async Task RefreshAsync_ActiveProfileUsesTargetSwitchToRebuildCatalogRestartAndVerify()
    {
        var profile = CreateProfile("deepseek-v4.1-flash");
        profile.ModelInventory = new ApiProviderModelInventory
        {
            SelectedModel = profile.SelectedModel,
            Models = [new ApiProviderModelItem { Slug = profile.SelectedModel!, Enabled = true }],
        };
        var store = new JsonProfileStore();
        store.Save(profile);
        var inspection = new ScriptedInspectionService();
        inspection.Enqueue(Snapshot("deepseek-v4.1-flash", "deepseek-v4.1-flash:free"));
        var (service, _, routing, catalogs, switches) = CreateService(store, inspection);
        var progressMessages = new List<string>();
        routing.State = new CodexRoutingState(
            profile.StableCodexProviderId,
            profile.SelectedModel,
            new Dictionary<string, CodexProviderBlock>(),
            "before-active-refresh",
            "C:\\catalogs\\old\\models.json");

        var result = await service.RefreshAsync(
            profile.Id,
            new RefreshModelsOptions(Progress: new CallbackProgress(progressMessages.Add)));

        Assert.True(result.Succeeded);
        Assert.True(result.ActiveTargetReconciled);
        Assert.True(result.RuntimeRestarted);
        Assert.Equal(ModelCatalogVerificationStatus.Verified, result.VerificationStatus);
        Assert.Equal(1, switches.ApiSwitchCalls);
        Assert.Equal(1, catalogs.ProfileCatalogCalls);
        Assert.Equal(profile.StableCodexProviderId, routing.State.ModelProvider);
        Assert.Equal("deepseek-v4.1-flash", routing.State.Model);
        Assert.Equal(result.CatalogPath, routing.State.ModelCatalogJson);
        Assert.Contains("Refreshing models...", progressMessages);
        Assert.Contains("Updating catalog...", progressMessages);
        Assert.Contains("Restarting Codex...", progressMessages);
        Assert.Contains("Verifying models...", progressMessages);
    }

    [Fact]
    public async Task RefreshAsync_ActiveSwitchFailureRestoresPersistedInventorySnapshot()
    {
        var profile = CreateProfile("model-original");
        profile.ModelInventory = new ApiProviderModelInventory
        {
            SelectedModel = "model-original",
            Models =
            [
                new ApiProviderModelItem
                {
                    Slug = "model-original",
                    Enabled = true,
                    DiscoverySource = ModelDiscoverySource.Manual,
                    Availability = ModelAvailability.Reported,
                },
            ],
        };
        var store = new JsonProfileStore();
        store.Save(profile);
        var inspection = new ScriptedInspectionService();
        inspection.Enqueue(Snapshot("model-original", "model-new"));
        var (service, _, routing, _, switches) = CreateService(store, inspection);
        routing.State = new CodexRoutingState(
            profile.StableCodexProviderId,
            "model-original",
            new Dictionary<string, CodexProviderBlock>(),
            "active-before-failure",
            "C:\\catalogs\\original\\models.json");
        switches.ReturnFailure = true;

        var result = await service.RefreshAsync(profile.Id);

        Assert.Equal(ModelInventoryRefreshOutcome.Failed, result.Outcome);
        Assert.Equal("ActiveTargetReconciliationFailed", result.ErrorKind);
        var restored = store.GetById(profile.Id)!;
        Assert.Single(restored.ModelInventory!.Models);
        Assert.Equal("model-original", restored.ModelInventory.Models[0].Slug);
        Assert.DoesNotContain(restored.ModelInventory.Models, item => item.Slug == "model-new");
    }

    [Fact]
    public async Task RefreshAsync_ReplacesProfileDerivedDisplayNameWithConservativeSlugDisplay()
    {
        var profile = CreateProfile("grok-4.6");
        profile.Nickname = "Modelflare";
        profile.ModelInventory = new ApiProviderModelInventory
        {
            SelectedModel = "grok-4.6",
            Models =
            [
                new ApiProviderModelItem
                {
                    Slug = "grok-4.6",
                    DisplayName = "Modelflare Grok",
                    Enabled = true,
                    DiscoverySource = ModelDiscoverySource.CatalogKnown,
                },
            ],
        };
        var store = new JsonProfileStore();
        store.Save(profile);
        var inspection = new ScriptedInspectionService();
        inspection.Enqueue(Snapshot("grok-4.6"));
        var (service, _, _, _, _) = CreateService(store, inspection);

        var result = await service.RefreshAsync(profile.Id);

        Assert.True(result.Succeeded);
        var refreshed = store.GetById(profile.Id)!;
        Assert.Equal("Grok 4.6", refreshed.ModelInventory!.Models.Single().DisplayName);
        Assert.Equal("DeepSeek v4.1 Flash:free", ModelDisplayName.FromSlug("deepseek-v4.1-flash:free"));
    }

    [Fact]
    public void MergeDiscoveredModels_PreservesExactFreeAndNonFreeSlugsAsDifferentModels()
    {
        var inventory = new ApiProviderModelInventory();

        inventory.MergeDiscoveredModels(["deepseek-v4.1-flash", "deepseek-v4.1-flash:free"]);

        Assert.Equal(2, inventory.Models.Count);
        Assert.Contains(inventory.Models, item => item.Slug == "deepseek-v4.1-flash");
        Assert.Contains(inventory.Models, item => item.Slug == "deepseek-v4.1-flash:free");
    }

    private static (ApiModelInventoryRefreshService Service, MemoryModelCache Cache, RecordingRoutingStore Routing, RecordingCatalogService Catalogs, RecordingTargetSwitchService Switches)
        CreateService(JsonProfileStore store, ScriptedInspectionService inspection)
    {
        var cache = new MemoryModelCache();
        var routing = new RecordingRoutingStore();
        var catalogs = new RecordingCatalogService();
        var switches = new RecordingTargetSwitchService(store, routing, catalogs);
        var paths = CodexPaths.ForHome(Path.Combine(Path.GetTempPath(), "codex-switchboard-refresh-tests", Guid.NewGuid().ToString("N")));
        var service = new ApiModelInventoryRefreshService(
            store,
            inspection,
            cache,
            routing,
            catalogs,
            switches,
            paths,
            new FakeClock());
        return (service, cache, routing, catalogs, switches);
    }

    private static ApiProviderProfile CreateProfile(string selectedModel)
    {
        var id = Guid.NewGuid();
        return new ApiProviderProfile
        {
            Id = id,
            StableCodexProviderId = ApiProviderProfile.GenerateStableCodexProviderId(id),
            Nickname = "Refresh test profile",
            BaseUrl = "https://api.example.test/v1",
            SelectedRouteId = "primary",
            SelectedModel = selectedModel,
            Status = ApiProviderProfileStatus.Active,
        };
    }

    private static ApiProviderSnapshot Snapshot(params string[] models) =>
        new()
        {
            ConnectionStatus = HealthStatus.Valid,
            Models = models,
            LastCheckedAt = new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero),
        };
}

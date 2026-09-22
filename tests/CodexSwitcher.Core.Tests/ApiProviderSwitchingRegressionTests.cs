using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using CodexSwitcher.Core.Accounts.Models;
using CodexSwitcher.Core.Common.Enums;
using CodexSwitcher.Core.Common.Lifecycle;
using CodexSwitcher.Core.Providers.Models;
using CodexSwitcher.Core.Routing.Contracts;
using CodexSwitcher.Core.Routing.Models;
using CodexSwitcher.Core.Routing.Services;
using CodexSwitcher.Core.Tests.TestSupport;
using CodexSwitcher.Infra.Codex.Runtime;
using Xunit;

namespace CodexSwitcher.Core.Tests;

/// <summary>
/// Comprehensive regression test suite proving the fix for the v0.2.1-preview.8
/// silent switching failure on newly created API providers/models.
/// Covers:
/// 1. Endpoint/Model secret ownership decoupling
/// 2. Positive postcondition verification and transactional rollback
/// 3. Legacy profile backward-compatibility
/// 4. Modern schema catalog generation without deadlocks
/// </summary>
public sealed class ApiProviderSwitchingRegressionTests
{
    private sealed class TestEnvironment : IDisposable
    {
        public TempDir Dir { get; } = new();
        public FaultInjectingFileSystem Fs { get; }
        public VaultService Vault { get; }
        public ProfileStore ProfileStore { get; }
        public FakeProcessManager Proc { get; } = new();
        public FakeConfigStore Config { get; } = new();
        public FakeClock Clock { get; } = new();
        public FakeAudit Audit { get; } = new();
        public AppPaths Paths { get; }
        public CodexRoutingConfigStore RoutingStore { get; }
        public ApiKeySecretStore SecretStore { get; }
        public ApiProviderStore ApiStore { get; }
        public KeyBrokerInstaller BrokerInstaller { get; }
        public CodexModelCatalogService CatalogService { get; }
        public SwitchService ChatGptSwitch { get; }
        public SwitchPlanBuilder PlanBuilder { get; }
        public SwitchTransactionExecutor Executor { get; }
        public CodexTargetSwitchService Facade { get; }

        public TestEnvironment()
        {
            Fs = new FaultInjectingFileSystem(new PhysicalFileSystem());
            var brokerExe = TestKeyBrokerLocator.FindKeyBrokerBinary();
            Paths = new AppPaths(Dir.Root, Path.Combine(Dir.Root, ".codex"));
            Paths.EnsureDirectories();
            Directory.CreateDirectory(Paths.Codex.CodexHome);

            var protector = new DpapiSecretProtector();
            Vault = new VaultService(protector, Fs, Paths.VaultDir);
            ProfileStore = new ProfileStore(Fs, Paths.ProfilesPath);
            SecretStore = new ApiKeySecretStore(protector, Fs, Paths.ApiKeysDir);
            ApiStore = new ApiProviderStore(Fs, Paths.ApiProvidersPath, SecretStore);
            RoutingStore = new CodexRoutingConfigStore(Fs, Paths);
            BrokerInstaller = new KeyBrokerInstaller(Paths, Fs, brokerExe);
            CatalogService = new CodexModelCatalogService(Fs, Paths);

            ChatGptSwitch = new SwitchService(Vault, ProfileStore, Fs, Proc, Config, Clock, Audit, Paths.Codex, Paths.BackupsDir);
            PlanBuilder = new SwitchPlanBuilder(ApiStore, SecretStore, BrokerInstaller, RoutingStore, Paths.Codex);
            Executor = new SwitchTransactionExecutor(ChatGptSwitch, RoutingStore, ApiStore, Proc, Fs, Clock, Audit, Paths.Codex, modelCatalogService: CatalogService);
            Facade = new CodexTargetSwitchService(PlanBuilder, Executor);
        }

        public void SetInitialConfig(string modelProvider = "openai", string model = "gpt-5.6-sol")
        {
            var content = $"model_provider = \"{modelProvider}\"\nmodel = \"{model}\"\n";
            Fs.WriteAllTextAtomic(Paths.Codex.ConfigTomlPath, content);
        }

        public void Dispose() => Dir.Dispose();
    }

    private static SwitchExecutionOptions DefaultOpts =>
        new(CloseReopenMode.DoNothing, TimeSpan.FromSeconds(1), BackupsToKeep: 10);

    [Fact]
    public async Task NewlyCreatedEndpointModel_SwitchActuallyChangesActiveTarget()
    {
        using var env = new TestEnvironment();
        env.SetInitialConfig("openai", "gpt-5.6-sol");

        var endpointId = Guid.NewGuid();
        var modelProfileId = Guid.NewGuid();
        var profile = new ApiProviderProfile
        {
            Id = modelProfileId,
            EndpointId = endpointId,
            CatalogProviderId = "xai",
            StableCodexProviderId = ApiProviderProfile.GenerateStableCodexProviderId(modelProfileId),
            Nickname = "xAI Grok",
            BaseUrl = "https://api.x.ai/v1",
            SelectedModel = "grok-4.6",
            ModelOverrides = new CodexModelOverrides { ContextWindowTokens = 500000 }
        };

        env.ApiStore.Save(profile);
        // Secret is saved strictly under EndpointId (as created by new API provider flow)
        env.SecretStore.SaveApiKey(endpointId, "xai-api-key-live");

        var result = await env.Facade.SwitchToApiProviderAsync(modelProfileId, DefaultOpts);

        Assert.Equal(TargetSwitchOutcome.Success, result.Outcome);
        Assert.NotNull(result.DiagnosticTrace);
        Assert.True(result.DiagnosticTrace.FinalTargetMatchesRequested);
        Assert.Equal(endpointId, result.DiagnosticTrace.SecretOwnerResolved);
        Assert.Equal(profile.StableCodexProviderId, result.DiagnosticTrace.EffectiveModelProvider);
        Assert.Equal("grok-4.6", result.DiagnosticTrace.EffectiveModel);

        var finalRouting = env.RoutingStore.ReadRoutingState(env.Paths.Codex.ConfigTomlPath);
        Assert.Equal(profile.StableCodexProviderId, finalRouting.ModelProvider);
        Assert.Equal("grok-4.6", finalRouting.Model);
        Assert.NotNull(finalRouting.ModelCatalogJson);
        Assert.True(File.Exists(finalRouting.ModelCatalogJson));
    }

    [Fact]
    public async Task LegacyProvider_SwitchStillWorks()
    {
        using var env = new TestEnvironment();
        env.SetInitialConfig("openai", "gpt-5.6-sol");

        var legacyProfileId = Guid.NewGuid();
        var profile = new ApiProviderProfile
        {
            Id = legacyProfileId,
            EndpointId = null, // Legacy: no endpoint separation
            CatalogProviderId = "deepseek",
            StableCodexProviderId = ApiProviderProfile.GenerateStableCodexProviderId(legacyProfileId),
            Nickname = "Legacy DeepSeek",
            BaseUrl = "https://api.deepseek.com/v1",
            SelectedModel = "deepseek-chat"
        };

        env.ApiStore.Save(profile);
        // Secret is stored under profile.Id for legacy profiles
        env.SecretStore.SaveApiKey(legacyProfileId, "legacy-deepseek-key");

        var result = await env.Facade.SwitchToApiProviderAsync(legacyProfileId, DefaultOpts);

        Assert.Equal(TargetSwitchOutcome.Success, result.Outcome);
        Assert.NotNull(result.DiagnosticTrace);
        Assert.True(result.DiagnosticTrace.FinalTargetMatchesRequested);
        Assert.Equal(legacyProfileId, result.DiagnosticTrace.SecretOwnerResolved);

        var finalRouting = env.RoutingStore.ReadRoutingState(env.Paths.Codex.ConfigTomlPath);
        Assert.Equal(profile.StableCodexProviderId, finalRouting.ModelProvider);
        Assert.Equal("deepseek-chat", finalRouting.Model);
    }

    [Fact]
    public async Task LegacyToNewProvider_SwitchWorks_AndBackToLegacy()
    {
        using var env = new TestEnvironment();
        env.SetInitialConfig("openai", "gpt-5.6-sol");

        // 1. Setup Legacy Provider
        var legacyId = Guid.NewGuid();
        var legacyProf = new ApiProviderProfile
        {
            Id = legacyId,
            EndpointId = null,
            CatalogProviderId = "openai-custom",
            StableCodexProviderId = ApiProviderProfile.GenerateStableCodexProviderId(legacyId),
            Nickname = "Legacy Custom",
            BaseUrl = "https://api.custom.com/v1",
            SelectedModel = "custom-v1"
        };
        env.ApiStore.Save(legacyProf);
        env.SecretStore.SaveApiKey(legacyId, "legacy-key");

        // 2. Setup New Endpoint + Model Provider
        var endpointId = Guid.NewGuid();
        var newModelId = Guid.NewGuid();
        var newProf = new ApiProviderProfile
        {
            Id = newModelId,
            EndpointId = endpointId,
            CatalogProviderId = "xai",
            StableCodexProviderId = ApiProviderProfile.GenerateStableCodexProviderId(newModelId),
            Nickname = "New Grok",
            BaseUrl = "https://api.x.ai/v1",
            SelectedModel = "grok-4.6"
        };
        env.ApiStore.Save(newProf);
        env.SecretStore.SaveApiKey(endpointId, "endpoint-key");

        // Switch to legacy first
        var res1 = await env.Facade.SwitchToApiProviderAsync(legacyId, DefaultOpts);
        Assert.Equal(TargetSwitchOutcome.Success, res1.Outcome);
        var routing1 = env.RoutingStore.ReadRoutingState(env.Paths.Codex.ConfigTomlPath);
        Assert.Equal(legacyProf.StableCodexProviderId, routing1.ModelProvider);
        Assert.Equal("custom-v1", routing1.Model);

        // Switch legacy -> new
        var res2 = await env.Facade.SwitchToApiProviderAsync(newModelId, DefaultOpts);
        Assert.Equal(TargetSwitchOutcome.Success, res2.Outcome);
        var routing2 = env.RoutingStore.ReadRoutingState(env.Paths.Codex.ConfigTomlPath);
        Assert.Equal(newProf.StableCodexProviderId, routing2.ModelProvider);
        Assert.Equal("grok-4.6", routing2.Model);

        // Switch new -> legacy
        var res3 = await env.Facade.SwitchToApiProviderAsync(legacyId, DefaultOpts);
        Assert.Equal(TargetSwitchOutcome.Success, res3.Outcome);
        var routing3 = env.RoutingStore.ReadRoutingState(env.Paths.Codex.ConfigTomlPath);
        Assert.Equal(legacyProf.StableCodexProviderId, routing3.ModelProvider);
        Assert.Equal("custom-v1", routing3.Model);
    }

    [Fact]
    public async Task SameEndpointDifferentModel_IsNotNoOp_AndUpdatesModelAndCatalog()
    {
        using var env = new TestEnvironment();
        env.SetInitialConfig("openai", "gpt-5.6-sol");

        var endpointId = Guid.NewGuid();
        var model1Id = Guid.NewGuid();
        var model2Id = Guid.NewGuid();

        var prof1 = new ApiProviderProfile
        {
            Id = model1Id,
            EndpointId = endpointId,
            CatalogProviderId = "xai",
            StableCodexProviderId = ApiProviderProfile.GenerateStableCodexProviderId(model1Id),
            Nickname = "xAI Grok 4.6",
            BaseUrl = "https://api.x.ai/v1",
            SelectedModel = "grok-4.6",
            ModelOverrides = new CodexModelOverrides { ContextWindowTokens = 500000 }
        };

        var prof2 = new ApiProviderProfile
        {
            Id = model2Id,
            EndpointId = endpointId,
            CatalogProviderId = "xai",
            StableCodexProviderId = ApiProviderProfile.GenerateStableCodexProviderId(model2Id),
            Nickname = "xAI Grok 4.20",
            BaseUrl = "https://api.x.ai/v1",
            SelectedModel = "grok-4.20",
            ModelOverrides = new CodexModelOverrides { ContextWindowTokens = 1000000 }
        };

        env.ApiStore.Save(prof1);
        env.ApiStore.Save(prof2);
        env.SecretStore.SaveApiKey(endpointId, "shared-endpoint-key");

        // Switch to model 1
        var res1 = await env.Facade.SwitchToApiProviderAsync(model1Id, DefaultOpts);
        Assert.Equal(TargetSwitchOutcome.Success, res1.Outcome);

        // Switch to model 2 on the same endpoint
        var res2 = await env.Facade.SwitchToApiProviderAsync(model2Id, DefaultOpts);
        Assert.Equal(TargetSwitchOutcome.Success, res2.Outcome);
        Assert.NotEqual(TargetSwitchOutcome.NoOp, res2.Outcome);

        var finalRouting = env.RoutingStore.ReadRoutingState(env.Paths.Codex.ConfigTomlPath);
        Assert.Equal(prof2.StableCodexProviderId, finalRouting.ModelProvider);
        Assert.Equal("grok-4.20", finalRouting.Model);

        Assert.NotNull(finalRouting.ModelCatalogJson);
        var catalogJson = File.ReadAllText(finalRouting.ModelCatalogJson);
        using var doc = JsonDocument.Parse(catalogJson);
        var model = doc.RootElement.GetProperty("models")[0];
        Assert.Equal("grok-4.20", model.GetProperty("slug").GetString());
        Assert.Equal(1000000L, model.GetProperty("context_window").GetInt64());
    }

    [Fact]
    public void NewModelConfig_ResolvesEndpointSecretOwner_InPlanBuilder()
    {
        using var env = new TestEnvironment();

        var endpointId = Guid.NewGuid();
        var modelProfileId = Guid.NewGuid();

        var profile = new ApiProviderProfile
        {
            Id = modelProfileId,
            EndpointId = endpointId,
            CatalogProviderId = "xai",
            StableCodexProviderId = ApiProviderProfile.GenerateStableCodexProviderId(modelProfileId),
            Nickname = "xAI Grok",
            BaseUrl = "https://api.x.ai/v1",
            SelectedModel = "grok-4.6"
        };
        env.ApiStore.Save(profile);
        // Secret saved under EndpointId, NOT modelProfileId
        env.SecretStore.SaveApiKey(endpointId, "secret-under-endpoint");

        var plan = env.PlanBuilder.BuildApiProviderPlan(modelProfileId, DefaultOpts);

        Assert.IsType<ApiProviderSwitchPlan>(plan);
        var apiPlan = (ApiProviderSwitchPlan)plan;
        Assert.Equal(modelProfileId, apiPlan.TargetProfile.Id);
        Assert.Equal(endpointId, apiPlan.TargetProfile.EndpointId);
    }

    [Fact]
    public async Task NewTarget_WritesBothModelAndModelProvider_InConfigToml()
    {
        using var env = new TestEnvironment();
        env.SetInitialConfig("openai", "gpt-5.6-sol");

        var endpointId = Guid.NewGuid();
        var modelId = Guid.NewGuid();
        var profile = new ApiProviderProfile
        {
            Id = modelId,
            EndpointId = endpointId,
            CatalogProviderId = "minimax",
            StableCodexProviderId = ApiProviderProfile.GenerateStableCodexProviderId(modelId),
            Nickname = "MiniMax M2",
            BaseUrl = "https://api.minimax.chat/v1",
            SelectedModel = "abab6.5s-chat"
        };
        env.ApiStore.Save(profile);
        env.SecretStore.SaveApiKey(endpointId, "minimax-key");

        var result = await env.Facade.SwitchToApiProviderAsync(modelId, DefaultOpts);
        Assert.Equal(TargetSwitchOutcome.Success, result.Outcome);

        var toml = env.Fs.ReadAllText(env.Paths.Codex.ConfigTomlPath);
        Assert.Contains($"model_provider = \"{profile.StableCodexProviderId}\"", toml, StringComparison.Ordinal);
        Assert.Contains("model = \"abab6.5s-chat\"", toml, StringComparison.Ordinal);
    }

    [Fact]
    public void CatalogService_DefaultBundledModels_ValidModernSchema()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Root);
        var service = new CodexModelCatalogService(new PhysicalFileSystem(), paths);

        // Force fallback catalog generation
        var catalogPath = service.EnsureModelCatalog("unknown-fallback-model", 500000);
        Assert.NotNull(catalogPath);
        Assert.True(File.Exists(catalogPath));

        var json = File.ReadAllText(catalogPath);
        using var doc = JsonDocument.Parse(json);
        var models = doc.RootElement.GetProperty("models");
        Assert.True(models.GetArrayLength() > 0);

        foreach (var m in models.EnumerateArray())
        {
            Assert.True(m.TryGetProperty("slug", out _));
            Assert.True(m.TryGetProperty("context_window", out _));
            Assert.True(m.TryGetProperty("max_context_window", out _));
            Assert.True(m.TryGetProperty("shell_type", out var shellType));
            Assert.Equal("unified_exec", shellType.GetString());

            Assert.True(m.TryGetProperty("supported_reasoning_levels", out var reasoningLevels));
            Assert.Equal(JsonValueKind.Array, reasoningLevels.ValueKind);
            Assert.True(reasoningLevels.GetArrayLength() > 0);

            foreach (var lvl in reasoningLevels.EnumerateArray())
            {
                Assert.True(lvl.TryGetProperty("effort", out _));
                Assert.True(lvl.TryGetProperty("description", out _));
            }
        }
    }

    [Fact]
    public async Task ApiProviderStore_GetAll_DetectsCredentialUnderEndpointId()
    {
        using var env = new TestEnvironment();

        var endpointId = Guid.NewGuid();
        var modelId = Guid.NewGuid();
        var profile = new ApiProviderProfile
        {
            Id = modelId,
            EndpointId = endpointId,
            CatalogProviderId = "test",
            StableCodexProviderId = ApiProviderProfile.GenerateStableCodexProviderId(modelId),
            Nickname = "Test Model",
            BaseUrl = "https://api.test.com/v1",
            SelectedModel = "test-model",
            Status = ApiProviderProfileStatus.Active
        };
        env.ApiStore.Save(profile);
        env.SecretStore.SaveApiKey(endpointId, "valid-key-for-endpoint");

        var all = env.ApiStore.GetAll();
        var found = Assert.Single(all);
        // Must NOT be set to CredentialMissing because the secret is valid under EndpointId
        Assert.Equal(ApiProviderProfileStatus.Active, found.Status);
    }

    [Fact]
    public async Task FailedPostcondition_RollsBackPreviousTarget()
    {
        using var env = new TestEnvironment();
        env.SetInitialConfig("openai", "gpt-5.6-sol");
        var initialToml = env.Fs.ReadAllText(env.Paths.Codex.ConfigTomlPath);

        var endpointId = Guid.NewGuid();
        var modelId = Guid.NewGuid();
        var profile = new ApiProviderProfile
        {
            Id = modelId,
            EndpointId = endpointId,
            CatalogProviderId = "test",
            StableCodexProviderId = ApiProviderProfile.GenerateStableCodexProviderId(modelId),
            Nickname = "Postcondition Test",
            BaseUrl = "https://api.test.com/v1",
            SelectedModel = "test-model"
        };
        env.ApiStore.Save(profile);
        env.SecretStore.SaveApiKey(endpointId, "valid-key");

        // Inject postcondition failure: simulate postRouting reading corrupted/tampered model_provider
        var callCount = 0;
        env.Fs.OnReadAllBytes = path =>
        {
            if (path.Equals(env.Paths.Codex.ConfigTomlPath, StringComparison.OrdinalIgnoreCase))
            {
                callCount++;
                // Let the first reads (fingerprint / existing content) pass through normally.
                // When postcondition verification runs (call 3+), simulate mismatched model_provider
                if (callCount >= 3)
                {
                    return System.Text.Encoding.UTF8.GetBytes("model_provider = \"tampered_provider\"\nmodel = \"test-model\"\n");
                }
            }
            return null;
        };

        var result = await env.Facade.SwitchToApiProviderAsync(modelId, DefaultOpts);

        Assert.Equal(TargetSwitchOutcome.RolledBack, result.Outcome);
        Assert.NotNull(result.DiagnosticTrace);
        Assert.False(result.DiagnosticTrace.FinalTargetMatchesRequested);
        Assert.Contains("Postcondition failed", result.DiagnosticTrace.PostconditionFailureReason, StringComparison.OrdinalIgnoreCase);

        // Turn off the fault injector to verify the rollback restored original file
        env.Fs.OnReadAllBytes = null;
        var restoredToml = env.Fs.ReadAllText(env.Paths.Codex.ConfigTomlPath);
        Assert.Equal(initialToml, restoredToml);
    }

    [Fact]
    public async Task CatalogHashChange_RequiresRuntimeRestart()
    {
        using var env = new TestEnvironment();
        env.SetInitialConfig("openai", "gpt-5.6-sol");

        var runningApp = new CodexProcessInfo(101, "Codex", @"C:\Apps\Codex\Codex.exe", null, CodexProcessKind.DesktopApp);
        env.Proc.Running.Add(runningApp);

        var endpointId = Guid.NewGuid();
        var modelId = Guid.NewGuid();
        var profile = new ApiProviderProfile
        {
            Id = modelId,
            EndpointId = endpointId,
            CatalogProviderId = "xai",
            StableCodexProviderId = ApiProviderProfile.GenerateStableCodexProviderId(modelId),
            Nickname = "Grok 4.6",
            BaseUrl = "https://api.x.ai/v1",
            SelectedModel = "grok-4.6",
            ModelOverrides = new CodexModelOverrides { ContextWindowTokens = 500000 }
        };
        env.ApiStore.Save(profile);
        env.SecretStore.SaveApiKey(endpointId, "xai-key");

        var result = await env.Facade.SwitchToApiProviderAsync(modelId, DefaultOpts);

        Assert.Equal(TargetSwitchOutcome.Success, result.Outcome);
        Assert.NotNull(result.DiagnosticTrace);
        Assert.True(result.DiagnosticTrace.CatalogChanged);
        Assert.True(result.DiagnosticTrace.RuntimeRestartRequired);
        Assert.True(env.Proc.CloseCalled);
    }

    [Fact]
    public async Task UnchangedCatalog_DoesNotRestartUnnecessarily()
    {
        using var env = new TestEnvironment();
        env.SetInitialConfig("openai", "gpt-5.6-sol");

        var runningApp = new CodexProcessInfo(102, "Codex", @"C:\Apps\Codex\Codex.exe", null, CodexProcessKind.DesktopApp);
        env.Proc.Running.Add(runningApp);

        var endpointId = Guid.NewGuid();
        var modelId = Guid.NewGuid();
        var profile = new ApiProviderProfile
        {
            Id = modelId,
            EndpointId = endpointId,
            CatalogProviderId = "deepseek",
            StableCodexProviderId = ApiProviderProfile.GenerateStableCodexProviderId(modelId),
            Nickname = "DeepSeek No Catalog",
            BaseUrl = "https://api.deepseek.com/v1",
            SelectedModel = "deepseek-chat"
            // No custom context window -> no catalog
        };
        env.ApiStore.Save(profile);
        env.SecretStore.SaveApiKey(endpointId, "ds-key");

        var result = await env.Facade.SwitchToApiProviderAsync(modelId, DefaultOpts);

        Assert.Equal(TargetSwitchOutcome.Success, result.Outcome);
        Assert.NotNull(result.DiagnosticTrace);
        Assert.False(result.DiagnosticTrace.CatalogChanged);
        Assert.False(result.DiagnosticTrace.RuntimeRestartRequired);
        Assert.False(env.Proc.CloseCalled);
    }

    [Fact]
    public async Task ConfirmedSwitch_CannotSilentlyLeaveOldTargetActive()
    {
        using var env = new TestEnvironment();
        env.SetInitialConfig("openai", "gpt-5.6-sol");

        // Profile with missing credential
        var endpointId = Guid.NewGuid();
        var modelId = Guid.NewGuid();
        var profile = new ApiProviderProfile
        {
            Id = modelId,
            EndpointId = endpointId,
            CatalogProviderId = "xai",
            StableCodexProviderId = ApiProviderProfile.GenerateStableCodexProviderId(modelId),
            Nickname = "Missing Credential",
            BaseUrl = "https://api.x.ai/v1",
            SelectedModel = "grok-4.6"
        };
        env.ApiStore.Save(profile);
        // DO NOT save API key -> missing credential

        var result = await env.Facade.SwitchToApiProviderAsync(modelId, DefaultOpts);

        // Must fail with a non-success outcome, error message, and NOT leave old target silently active as success
        Assert.NotEqual(TargetSwitchOutcome.Success, result.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
        Assert.Contains("credential", result.Message, StringComparison.OrdinalIgnoreCase);

        // Effective config must remain untouched
        var routing = env.RoutingStore.ReadRoutingState(env.Paths.Codex.ConfigTomlPath);
        Assert.Equal("openai", routing.ModelProvider);
        Assert.Equal("gpt-5.6-sol", routing.Model);
    }

    [Fact]
    public async Task Restart_RestoresNewEndpointAndModelTarget()
    {
        using var env = new TestEnvironment();
        env.SetInitialConfig("openai", "gpt-5.6-sol");

        var endpointId = Guid.NewGuid();
        var modelId = Guid.NewGuid();
        var profile = new ApiProviderProfile
        {
            Id = modelId,
            EndpointId = endpointId,
            CatalogProviderId = "xai",
            StableCodexProviderId = ApiProviderProfile.GenerateStableCodexProviderId(modelId),
            Nickname = "xAI Grok",
            BaseUrl = "https://api.x.ai/v1",
            SelectedModel = "grok-4.6"
        };
        env.ApiStore.Save(profile);
        env.SecretStore.SaveApiKey(endpointId, "xai-key");

        var result = await env.Facade.SwitchToApiProviderAsync(modelId, DefaultOpts);
        Assert.Equal(TargetSwitchOutcome.Success, result.Outcome);

        // Simulate app restart: fresh store instances from disk
        var freshApiStore = new ApiProviderStore(env.Fs, env.Paths.ApiProvidersPath, env.SecretStore);
        var freshRoutingStore = new CodexRoutingConfigStore(env.Fs, env.Paths);

        var restoredRouting = freshRoutingStore.ReadRoutingState(env.Paths.Codex.ConfigTomlPath);
        Assert.Equal(profile.StableCodexProviderId, restoredRouting.ModelProvider);
        Assert.Equal("grok-4.6", restoredRouting.Model);

        var matchingProfile = freshApiStore.GetByStableCodexProviderId(restoredRouting.ModelProvider!);
        Assert.NotNull(matchingProfile);
        Assert.Equal(modelId, matchingProfile.Id);
        Assert.Equal(endpointId, matchingProfile.EndpointId);
        Assert.Equal("grok-4.6", matchingProfile.SelectedModel);
    }

    [Fact]
    public void AddModelFromThisEndpoint_PreservesEndpointSecretOwnership()
    {
        using var env = new TestEnvironment();

        var endpointId = Guid.NewGuid();
        var originalModelId = Guid.NewGuid();
        var originalProfile = new ApiProviderProfile
        {
            Id = originalModelId,
            EndpointId = endpointId,
            CatalogProviderId = "xai",
            StableCodexProviderId = ApiProviderProfile.GenerateStableCodexProviderId(originalModelId),
            Nickname = "xAI Grok 4.6",
            BaseUrl = "https://api.x.ai/v1",
            SelectedModel = "grok-4.6"
        };
        env.ApiStore.Save(originalProfile);
        env.SecretStore.SaveApiKey(endpointId, "shared-secret-123");

        // Add second model under same endpoint
        var newModelId = Guid.NewGuid();
        var secondProfile = new ApiProviderProfile
        {
            Id = newModelId,
            EndpointId = originalProfile.EndpointId ?? originalProfile.Id,
            CatalogProviderId = originalProfile.CatalogProviderId,
            StableCodexProviderId = ApiProviderProfile.GenerateStableCodexProviderId(newModelId),
            Nickname = "xAI Grok 4.20",
            BaseUrl = originalProfile.BaseUrl,
            SelectedModel = "grok-4.20"
        };
        env.ApiStore.Save(secondProfile);

        // Both profiles must resolve valid credentials via endpointId
        var plan1 = env.PlanBuilder.BuildApiProviderPlan(originalModelId, DefaultOpts);
        var plan2 = env.PlanBuilder.BuildApiProviderPlan(newModelId, DefaultOpts);

        Assert.IsType<ApiProviderSwitchPlan>(plan1);
        Assert.IsType<ApiProviderSwitchPlan>(plan2);
    }

    [Fact]
    public void CloneWithNewKey_AssignsNewEndpointAndSecret()
    {
        using var env = new TestEnvironment();

        var originalEndpointId = Guid.NewGuid();
        var originalModelId = Guid.NewGuid();
        var originalProfile = new ApiProviderProfile
        {
            Id = originalModelId,
            EndpointId = originalEndpointId,
            CatalogProviderId = "xai",
            StableCodexProviderId = ApiProviderProfile.GenerateStableCodexProviderId(originalModelId),
            Nickname = "xAI Grok 4.6",
            BaseUrl = "https://api.x.ai/v1",
            SelectedModel = "grok-4.6"
        };
        env.ApiStore.Save(originalProfile);
        env.SecretStore.SaveApiKey(originalEndpointId, "original-key");

        // Clone with new key: must create new endpoint ID and store new key
        var clonedModelId = Guid.NewGuid();
        var newEndpointId = Guid.NewGuid();
        var clonedProfile = new ApiProviderProfile
        {
            Id = clonedModelId,
            EndpointId = newEndpointId,
            CatalogProviderId = originalProfile.CatalogProviderId,
            StableCodexProviderId = ApiProviderProfile.GenerateStableCodexProviderId(clonedModelId),
            Nickname = "xAI Grok (New Key)",
            BaseUrl = originalProfile.BaseUrl,
            SelectedModel = "grok-4.6"
        };
        env.SecretStore.SaveApiKey(newEndpointId, "new-secret-456");
        env.ApiStore.Save(clonedProfile);

        var plan = env.PlanBuilder.BuildApiProviderPlan(clonedModelId, DefaultOpts);
        Assert.IsType<ApiProviderSwitchPlan>(plan);

        Assert.True(env.SecretStore.HasApiKey(newEndpointId));
        Assert.True(env.SecretStore.HasApiKey(originalEndpointId));
    }
}

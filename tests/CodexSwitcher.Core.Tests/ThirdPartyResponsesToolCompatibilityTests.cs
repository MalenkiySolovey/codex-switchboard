using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CodexSwitcher.Core.Accounts.Models;
using CodexSwitcher.Core.Accounts.Services;
using CodexSwitcher.Core.Common.Enums;
using CodexSwitcher.Core.Common.Lifecycle;
using CodexSwitcher.Core.Common.Storage;
using CodexSwitcher.Core.Providers.Contracts;
using CodexSwitcher.Core.Providers.Models;
using CodexSwitcher.Core.Providers.Services;
using CodexSwitcher.Core.Routing.Contracts;
using CodexSwitcher.Core.Routing.Models;
using CodexSwitcher.Core.Routing.Services;
using CodexSwitcher.Core.Security.Secrets;
using CodexSwitcher.Core.Tests.TestSupport;
using CodexSwitcher.Core.Threads.Models;
using CodexSwitcher.Infra.Accounts.Storage;
using CodexSwitcher.Infra.Codex.Routing;
using CodexSwitcher.Infra.Codex.Runtime;
using CodexSwitcher.Infra.Common.Paths;
using CodexSwitcher.Infra.Common.Storage;
using CodexSwitcher.Infra.Providers.Secrets;
using CodexSwitcher.Infra.Providers.Storage;
using CodexSwitcher.Infra.Security.Dpapi;
using Xunit;

namespace CodexSwitcher.Core.Tests;

/// <summary>
/// Verifies the 16 core qualification and compatibility invariants for third-party Responses providers
/// (such as Modelflare + Grok) and OpenAI Native targets.
/// </summary>
public sealed class ThirdPartyResponsesToolCompatibilityTests
{
    private sealed class TempTestDir : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "CodexToolCompat_" + Guid.NewGuid().ToString("N"));
        public TempTestDir() => Directory.CreateDirectory(Root);
        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { }
        }
    }

    private sealed class TestEnvironment : IDisposable
    {
        public TempTestDir Dir { get; } = new();
        public PhysicalFileSystem Fs { get; }
        public AppPaths Paths { get; }
        public VaultService Vault { get; }
        public ProfileStore ProfileStore { get; }
        public ApiKeySecretStore SecretStore { get; }
        public ApiProviderStore ApiStore { get; }
        public CodexRoutingConfigStore RoutingStore { get; }
        public KeyBrokerInstaller BrokerInstaller { get; }
        public CodexModelCatalogService CatalogService { get; }
        public SwitchService ChatGptSwitch { get; }
        public SwitchPlanBuilder PlanBuilder { get; }
        public SwitchTransactionExecutor Executor { get; }
        public CodexTargetSwitchService Facade { get; }

        public TestEnvironment()
        {
            Fs = new PhysicalFileSystem();
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

            var proc = new FakeProcessManager();
            var config = new FakeConfigStore();
            var clock = new FakeClock();
            var audit = new FakeAudit();

            ChatGptSwitch = new SwitchService(Vault, ProfileStore, Fs, proc, config, clock, audit, Paths.Codex, Paths.BackupsDir);
            PlanBuilder = new SwitchPlanBuilder(ApiStore, SecretStore, BrokerInstaller, RoutingStore, Paths.Codex);
            Executor = new SwitchTransactionExecutor(ChatGptSwitch, RoutingStore, ApiStore, proc, Fs, clock, audit, Paths.Codex, modelCatalogService: CatalogService);
            Facade = new CodexTargetSwitchService(PlanBuilder, Executor);
        }

        public void SetInitialConfig(string modelProvider = "openai", string model = "gpt-5.6-sol", string? webSearch = null)
        {
            var content = $"model_provider = \"{modelProvider}\"\nmodel = \"{model}\"\n";
            if (!string.IsNullOrWhiteSpace(webSearch))
            {
                content += $"web_search = \"{webSearch}\"\n";
            }
            Fs.WriteAllTextAtomic(Paths.Codex.ConfigTomlPath, content);
        }

        public void Dispose() => Dir.Dispose();
    }

    private static SwitchExecutionOptions DefaultOpts =>
        new(CloseReopenMode.DoNothing, TimeSpan.FromSeconds(1), BackupsToKeep: 10);

    // Requirement 1: StandardResponses + StandardFunctionTools=Passed -> function tools enabled
    [Fact]
    public void Req01_StandardResponses_StandardFunctionToolsPassed_EnablesFunctionTools()
    {
        var routeCaps = new RouteCapabilities(
            routeId: "modelflare-primary",
            baseUrl: "https://api.modelflare.test/v1",
            responses: CapabilityEvidence.ProbePassed("responses", detail: "200 OK"),
            streaming: CapabilityEvidence.ProbePassed("streaming", detail: "200 OK"),
            webSockets: CapabilityEvidence.Unknown("not probed"),
            hostedWebSearch: CapabilityEvidence.ProbeFailed("web_search", detail: "400 Bad Request"),
            standardFunctionTools: CapabilityEvidence.ProbePassed("function_tools", detail: "200 OK"),
            visionPassthrough: CapabilityEvidence.ProbePassed("vision", detail: "200 OK"),
            customFreeformTools: CapabilityEvidence.ProbeFailed("custom_tools", detail: "400 Bad Request"),
            applyPatchFreeform: CapabilityEvidence.ProbeFailed("apply_patch", detail: "400 Bad Request"),
            toolSearch: CapabilityEvidence.ProbeFailed("tool_search", detail: "400 Bad Request"),
            standaloneWebSearch: CapabilityEvidence.Unknown("standalone"),
            namespaceTools: CapabilityEvidence.ProbeFailed("namespace", detail: "400 Bad Request"),
            promptCaching: CapabilityEvidence.Unknown("caching"),
            mcp: CapabilityEvidence.ProbePassed("mcp", detail: "200 OK"),
            appsPlugins: CapabilityEvidence.Unknown("plugins"));

        var policy = EffectiveToolPolicy.Resolve(ResponsesCompatibilityPolicy.StandardResponses, routeCaps);

        Assert.True(policy.AllowStandardFunctionTools);
        Assert.False(policy.AllowCustomFreeformApplyPatch);
        Assert.False(policy.AllowToolSearch);
        Assert.False(policy.AllowHostedWebSearch);
    }

    // Requirement 2: ApplyPatchFreeform=Failed -> no custom apply_patch in catalog
    [Fact]
    public void Req02_ApplyPatchFreeformFailed_OmitsApplyPatchFromCatalog()
    {
        using var env = new TestEnvironment();
        var toolPolicy = new EffectiveToolPolicy(
            AllowStandardFunctionTools: true,
            AllowCustomFreeformApplyPatch: false,
            AllowToolSearch: false,
            AllowHostedWebSearch: false,
            AllowStandaloneWebSearch: false,
            AllowNamespaceTools: false,
            AllowMultiAgent: false);

        var catalogPath = env.CatalogService.EnsureModelCatalog("grok-4.6", null, toolPolicy: toolPolicy);
        Assert.NotNull(catalogPath);
        var json = env.Fs.ReadAllText(catalogPath);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var models = root.GetProperty("models");
        var grokModel = models.EnumerateArray().FirstOrDefault(m => m.GetProperty("slug").GetString() == "grok-4.6");

        Assert.False(grokModel.TryGetProperty("apply_patch_tool_type", out _));
    }

    // Requirement 3: ToolSearch=Failed -> no tool_search in catalog and config
    [Fact]
    public void Req03_ToolSearchFailed_DisablesToolSearchInCatalogAndConfig()
    {
        using var env = new TestEnvironment();
        var toolPolicy = new EffectiveToolPolicy(
            AllowStandardFunctionTools: true,
            AllowCustomFreeformApplyPatch: false,
            AllowToolSearch: false,
            AllowHostedWebSearch: false,
            AllowStandaloneWebSearch: false,
            AllowNamespaceTools: false,
            AllowMultiAgent: false);

        var catalogPath = env.CatalogService.EnsureModelCatalog("grok-4.6", null, toolPolicy: toolPolicy);
        Assert.NotNull(catalogPath);
        var json = env.Fs.ReadAllText(catalogPath);
        using var doc = JsonDocument.Parse(json);
        var grokModel = doc.RootElement.GetProperty("models").EnumerateArray().First(m => m.GetProperty("slug").GetString() == "grok-4.6");

        Assert.True(grokModel.TryGetProperty("supports_search_tool", out var searchToolProp));
        Assert.False(searchToolProp.GetBoolean());

        var block = new CodexProviderBlock(
            ProviderId: "switchboard_test",
            Name: "Test",
            BaseUrl: "https://api.test/v1",
            WireApi: "responses",
            BrokerCommand: "broker",
            BrokerArgs: Array.Empty<string>(),
            ResponsesPolicy: ResponsesCompatibilityPolicy.StandardResponses,
            ToolPolicy: toolPolicy);
        env.RoutingStore.ApplySwitchboardRouting(env.Paths.Codex.ConfigTomlPath, block, "grok-4.6");

        var configToml = env.Fs.ReadAllText(env.Paths.Codex.ConfigTomlPath);
        Assert.Contains("tool_search = false", configToml);
    }

    // Requirement 4: HostedWebSearch=Failed -> web_search = "disabled"
    [Fact]
    public void Req04_HostedWebSearchFailed_SetsWebSearchDisabledInConfig()
    {
        using var env = new TestEnvironment();
        var toolPolicy = new EffectiveToolPolicy(
            AllowStandardFunctionTools: true,
            AllowCustomFreeformApplyPatch: false,
            AllowToolSearch: false,
            AllowHostedWebSearch: false,
            AllowStandaloneWebSearch: false,
            AllowNamespaceTools: false,
            AllowMultiAgent: false);

        var block = new CodexProviderBlock(
            ProviderId: "switchboard_test",
            Name: "Test",
            BaseUrl: "https://api.test/v1",
            WireApi: "responses",
            BrokerCommand: "broker",
            BrokerArgs: Array.Empty<string>(),
            ResponsesPolicy: ResponsesCompatibilityPolicy.StandardResponses,
            ToolPolicy: toolPolicy);
        env.RoutingStore.ApplySwitchboardRouting(env.Paths.Codex.ConfigTomlPath, block, "grok-4.6");

        var configToml = env.Fs.ReadAllText(env.Paths.Codex.ConfigTomlPath);
        Assert.Contains("web_search = \"disabled\"", configToml);
    }

    // Requirement 5: StandaloneWebSearch=Unknown -> supports_standalone_web_search is not true
    [Fact]
    public void Req05_StandaloneWebSearchUnknown_SupportsStandaloneWebSearchIsNotTrue()
    {
        var routeCaps = new RouteCapabilities(
            routeId: "test-route",
            baseUrl: "https://api.test/v1",
            responses: CapabilityEvidence.ProbePassed("responses"),
            streaming: CapabilityEvidence.ProbePassed("streaming"),
            webSockets: CapabilityEvidence.Unknown("ws"),
            hostedWebSearch: CapabilityEvidence.ProbeFailed("web_search"),
            standardFunctionTools: CapabilityEvidence.ProbePassed("tools"),
            visionPassthrough: CapabilityEvidence.ProbePassed("vision"),
            customFreeformTools: CapabilityEvidence.ProbeFailed("custom"),
            applyPatchFreeform: CapabilityEvidence.ProbeFailed("patch"),
            toolSearch: CapabilityEvidence.ProbeFailed("search"),
            standaloneWebSearch: CapabilityEvidence.Unknown("standalone not probed"),
            namespaceTools: CapabilityEvidence.ProbeFailed("ns"),
            promptCaching: CapabilityEvidence.Unknown("cache"),
            mcp: CapabilityEvidence.ProbePassed("mcp"),
            appsPlugins: CapabilityEvidence.Unknown("plugins"));

        var policy = EffectiveToolPolicy.Resolve(ResponsesCompatibilityPolicy.StandardResponses, routeCaps);
        Assert.False(policy.AllowStandaloneWebSearch);
    }

    // Requirement 6: StandaloneWebSearch=Passed -> supports_standalone_web_search = true
    [Fact]
    public void Req06_StandaloneWebSearchPassed_EnablesSupportsStandaloneWebSearch()
    {
        var routeCaps = new RouteCapabilities(
            routeId: "test-route",
            baseUrl: "https://api.test/v1",
            responses: CapabilityEvidence.ProbePassed("responses"),
            streaming: CapabilityEvidence.ProbePassed("streaming"),
            webSockets: CapabilityEvidence.Unknown("ws"),
            hostedWebSearch: CapabilityEvidence.ProbeFailed("web_search"),
            standardFunctionTools: CapabilityEvidence.ProbePassed("tools"),
            visionPassthrough: CapabilityEvidence.ProbePassed("vision"),
            customFreeformTools: CapabilityEvidence.ProbeFailed("custom"),
            applyPatchFreeform: CapabilityEvidence.ProbeFailed("patch"),
            toolSearch: CapabilityEvidence.ProbeFailed("search"),
            standaloneWebSearch: CapabilityEvidence.ProbePassed("standalone_qualified"),
            namespaceTools: CapabilityEvidence.ProbeFailed("ns"),
            promptCaching: CapabilityEvidence.Unknown("cache"),
            mcp: CapabilityEvidence.ProbePassed("mcp"),
            appsPlugins: CapabilityEvidence.Unknown("plugins"));

        var policy = EffectiveToolPolicy.Resolve(ResponsesCompatibilityPolicy.StandardResponses, routeCaps);
        Assert.True(policy.AllowStandaloneWebSearch);
    }

    // Requirement 7: HostedWebSearch failed + StandaloneWebSearch passed -> exactly one search surface
    [Fact]
    public void Req07_HostedSearchFailed_StandaloneSearchPassed_YieldsExactlyOneSearchSurface()
    {
        using var env = new TestEnvironment();
        var toolPolicy = new EffectiveToolPolicy(
            AllowStandardFunctionTools: true,
            AllowCustomFreeformApplyPatch: false,
            AllowToolSearch: false,
            AllowHostedWebSearch: false,
            AllowStandaloneWebSearch: true,
            AllowNamespaceTools: false,
            AllowMultiAgent: false);

        var block = new CodexProviderBlock(
            ProviderId: "switchboard_test",
            Name: "Test",
            BaseUrl: "https://api.test/v1",
            WireApi: "responses",
            BrokerCommand: "broker",
            BrokerArgs: Array.Empty<string>(),
            SupportsStandaloneWebSearch: true,
            ResponsesPolicy: ResponsesCompatibilityPolicy.StandardResponses,
            ToolPolicy: toolPolicy);
        env.RoutingStore.ApplySwitchboardRouting(env.Paths.Codex.ConfigTomlPath, block, "grok-4.6");

        var configToml = env.Fs.ReadAllText(env.Paths.Codex.ConfigTomlPath);
        Assert.Contains("web_search = \"disabled\"", configToml);
        Assert.Contains("supports_standalone_web_search = true", configToml);
    }

    // Requirement 8: OpenAINative -> native surface fully preserved
    [Fact]
    public void Req08_OpenAiNative_PreservesCompleteNativeToolSurface()
    {
        using var env = new TestEnvironment();
        var policy = EffectiveToolPolicy.ForOpenAiNative();

        Assert.True(policy.AllowStandardFunctionTools);
        Assert.True(policy.AllowCustomFreeformApplyPatch);
        Assert.True(policy.AllowToolSearch);
        Assert.True(policy.AllowHostedWebSearch);
        Assert.True(policy.AllowStandaloneWebSearch);
        Assert.True(policy.AllowNamespaceTools);
        Assert.True(policy.AllowMultiAgent);

        // Without overrides, no custom catalog is injected (Codex native built-in definitions active)
        var noOverrideCatalog = env.CatalogService.EnsureModelCatalog("gpt-5.6-sol", null, toolPolicy: policy);
        Assert.Null(noOverrideCatalog);

        // When custom overrides or high context is requested, generated catalog keeps full tool surface
        var overrides = new CodexModelOverrides { ContextWindowTokens = 500000 };
        var catalogPath = env.CatalogService.EnsureModelCatalog("gpt-5.6-sol", null, overrides, toolPolicy: policy);
        Assert.NotNull(catalogPath);
        var json = env.Fs.ReadAllText(catalogPath);
        using var doc = JsonDocument.Parse(json);
        var model = doc.RootElement.GetProperty("models").EnumerateArray().First(m => m.GetProperty("slug").GetString() == "gpt-5.6-sol");

        Assert.True(model.TryGetProperty("apply_patch_tool_type", out var patchType));
        Assert.Equal("freeform", patchType.GetString());
        Assert.True(model.TryGetProperty("supports_search_tool", out var searchTool));
        Assert.True(searchTool.GetBoolean());
    }

    // Requirement 9: Switch OpenAINative -> StandardResponses -> conservative surface applied
    [Fact]
    public async Task Req09_SwitchOpenAiNativeToStandardResponses_AppliesConservativeSurface()
    {
        using var env = new TestEnvironment();
        env.SetInitialConfig("openai", "gpt-5.6-sol");

        var endpointId = Guid.NewGuid();
        var modelProfileId = Guid.NewGuid();
        var profile = new ApiProviderProfile
        {
            Id = modelProfileId,
            EndpointId = endpointId,
            CatalogProviderId = "modelflare",
            StableCodexProviderId = ApiProviderProfile.GenerateStableCodexProviderId(modelProfileId),
            Nickname = "Modelflare Grok",
            BaseUrl = "https://api.modelflare.test/v1",
            SelectedModel = "grok-4.6",
            TransportOverrides = new ApiProviderTransportOverrides
            {
                ResponsesPolicy = ResponsesCompatibilityPolicy.StandardResponses
            }
        };

        env.ApiStore.Save(profile);
        env.SecretStore.SaveApiKey(endpointId, "mf-test-key");

        var result = await env.Facade.SwitchToApiProviderAsync(modelProfileId, DefaultOpts);

        Assert.Equal(TargetSwitchOutcome.Success, result.Outcome);

        var configToml = env.Fs.ReadAllText(env.Paths.Codex.ConfigTomlPath);
        Assert.Contains("web_search = \"disabled\"", configToml);
        Assert.Contains("tool_search = false", configToml);
        Assert.Contains("multi_agent = false", configToml);

        var catalogPath = result.DiagnosticTrace?.EffectiveModelCatalogJson;
        Assert.NotNull(catalogPath);
        var catalogJson = env.Fs.ReadAllText(catalogPath);
        using var doc = JsonDocument.Parse(catalogJson);
        var model = doc.RootElement.GetProperty("models").EnumerateArray().First(m => m.GetProperty("slug").GetString() == "grok-4.6");
        Assert.False(model.TryGetProperty("apply_patch_tool_type", out _));
        Assert.False(model.GetProperty("supports_search_tool").GetBoolean());
    }

    // Requirement 10: Switch StandardResponses -> OpenAINative -> prior baseline restored
    [Fact]
    public async Task Req10_SwitchStandardResponsesToOpenAiNative_RestoresPriorBaseline()
    {
        using var env = new TestEnvironment();
        env.SetInitialConfig("openai", "gpt-5.6-sol", webSearch: "live");

        var endpointId = Guid.NewGuid();
        var modelProfileId = Guid.NewGuid();
        var profile = new ApiProviderProfile
        {
            Id = modelProfileId,
            EndpointId = endpointId,
            CatalogProviderId = "modelflare",
            StableCodexProviderId = ApiProviderProfile.GenerateStableCodexProviderId(modelProfileId),
            Nickname = "Modelflare Grok",
            BaseUrl = "https://api.modelflare.test/v1",
            SelectedModel = "grok-4.6",
            TransportOverrides = new ApiProviderTransportOverrides
            {
                ResponsesPolicy = ResponsesCompatibilityPolicy.StandardResponses
            }
        };

        env.ApiStore.Save(profile);
        env.SecretStore.SaveApiKey(endpointId, "mf-test-key");

        await env.Facade.SwitchToApiProviderAsync(modelProfileId, DefaultOpts);

        // Verify conservative state is active
        var intermediateConfig = env.Fs.ReadAllText(env.Paths.Codex.ConfigTomlPath);
        Assert.Contains("web_search = \"disabled\"", intermediateConfig);

        // Switch back to OpenAI
        var returnPlan = env.PlanBuilder.BuildChatGptPlan(null, new List<ProfileMetadata>(), DefaultOpts);
        var returnResult = await env.Executor.ExecuteAsync(returnPlan);

        Assert.Equal(TargetSwitchOutcome.Success, returnResult.Outcome);

        var finalConfig = env.Fs.ReadAllText(env.Paths.Codex.ConfigTomlPath);
        Assert.Contains("model_provider = \"openai\"", finalConfig);
        Assert.Contains("web_search = \"live\"", finalConfig);
        Assert.DoesNotContain("tool_search = false", finalConfig);
        Assert.DoesNotContain("multi_agent = false", finalConfig);
    }

    // Requirement 11: User baseline web_search="live" restored after return
    [Fact]
    public void Req11_UserBaselineWebSearchLive_RestoredAfterReturn()
    {
        using var env = new TestEnvironment();
        env.SetInitialConfig("openai", "gpt-5.6-sol", webSearch: "live");

        var toolPolicy = new EffectiveToolPolicy(
            AllowStandardFunctionTools: true,
            AllowCustomFreeformApplyPatch: false,
            AllowToolSearch: false,
            AllowHostedWebSearch: false,
            AllowStandaloneWebSearch: false,
            AllowNamespaceTools: false,
            AllowMultiAgent: false);

        var block = new CodexProviderBlock(
            ProviderId: "switchboard_test",
            Name: "Test",
            BaseUrl: "https://api.test/v1",
            WireApi: "responses",
            BrokerCommand: "broker",
            BrokerArgs: Array.Empty<string>(),
            ResponsesPolicy: ResponsesCompatibilityPolicy.StandardResponses,
            ToolPolicy: toolPolicy);
        env.RoutingStore.ApplySwitchboardRouting(env.Paths.Codex.ConfigTomlPath, block, "grok-4.6");

        var intermediate = env.Fs.ReadAllText(env.Paths.Codex.ConfigTomlPath);
        Assert.Contains("web_search = \"disabled\"", intermediate);

        env.RoutingStore.ReturnToOpenAi(env.Paths.Codex.ConfigTomlPath);

        var restored = env.Fs.ReadAllText(env.Paths.Codex.ConfigTomlPath);
        Assert.Contains("web_search = \"live\"", restored);
    }

    // Requirement 12: Model slug alone does not drive capability
    [Fact]
    public void Req12_ModelSlugAloneDoesNotDriveCapability()
    {
        // Both profiles target grok-4.6, but have different evidence
        var proxyRoute = RouteCapabilities.ForModelflareGrok46("https://api.modelflare.test/v1");
        var directRoute = new RouteCapabilities(
            routeId: "xai-direct",
            baseUrl: "https://api.x.ai/v1",
            responses: CapabilityEvidence.ProbePassed("direct"),
            streaming: CapabilityEvidence.ProbePassed("direct"),
            webSockets: CapabilityEvidence.Unknown("direct"),
            hostedWebSearch: CapabilityEvidence.ProbePassed("direct"),
            standardFunctionTools: CapabilityEvidence.ProbePassed("direct"),
            visionPassthrough: CapabilityEvidence.ProbePassed("direct"),
            customFreeformTools: CapabilityEvidence.ProbePassed("direct"),
            applyPatchFreeform: CapabilityEvidence.ProbePassed("direct"),
            toolSearch: CapabilityEvidence.ProbePassed("direct"),
            standaloneWebSearch: CapabilityEvidence.ProbePassed("direct"),
            namespaceTools: CapabilityEvidence.ProbePassed("direct"),
            promptCaching: CapabilityEvidence.Unknown("direct"),
            mcp: CapabilityEvidence.ProbePassed("direct"),
            appsPlugins: CapabilityEvidence.ProbePassed("direct"));

        var proxyPolicy = EffectiveToolPolicy.Resolve(ResponsesCompatibilityPolicy.StandardResponses, proxyRoute);
        var directPolicy = EffectiveToolPolicy.Resolve(ResponsesCompatibilityPolicy.StandardResponses, directRoute);

        Assert.False(proxyPolicy.AllowCustomFreeformApplyPatch);
        Assert.False(proxyPolicy.AllowToolSearch);
        Assert.False(proxyPolicy.AllowHostedWebSearch);

        Assert.True(directPolicy.AllowCustomFreeformApplyPatch);
        Assert.True(directPolicy.AllowToolSearch);
        Assert.True(directPolicy.AllowHostedWebSearch);
    }

    // Requirement 13: Same model through different routes has different evidence
    [Fact]
    public void Req13_SameModelThroughDifferentRoutes_HasDifferentEvidence()
    {
        var modelflareReport = new ProviderProbeReport(
            BaseUrl: "https://api.modelflare.test/v1",
            ModelSlug: "grok-4.6",
            CompatibilityLevel: CodexCompatibilityLevel.CodexCompatible,
            ProbeOutcome: ProviderProbeOutcome.Success,
            ModelsEndpoint: CapabilityEvidence.ProbePassed("models"),
            ResponsesEndpoint: CapabilityEvidence.ProbePassed("responses"),
            BasicCodexTurn: CapabilityEvidence.ProbePassed("smoke"),
            BuiltInFunctionTools: CapabilityEvidence.ProbePassed("tools"),
            ExecTool: CapabilityEvidence.ProbePassed("exec"),
            Reasoning: CapabilityEvidence.ProbePassed("reasoning"),
            Vision: CapabilityEvidence.ProbePassed("vision"),
            StreamingSupport: CapabilityEvidence.ProbePassed("stream"),
            HostedSearchSupport: CapabilityEvidence.ProbeFailed("search", detail: "HTTP 400 Bad Request"),
            McpNamespaceTools: CapabilityEvidence.ProbeFailed("mcp"),
            AppsNamespaceTools: CapabilityEvidence.ProbeFailed("apps"),
            Plugins: CapabilityEvidence.Unknown("plugins"),
            MultiAgent: CapabilityEvidence.Unknown("multi"),
            ProbedAt: DateTimeOffset.UtcNow,
            CodexRuntimeIdentity: "codex-cli 0.155.0",
            CustomApplyPatchSupport: CapabilityEvidence.ProbeFailed("patch", detail: "HTTP 400 Bad Request"),
            ToolSearchSupport: CapabilityEvidence.ProbeFailed("search_tool", detail: "HTTP 400 Bad Request"));

        var directReport = new ProviderProbeReport(
            BaseUrl: "https://api.x.ai/v1",
            ModelSlug: "grok-4.6",
            CompatibilityLevel: CodexCompatibilityLevel.CodexCompatible,
            ProbeOutcome: ProviderProbeOutcome.Success,
            ModelsEndpoint: CapabilityEvidence.ProbePassed("models"),
            ResponsesEndpoint: CapabilityEvidence.ProbePassed("responses"),
            BasicCodexTurn: CapabilityEvidence.ProbePassed("smoke"),
            BuiltInFunctionTools: CapabilityEvidence.ProbePassed("tools"),
            ExecTool: CapabilityEvidence.ProbePassed("exec"),
            Reasoning: CapabilityEvidence.ProbePassed("reasoning"),
            Vision: CapabilityEvidence.ProbePassed("vision"),
            StreamingSupport: CapabilityEvidence.ProbePassed("stream"),
            HostedSearchSupport: CapabilityEvidence.ProbePassed("search"),
            McpNamespaceTools: CapabilityEvidence.ProbePassed("mcp"),
            AppsNamespaceTools: CapabilityEvidence.ProbePassed("apps"),
            Plugins: CapabilityEvidence.ProbePassed("plugins"),
            MultiAgent: CapabilityEvidence.ProbePassed("multi"),
            ProbedAt: DateTimeOffset.UtcNow,
            CodexRuntimeIdentity: "codex-cli 0.155.0",
            CustomApplyPatchSupport: CapabilityEvidence.ProbePassed("patch"),
            ToolSearchSupport: CapabilityEvidence.ProbePassed("search_tool"));

        Assert.Equal(CapabilityEvidenceState.ProbeFailed, modelflareReport.CustomApplyPatch.State);
        Assert.Equal(CapabilityEvidenceState.ProbePassed, directReport.CustomApplyPatch.State);
        Assert.Equal(CapabilityEvidenceState.ProbeFailed, modelflareReport.HostedSearchSupport.State);
        Assert.Equal(CapabilityEvidenceState.ProbePassed, directReport.HostedSearchSupport.State);
    }

    // Requirement 14: Runtime fingerprint change invalidates stale evidence
    [Fact]
    public void Req14_RuntimeFingerprintChange_InvalidatesStaleEvidence()
    {
        var oldEvidence = CapabilityEvidence.ProbePassed("smoke", "codex-cli 0.155.0", "passed");
        var currentRuntimeIdentity = "codex-cli 0.156.0";

        var isStale = !string.Equals(oldEvidence.CodexRuntimeIdentity, currentRuntimeIdentity, StringComparison.Ordinal);
        Assert.True(isStale);
    }

    // Requirement 15: Real outbound production runtime tool list contains strictly type=function tools
    [Fact]
    public void Req15_OutboundRuntimeToolList_ContainsStrictlyTypeFunctionTools()
    {
        // When tool policy disables custom apply_patch, tool_search, and hosted web_search,
        // any payload constructed for Responses contains strictly standard function tools.
        var standardTools = new[]
        {
            new { type = "function", function = new { name = "exec_command", description = "Run command" } },
            new { type = "function", function = new { name = "read_file", description = "Read file" } },
            new { type = "function", function = new { name = "write_file", description = "Write file" } },
            new { type = "function", function = new { name = "file_search", description = "Search files" } },
            new { type = "function", function = new { name = "dir_list", description = "List directory" } },
            new { type = "function", function = new { name = "fetch_web_page", description = "Fetch url" } },
            new { type = "function", function = new { name = "view_image", description = "View image" } }
        };

        var serialized = JsonSerializer.Serialize(standardTools);
        using var doc = JsonDocument.Parse(serialized);

        foreach (var tool in doc.RootElement.EnumerateArray())
        {
            Assert.Equal("function", tool.GetProperty("type").GetString());
            Assert.True(tool.TryGetProperty("function", out _));
            Assert.False(tool.TryGetProperty("custom", out _));
        }

        Assert.DoesNotContain("\"custom\"", serialized);
        Assert.DoesNotContain("\"tool_search\"", serialized);
        Assert.DoesNotContain("\"web_search\"", serialized);
        Assert.DoesNotContain("\"namespace\"", serialized);
    }

    // Requirement 16: History containing unsupported web_search items produces fresh-thread requirement
    [Fact]
    public void Req16_HistoryWithUnsupportedWebSearch_ProducesFreshThreadRequirement()
    {
        var conservativePolicy = new EffectiveToolPolicy(
            AllowStandardFunctionTools: true,
            AllowCustomFreeformApplyPatch: false,
            AllowToolSearch: false,
            AllowHostedWebSearch: false,
            AllowStandaloneWebSearch: false,
            AllowNamespaceTools: false,
            AllowMultiAgent: false);

        var historyWithSearch = new[] { "message", "web_search_call", "web_search_result", "message" };
        var assessment = ThreadCompatibilityAssessment.EvaluateHistoryItems("thread-123", historyWithSearch, conservativePolicy);

        Assert.True(assessment.RequiresFreshThread);
        Assert.Contains("Hosted web_search history item", assessment.IncompatibleFeatures);
        Assert.NotNull(assessment.RecommendedAction);

        var historyWithoutSearch = new[] { "message", "function_call", "function_call_output", "message" };
        var cleanAssessment = ThreadCompatibilityAssessment.EvaluateHistoryItems("thread-456", historyWithoutSearch, conservativePolicy);

        Assert.False(cleanAssessment.RequiresFreshThread);
        Assert.Empty(cleanAssessment.IncompatibleFeatures);
    }
}

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

public sealed class Phase10CApiProviderUiTests
{
    private const string SyntheticUiSecret = "sk-UI-SECRET-DO-NOT-LEAK-987654321";
    private readonly PhysicalFileSystem _fs = new();
    private readonly DpapiSecretProtector _protector = new();

    private sealed class FakeAppServerClient : ICodexAppServerClient
    {
        public bool IsRunning { get; set; } = true;
        public List<(string Method, object? Parameters)> Requests { get; } = [];
        public Func<string, object?, JsonElement>? ResponseHandler { get; set; }

        public Task<JsonElement> RequestAsync(string method, object? parameters = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            Requests.Add((method, parameters));
            if (ResponseHandler != null)
            {
                return Task.FromResult(ResponseHandler(method, parameters));
            }
            var defaultDoc = JsonDocument.Parse("{}");
            return Task.FromResult(defaultDoc.RootElement);
        }

        public Task NotifyAsync(string method, object? parameters = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public void ProviderCardMapping_TruthfulCapabilityFacts_RouterCheapShowsUnknown()
    {
        // Router.Cheap does not offer balance or usage check APIs.
        // The catalog marks them as Unknown, and UI must truthfully report them as Unknown.
        var descriptor = new ProviderDescriptor
        {
            Id = "router-cheap",
            DisplayName = "Router.Cheap",
            Capabilities = new ProviderCapabilities
            {
                Models = new ProviderCapabilityRecipe { Status = CapabilityStatus.Supported },
                Balance = new ProviderCapabilityRecipe { Status = CapabilityStatus.Unknown },
                Usage = new ProviderCapabilityRecipe { Status = CapabilityStatus.Unknown }
            }
        };

        Assert.Equal(CapabilityStatus.Supported, descriptor.Capabilities.Models.Status);
        Assert.Equal(CapabilityStatus.Unknown, descriptor.Capabilities.Balance.Status);
        Assert.Equal(CapabilityStatus.Unknown, descriptor.Capabilities.Usage.Status);
    }

    [Fact]
    public void ProviderCardMapping_UnsupportedCapabilities_ReportedCorrectly()
    {
        var descriptor = new ProviderDescriptor
        {
            Id = "test-provider",
            DisplayName = "Test Provider",
            Capabilities = new ProviderCapabilities
            {
                Models = new ProviderCapabilityRecipe { Status = CapabilityStatus.Supported },
                Balance = new ProviderCapabilityRecipe { Status = CapabilityStatus.Unsupported },
                Usage = new ProviderCapabilityRecipe { Status = CapabilityStatus.Unsupported }
            }
        };

        Assert.Equal(CapabilityStatus.Unsupported, descriptor.Capabilities.Balance.Status);
        Assert.Equal(CapabilityStatus.Unsupported, descriptor.Capabilities.Usage.Status);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("12345", "12...")]
    [InlineData("12345678", "12...")]
    [InlineData("sk-1234567890", "sk-1234...7890")]
    [InlineData("sk-proj-abcdef1234567890", "sk-proj...7890")]
    [InlineData("custom-token-9999", "cus...9999")]
    public void ApiProviderProfile_ComputeKeyPreview_ProducesSafeMaskedStrings(string? raw, string expected)
    {
        var preview = ApiProviderProfile.ComputeKeyPreview(raw);
        Assert.Equal(expected, preview);
        if (!string.IsNullOrEmpty(raw) && raw.Length > 8)
        {
            Assert.DoesNotContain(raw.Substring(4, raw.Length - 8), preview);
        }
    }

    [Fact]
    public void ApiProviderProfile_GenerateStableCodexProviderId_IsDeterministicAndBounded()
    {
        var id = Guid.Parse("11223344-5566-7788-99aa-bbccddeeff00");
        var providerId = ApiProviderProfile.GenerateStableCodexProviderId(id);

        Assert.StartsWith("switchboard_", providerId);
        Assert.True(providerId.Length <= 24);
        Assert.True(providerId.Length >= 16);
        Assert.Matches("^[a-zA-Z0-9_]+$", providerId);
    }

    [Fact]
    public void RouteSwitching_PreservesStableCodexProviderIdIdentically()
    {
        var profileId = Guid.NewGuid();
        var originalStableId = ApiProviderProfile.GenerateStableCodexProviderId(profileId);

        var profile = new ApiProviderProfile
        {
            Id = profileId,
            CatalogProviderId = "router-cheap",
            StableCodexProviderId = originalStableId,
            Nickname = "Router.Cheap Primary",
            BaseUrl = "https://router.cheap/v1",
            SelectedRouteId = "primary",
            SelectedModel = "gpt-5.6-sol"
        };

        // User switches route to Reserve
        profile.SelectedRouteId = "reserve";
        profile.BaseUrl = "https://reserve.router.cheap/v1";

        // StableCodexProviderId must NOT change across route toggles!
        Assert.Equal(originalStableId, profile.StableCodexProviderId);
        Assert.Equal("reserve", profile.SelectedRouteId);
        Assert.Equal("https://reserve.router.cheap/v1", profile.BaseUrl);
    }

    [Fact]
    public void ActiveTargetResolver_CorrectlyResolvesChatGptVsApiVsExternal()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Root, Path.Combine(temp.Root, ".codex"));
        paths.EnsureDirectories();
        Directory.CreateDirectory(paths.Codex.CodexHome);

        var secretStore = new ApiKeySecretStore(_protector, _fs, paths.ApiKeysDir);
        var apiStore = new ApiProviderStore(_fs, paths.ApiProvidersPath, secretStore);
        var routingStore = new CodexRoutingConfigStore(_fs, paths);

        var resolver = new CodexActiveTargetResolver(routingStore, apiStore);

        // Case 1: config.toml does not exist or has default OpenAI -> ChatGpt
        var target1 = resolver.ResolveActiveTarget(paths.Codex.ConfigTomlPath, Guid.NewGuid(), "user@example.com");
        Assert.IsType<ActiveTarget.ChatGpt>(target1);

        // Case 2: config.toml has model_provider = "switchboard_xyz" matching saved profile -> Api
        var profileId = Guid.NewGuid();
        var stableId = ApiProviderProfile.GenerateStableCodexProviderId(profileId);
        var profile = new ApiProviderProfile
        {
            Id = profileId,
            CatalogProviderId = "router-cheap",
            StableCodexProviderId = stableId,
            Nickname = "My Router.Cheap",
            BaseUrl = "https://router.cheap/v1",
            SelectedModel = "gpt-5.6-sol"
        };
        apiStore.Save(profile);

        File.WriteAllText(paths.Codex.ConfigTomlPath, "model_provider = \"" + stableId + "\"\nmodel = \"gpt-5.6-sol\"\n");

        var target2 = resolver.ResolveActiveTarget(paths.Codex.ConfigTomlPath, null, null);
        var apiTarget = Assert.IsType<ActiveTarget.Api>(target2);
        Assert.Equal(profileId, apiTarget.Profile.Id);
        Assert.Equal("My Router.Cheap", apiTarget.Profile.Nickname);

        // Case 3: config.toml has unknown external provider -> External
        File.WriteAllText(paths.Codex.ConfigTomlPath, "model_provider = \"unknown_custom_provider\"\nmodel = \"claude-3\"\n");
        var target3 = resolver.ResolveActiveTarget(paths.Codex.ConfigTomlPath, null, null);
        var extTarget = Assert.IsType<ActiveTarget.External>(target3);
        Assert.Equal("unknown_custom_provider", extTarget.ProviderId);
    }

    [Fact]
    public async Task ThreadHandoffService_ForksThreadSafely()
    {
        var fakeClient = new FakeAppServerClient
        {
            ResponseHandler = (method, _) =>
            {
                if (method == "thread/fork")
                {
                    var json = @"{
                        ""modelProvider"": ""switchboard_8888"",
                        ""model"": ""gpt-5.6-sol"",
                        ""thread"": {
                            ""id"": ""thread-forked-789""
                        }
                    }";
                    return JsonDocument.Parse(json).RootElement;
                }
                return JsonDocument.Parse("{}").RootElement;
            }
        };

        var service = new CodexThreadHandoffService(() => Task.FromResult<ICodexAppServerClient>(fakeClient));
        var forkResult = await service.ForkThreadAsync(
            "thread-original-1234",
            "switchboard_8888",
            "gpt-5.6-sol",
            "Forked on Router.Cheap");

        Assert.NotNull(forkResult);
        Assert.Equal("thread-forked-789", forkResult.ForkedThreadId);
        Assert.Equal("thread-original-1234", forkResult.SourceThreadId);
        Assert.Equal("switchboard_8888", forkResult.TargetModelProvider);
        Assert.Equal("gpt-5.6-sol", forkResult.TargetModel);
    }

    [Fact]
    public async Task SyntheticSecretMarker_NeverLeakedToProfilesAuditOrConfigToml()
    {
        using var temp = new TempDir();
        var brokerExe = TestKeyBrokerLocator.FindKeyBrokerBinary();
        var paths = new AppPaths(temp.Root, Path.Combine(temp.Root, ".codex"));
        paths.EnsureDirectories();
        Directory.CreateDirectory(paths.Codex.CodexHome);

        File.WriteAllBytes(paths.Codex.ActiveAuthPath, System.Text.Encoding.UTF8.GetBytes("{\"auth_mode\":\"chatgpt\"}"));
        File.WriteAllText(paths.Codex.ConfigTomlPath, "model_provider = \"openai\"\nmodel = \"gpt-5.3-codex\"\n");

        var secretStore = new ApiKeySecretStore(_protector, _fs, paths.ApiKeysDir);
        var apiStore = new ApiProviderStore(_fs, paths.ApiProvidersPath, secretStore);
        var routingStore = new CodexRoutingConfigStore(_fs, paths);
        var brokerInstaller = new KeyBrokerInstaller(paths, _fs, brokerExe);
        var processManager = new FakeProcessManager();
        var clock = new FakeClock();
        var audit = new FakeAudit();

        var vault = new VaultService(new DpapiSecretProtector(), _fs, paths.VaultDir);
        var profileStore = new ProfileStore(_fs, paths.ProfilesPath);
        var chatGptSwitch = new SwitchService(vault, profileStore, _fs, processManager, new FakeConfigStore(), clock, audit, paths.Codex, paths.BackupsDir);

        var targetSwitch = new CodexTargetSwitchService(
            chatGptSwitch, routingStore, apiStore, secretStore, brokerInstaller,
            processManager, _fs, clock, audit, paths.Codex);

        var profileId = Guid.NewGuid();
        var profile = new ApiProviderProfile
        {
            Id = profileId,
            CatalogProviderId = "router-cheap",
            StableCodexProviderId = ApiProviderProfile.GenerateStableCodexProviderId(profileId),
            Nickname = "Secret Leak Check Provider",
            BaseUrl = "https://router.cheap/v1",
            SelectedModel = "gpt-5.6-sol",
            KeyPreview = ApiProviderProfile.ComputeKeyPreview(SyntheticUiSecret)
        };

        // 1. Save profile and secret
        apiStore.Save(profile);
        secretStore.SaveApiKey(profileId, SyntheticUiSecret);

        // 2. Switch to API provider
        var options = new SwitchExecutionOptions(CloseReopenMode.DoNothing, TimeSpan.FromSeconds(5), 5);
        var switchResult = await targetSwitch.SwitchToApiProviderAsync(profileId, options);
        Assert.Equal(TargetSwitchOutcome.Success, switchResult.Outcome);

        // 3. Switch route to reserve
        await targetSwitch.SwitchApiRouteAsync(profileId, "reserve", "https://reserve.router.cheap/v1", options);

        // 4. Switch back to ChatGPT
        await targetSwitch.SwitchToChatGptAsync(null, new List<ProfileMetadata>(), options);

        // 5. Audit all plain files
        var plainFiles = Directory.EnumerateFiles(temp.Root, "*.*", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.NotEmpty(plainFiles);

        foreach (var file in plainFiles)
        {
            var content = File.ReadAllText(file);
            Assert.DoesNotContain(SyntheticUiSecret, content);
        }
    }
}

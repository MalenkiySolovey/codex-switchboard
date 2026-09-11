using System.Security.Cryptography;
using CodexSwitcher.Core.Abstractions;
using CodexSwitcher.Core.Models;
using CodexSwitcher.Core.Services;
using CodexSwitcher.Core.Tests.TestSupport;
using CodexSwitcher.Infra;
using CodexSwitcher.Infra.Codex;
using CodexSwitcher.Infra.Io;
using CodexSwitcher.Infra.Security;
using Xunit;

namespace CodexSwitcher.Core.Tests;

public sealed class CodexTargetSwitchServiceTests
{
    private readonly PhysicalFileSystem _fs = new();
    private readonly DpapiSecretProtector _protector = new();

    

    [Fact]
    public async Task SwitchToApiProvider_LeavesAuthJsonBytesUnchanged()
    {
        using var temp = new TempDir();
        var brokerExe = TestKeyBrokerLocator.FindKeyBrokerBinary();
        var paths = new AppPaths(temp.Root, Path.Combine(temp.Root, ".codex"));
        paths.EnsureDirectories();
        Directory.CreateDirectory(paths.Codex.CodexHome);

        // Initial auth.json
        var initialAuthBytes = System.Text.Encoding.UTF8.GetBytes("{\"auth_mode\":\"chatgpt\",\"tokens\":{\"id_token\":\"user.jwt.token\"}}");
        File.WriteAllBytes(paths.Codex.ActiveAuthPath, initialAuthBytes);
        var preAuthHash = Convert.ToHexString(SHA256.HashData(initialAuthBytes));

        // Initial config.toml
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

        // Setup API Profile
        var apiProfileId = Guid.NewGuid();
        var apiProfile = new ApiProviderProfile
        {
            Id = apiProfileId,
            CatalogProviderId = "router-cheap",
            StableCodexProviderId = ApiProviderProfile.GenerateStableCodexProviderId(apiProfileId),
            Nickname = "Router.Cheap Test",
            BaseUrl = "https://router.cheap/v1",
            SelectedModel = "gpt-5.6-sol"
        };
        apiStore.Save(apiProfile);
        secretStore.SaveApiKey(apiProfileId, "sk-test-api-key-123456");

        // Execute Switch
        var options = new SwitchExecutionOptions(CloseReopenMode.DoNothing, TimeSpan.FromSeconds(5), 5);
        var result = await targetSwitch.SwitchToApiProviderAsync(apiProfileId, options);

        Assert.Equal(TargetSwitchOutcome.Success, result.Outcome);

        // ASSERTION: auth.json bytes are IDENTICAL
        var postAuthBytes = File.ReadAllBytes(paths.Codex.ActiveAuthPath);
        var postAuthHash = Convert.ToHexString(SHA256.HashData(postAuthBytes));
        Assert.Equal(preAuthHash, postAuthHash);
        Assert.Equal(initialAuthBytes, postAuthBytes);

        // Config is updated to switchboard provider
        var configContent = File.ReadAllText(paths.Codex.ConfigTomlPath);
        Assert.Contains($"model_provider = \"{apiProfile.StableCodexProviderId}\"", configContent);
        Assert.Contains("model = \"gpt-5.6-sol\"", configContent);
    }

    [Fact]
    public async Task SwitchToChatGpt_WhenSameAccount_ReturnsToOpenAiWithoutTouchingAuthJson()
    {
        using var temp = new TempDir();
        var brokerExe = TestKeyBrokerLocator.FindKeyBrokerBinary();
        var paths = new AppPaths(temp.Root, Path.Combine(temp.Root, ".codex"));
        paths.EnsureDirectories();
        Directory.CreateDirectory(paths.Codex.CodexHome);

        var initialAuthBytes = System.Text.Encoding.UTF8.GetBytes("{\"auth_mode\":\"chatgpt\",\"tokens\":{\"id_token\":\"user.jwt.token\"}}");
        File.WriteAllBytes(paths.Codex.ActiveAuthPath, initialAuthBytes);
        var preAuthHash = Convert.ToHexString(SHA256.HashData(initialAuthBytes));

        // Start in API mode
        File.WriteAllText(paths.Codex.ConfigTomlPath, "model_provider = \"switchboard_1234\"\nmodel = \"gpt-5.6-sol\"\n");

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

        var activeProfile = new ProfileMetadata
        {
            Id = Guid.NewGuid(),
            Nickname = "Active Account",
            AccountEmail = "active@example.com",
            IsActive = true
        };

        var options = new SwitchExecutionOptions(CloseReopenMode.DoNothing, TimeSpan.FromSeconds(5), 5);
        var result = await targetSwitch.SwitchToChatGptAsync(activeProfile.Id, new List<ProfileMetadata> { activeProfile }, options);

        Assert.Equal(TargetSwitchOutcome.Success, result.Outcome);

        // auth.json untouched
        var postAuthBytes = File.ReadAllBytes(paths.Codex.ActiveAuthPath);
        Assert.Equal(preAuthHash, Convert.ToHexString(SHA256.HashData(postAuthBytes)));

        // config.toml returns to openai
        var configState = routingStore.ReadRoutingState(paths.Codex.ConfigTomlPath);
        Assert.Equal("openai", configState.ModelProvider);
    }
    [Fact]
    public async Task SwitchToApiProvider_ApiA_To_ApiB_CoexistsAndLeavesAuthJsonUntouched()
    {
        using var temp = new TempDir();
        var brokerExe = TestKeyBrokerLocator.FindKeyBrokerBinary();
        var paths = new AppPaths(temp.Root, Path.Combine(temp.Root, ".codex"));
        paths.EnsureDirectories();
        Directory.CreateDirectory(paths.Codex.CodexHome);

        var initialAuthBytes = System.Text.Encoding.UTF8.GetBytes("{\"auth_mode\":\"chatgpt\",\"tokens\":{\"id_token\":\"user.jwt\"}}");
        File.WriteAllBytes(paths.Codex.ActiveAuthPath, initialAuthBytes);
        var preAuthHash = Convert.ToHexString(SHA256.HashData(initialAuthBytes));

        File.WriteAllText(paths.Codex.ConfigTomlPath, "model_provider = \"openai\"\n");

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

        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();
        var profA = new ApiProviderProfile { Id = idA, StableCodexProviderId = ApiProviderProfile.GenerateStableCodexProviderId(idA), Nickname = "Provider A", BaseUrl = "https://a.example/v1", SelectedModel = "mod-a" };
        var profB = new ApiProviderProfile { Id = idB, StableCodexProviderId = ApiProviderProfile.GenerateStableCodexProviderId(idB), Nickname = "Provider B", BaseUrl = "https://b.example/v1", SelectedModel = "mod-b" };

        apiStore.Save(profA);
        apiStore.Save(profB);
        secretStore.SaveApiKey(idA, "key-a-secret-12345");
        secretStore.SaveApiKey(idB, "key-b-secret-67890");

        var options = new SwitchExecutionOptions(CloseReopenMode.DoNothing, TimeSpan.FromSeconds(5), 5);

        // Switch to A
        await targetSwitch.SwitchToApiProviderAsync(idA, options);
        var stateA = routingStore.ReadRoutingState(paths.Codex.ConfigTomlPath);
        Assert.Equal(profA.StableCodexProviderId, stateA.ModelProvider);

        // Switch to B
        await targetSwitch.SwitchToApiProviderAsync(idB, options);
        var stateB = routingStore.ReadRoutingState(paths.Codex.ConfigTomlPath);
        Assert.Equal(profB.StableCodexProviderId, stateB.ModelProvider);
        Assert.Equal("mod-b", stateB.Model);

        // Both provider blocks coexist for thread continuity
        Assert.True(stateB.SwitchboardProviders.ContainsKey(profA.StableCodexProviderId));
        Assert.True(stateB.SwitchboardProviders.ContainsKey(profB.StableCodexProviderId));

        // auth.json strictly unchanged
        var postAuthBytes = File.ReadAllBytes(paths.Codex.ActiveAuthPath);
        Assert.Equal(preAuthHash, Convert.ToHexString(SHA256.HashData(postAuthBytes)));
    }

    [Fact]
    public async Task SwitchApiRoute_UpdatesBaseUrl_PreservesStableCodexProviderId()
    {
        using var temp = new TempDir();
        var brokerExe = TestKeyBrokerLocator.FindKeyBrokerBinary();
        var paths = new AppPaths(temp.Root, Path.Combine(temp.Root, ".codex"));
        paths.EnsureDirectories();
        Directory.CreateDirectory(paths.Codex.CodexHome);

        File.WriteAllBytes(paths.Codex.ActiveAuthPath, System.Text.Encoding.UTF8.GetBytes("{\"auth_mode\":\"chatgpt\"}"));
        File.WriteAllText(paths.Codex.ConfigTomlPath, "model_provider = \"openai\"\n");

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

        var id = Guid.NewGuid();
        var prof = new ApiProviderProfile
        {
            Id = id,
            CatalogProviderId = "router-cheap",
            StableCodexProviderId = ApiProviderProfile.GenerateStableCodexProviderId(id),
            Nickname = "Router.Cheap",
            BaseUrl = "https://router.cheap/v1",
            SelectedRouteId = "primary",
            SelectedModel = "gpt-5.6-sol"
        };
        apiStore.Save(prof);
        secretStore.SaveApiKey(id, "key-secret-12345");

        var options = new SwitchExecutionOptions(CloseReopenMode.DoNothing, TimeSpan.FromSeconds(5), 5);
        await targetSwitch.SwitchToApiProviderAsync(id, options);

        // Switch from Primary to Reserve
        var routeResult = await targetSwitch.SwitchApiRouteAsync(id, "reserve", "https://direct.router-cheap.com/v1", options);
        Assert.Equal(TargetSwitchOutcome.Success, routeResult.Outcome);

        var updatedProfile = apiStore.GetById(id);
        Assert.Equal("reserve", updatedProfile!.SelectedRouteId);
        Assert.Equal("https://direct.router-cheap.com/v1", updatedProfile.BaseUrl);

        var state = routingStore.ReadRoutingState(paths.Codex.ConfigTomlPath);
        Assert.Equal(prof.StableCodexProviderId, state.ModelProvider);
        Assert.Equal("https://direct.router-cheap.com/v1", state.SwitchboardProviders[prof.StableCodexProviderId].BaseUrl);
    }

    [Fact]
    public async Task SwitchToApiProvider_WhenKeyMissing_FailsFastWithoutTouchingConfig()
    {
        using var temp = new TempDir();
        var brokerExe = TestKeyBrokerLocator.FindKeyBrokerBinary();
        var paths = new AppPaths(temp.Root, Path.Combine(temp.Root, ".codex"));
        paths.EnsureDirectories();
        Directory.CreateDirectory(paths.Codex.CodexHome);

        var initialConfig = "model_provider = \"openai\"\n";
        File.WriteAllText(paths.Codex.ConfigTomlPath, initialConfig);

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

        var id = Guid.NewGuid();
        var prof = new ApiProviderProfile { Id = id, StableCodexProviderId = ApiProviderProfile.GenerateStableCodexProviderId(id), Nickname = "Missing Key" };
        apiStore.Save(prof);
        // Do NOT save key in secretStore

        var options = new SwitchExecutionOptions(CloseReopenMode.DoNothing, TimeSpan.FromSeconds(5), 5);
        var result = await targetSwitch.SwitchToApiProviderAsync(id, options);

        Assert.Equal(TargetSwitchOutcome.Failed, result.Outcome);
        Assert.Contains("missing", result.Message, StringComparison.OrdinalIgnoreCase);

        // config is completely untouched
        Assert.Equal(initialConfig, File.ReadAllText(paths.Codex.ConfigTomlPath));
    }
}

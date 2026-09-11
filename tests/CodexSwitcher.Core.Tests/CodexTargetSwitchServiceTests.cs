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

        Assert.Equal(initialConfig, File.ReadAllText(paths.Codex.ConfigTomlPath));
    }

    private sealed class TargetSwitchTestEnv : IDisposable
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
        public SwitchService ChatGptSwitch { get; }
        public CodexTargetSwitchService TargetSwitch { get; }
        public List<ProfileMetadata> ChatGptProfiles { get; } = [];

        public TargetSwitchTestEnv()
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

            ChatGptSwitch = new SwitchService(Vault, ProfileStore, Fs, Proc, Config, Clock, Audit, Paths.Codex, Paths.BackupsDir);
            TargetSwitch = new CodexTargetSwitchService(
                ChatGptSwitch, RoutingStore, ApiStore, SecretStore, BrokerInstaller,
                Proc, Fs, Clock, Audit, Paths.Codex);
        }

        public ProfileMetadata AddChatGptProfile(string nick, byte[] authBytes, bool active)
        {
            var p = new ProfileMetadata
            {
                Id = Guid.NewGuid(),
                Nickname = nick,
                AccountEmail = $"{nick.ToLowerInvariant()}@example.com",
                CreatedAt = Clock.UtcNow,
                IsActive = active,
                HealthStatus = HealthStatus.Valid,
            };
            p.BlobFingerprint = Vault.SaveBlob(p.Id, authBytes);
            ChatGptProfiles.Add(p);
            ProfileStore.SaveAll(ChatGptProfiles);
            return p;
        }

        public void SetActiveSlot(byte[] bytes) => Fs.WriteAllBytesAtomic(Paths.Codex.ActiveAuthPath, bytes);
        public byte[] ReadActiveSlot() => Fs.ReadAllBytes(Paths.Codex.ActiveAuthPath);

        public void Dispose() => Dir.Dispose();
    }

    private static SwitchExecutionOptions AutoOpts =>
        new(CloseReopenMode.Automatic, TimeSpan.FromSeconds(1), BackupsToKeep: 10);

    private static CodexProcessInfo DesktopApp(int pid = 100) =>
        new(pid, "Codex", @"C:\Apps\Codex\Codex.exe", null, CodexProcessKind.DesktopApp);

    private static CodexProcessInfo Cli(int pid = 200) =>
        new(pid, "codex", @"C:\npm\codex.exe", "exec", CodexProcessKind.Cli);

    [Fact]
    public async Task ChatGptA_To_ChatGptB_SwapsAuth_UpdatesMetadata_AndRelaunchesDesktop()
    {
        using var env = new TargetSwitchTestEnv();
        var aBytes = Sample.AuthJson(accountId: "acct_A", refreshToken: "rt-A");
        var bBytes = Sample.AuthJson(accountId: "acct_B", refreshToken: "rt-B");
        var a = env.AddChatGptProfile("A", aBytes, active: true);
        var b = env.AddChatGptProfile("B", bBytes, active: false);
        env.SetActiveSlot(aBytes);
        env.Proc.Running = [DesktopApp(pid: 101)];

        var result = await env.TargetSwitch.SwitchToChatGptAsync(b.Id, env.ChatGptProfiles, AutoOpts);

        Assert.Equal(TargetSwitchOutcome.Success, result.Outcome);
        Assert.Equal(bBytes, env.ReadActiveSlot());
        Assert.True(b.IsActive);
        Assert.False(a.IsActive);
        Assert.True(env.Proc.CloseCalled);
        Assert.Contains(env.Proc.Relaunched, p => p.Pid == 101 && p.Kind == CodexProcessKind.DesktopApp);
        Assert.Contains(result.ClosedProcesses!, p => p.Pid == 101);

        var routingState = env.RoutingStore.ReadRoutingState(env.Paths.Codex.ConfigTomlPath);
        Assert.Equal("openai", routingState.ModelProvider);
    }

    [Fact]
    public async Task ChatGptA_To_ChatGptB_WriteBack_PreservesRenewedTokensOfOutgoingAccount()
    {
        using var env = new TargetSwitchTestEnv();
        var aV1 = Sample.AuthJson(accountId: "acct_A", refreshToken: "rt-A-OLD", lastRefresh: "2026-06-20T00:00:00Z");
        var aV2 = Sample.AuthJson(accountId: "acct_A", refreshToken: "rt-A-NEW", lastRefresh: "2026-06-30T00:00:00Z");
        var bBytes = Sample.AuthJson(accountId: "acct_B");
        var a = env.AddChatGptProfile("A", aV1, active: true);
        var b = env.AddChatGptProfile("B", bBytes, active: false);
        env.SetActiveSlot(aV2); // Codex renewed the token in active slot while running
        env.Proc.Running = [DesktopApp()];

        await env.TargetSwitch.SwitchToChatGptAsync(b.Id, env.ChatGptProfiles, AutoOpts);

        // Vault of outgoing account A must preserve the renewed token
        Assert.Equal(aV2, env.Vault.LoadBlob(a.Id));
        Assert.Equal(new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero), a.LastRefreshedAt);
    }

    [Fact]
    public async Task ChatGptA_To_ChatGptB_Rollback_OnSlotWriteFailure_RestoresA_AndKeepsAActive()
    {
        using var env = new TargetSwitchTestEnv();
        var aBytes = Sample.AuthJson(accountId: "acct_A", refreshToken: "rt-A");
        var bBytes = Sample.AuthJson(accountId: "acct_B", refreshToken: "rt-B");
        var a = env.AddChatGptProfile("A", aBytes, active: true);
        var b = env.AddChatGptProfile("B", bBytes, active: false);
        env.SetActiveSlot(aBytes);
        env.Proc.Running = [DesktopApp()];

        env.Fs.FailAtomicWriteForPath = env.Paths.Codex.ActiveAuthPath; // inject failure on active slot write

        var result = await env.TargetSwitch.SwitchToChatGptAsync(b.Id, env.ChatGptProfiles, AutoOpts);

        Assert.Equal(TargetSwitchOutcome.RolledBack, result.Outcome);
        Assert.Equal(aBytes, env.ReadActiveSlot()); // original active slot restored
        Assert.True(a.IsActive);
        Assert.False(b.IsActive);
        Assert.Contains(env.Proc.Relaunched, p => p.Kind == CodexProcessKind.DesktopApp);
    }

    [Fact]
    public async Task ChatGptA_To_ChatGptB_AntiRace_AbortsWhenCodexCliRemnant_LeavesSlotUntouched()
    {
        using var env = new TargetSwitchTestEnv();
        var aBytes = Sample.AuthJson(accountId: "acct_A");
        var bBytes = Sample.AuthJson(accountId: "acct_B");
        var a = env.AddChatGptProfile("A", aBytes, active: true);
        var b = env.AddChatGptProfile("B", bBytes, active: false);
        env.SetActiveSlot(aBytes);
        env.Proc.Running = [DesktopApp(), Cli()];
        env.Proc.RemnantAfterClose = true; // CLI survives close

        var result = await env.TargetSwitch.SwitchToChatGptAsync(b.Id, env.ChatGptProfiles, AutoOpts);

        Assert.Equal(TargetSwitchOutcome.AbortedProcessRemnant, result.Outcome);
        Assert.Equal(aBytes, env.ReadActiveSlot()); // auth.json untouched
        Assert.True(a.IsActive);
        Assert.False(b.IsActive);
    }

    [Fact]
    public async Task TargetUndecryptable_FailsFast_WithoutClosingApps()
    {
        using var env = new TargetSwitchTestEnv();
        var aBytes = Sample.AuthJson(accountId: "acct_A");
        var a = env.AddChatGptProfile("A", aBytes, active: true);
        var b = env.AddChatGptProfile("B", Sample.AuthJson(accountId: "acct_B"), active: false);
        env.SetActiveSlot(aBytes);
        env.Proc.Running = [DesktopApp()];

        // Corrupt B's blob in vault so DPAPI cannot decrypt it
        File.WriteAllBytes(env.Vault.BlobPath(b.Id), [1, 2, 3, 4, 5, 6, 7, 8]);

        var result = await env.TargetSwitch.SwitchToChatGptAsync(b.Id, env.ChatGptProfiles, AutoOpts);

        Assert.Equal(TargetSwitchOutcome.Failed, result.Outcome);
        Assert.Equal(ErrorCategory.DecryptionFailed, result.Error!.Category);
        Assert.False(env.Proc.CloseCalled); // no apps closed
        Assert.Equal(aBytes, env.ReadActiveSlot()); // active slot intact
        Assert.True(a.IsActive);
        Assert.False(b.IsActive);
    }

    [Fact]
    public async Task ApiProvider_To_ChatGptB_SwapsAuth_SetsOpenAiRouting_AndRelaunchesDesktop()
    {
        using var env = new TargetSwitchTestEnv();
        var aBytes = Sample.AuthJson(accountId: "acct_A");
        var bBytes = Sample.AuthJson(accountId: "acct_B");
        var a = env.AddChatGptProfile("A", aBytes, active: true);
        var b = env.AddChatGptProfile("B", bBytes, active: false);
        env.SetActiveSlot(aBytes);
        env.Proc.Running = [DesktopApp(pid: 301)];

        // Start in API Provider mode
        File.WriteAllText(env.Paths.Codex.ConfigTomlPath, "model_provider = \"switchboard_1234\"\nmodel = \"gpt-5.6-sol\"\n");

        var result = await env.TargetSwitch.SwitchToChatGptAsync(b.Id, env.ChatGptProfiles, AutoOpts);

        Assert.Equal(TargetSwitchOutcome.Success, result.Outcome);
        Assert.Equal(bBytes, env.ReadActiveSlot());
        Assert.True(b.IsActive);
        Assert.False(a.IsActive);

        var routing = env.RoutingStore.ReadRoutingState(env.Paths.Codex.ConfigTomlPath);
        Assert.Equal("openai", routing.ModelProvider);

        Assert.True(env.Proc.CloseCalled);
        Assert.Contains(env.Proc.Relaunched, p => p.Pid == 301);
    }

    [Fact]
    public async Task ApiProvider_To_SameChatGptA_PreservesAuth_SetsOpenAiRouting_AndRelaunchesDesktop()
    {
        using var env = new TargetSwitchTestEnv();
        var aBytes = Sample.AuthJson(accountId: "acct_A");
        var a = env.AddChatGptProfile("A", aBytes, active: true);
        env.SetActiveSlot(aBytes);
        env.Proc.Running = [DesktopApp(pid: 401)];

        // Start in API Provider mode
        File.WriteAllText(env.Paths.Codex.ConfigTomlPath, "model_provider = \"switchboard_1234\"\nmodel = \"gpt-5.6-sol\"\n");

        // Switch back to ChatGPT (passing A or null)
        var result = await env.TargetSwitch.SwitchToChatGptAsync(a.Id, env.ChatGptProfiles, AutoOpts);

        Assert.Equal(TargetSwitchOutcome.Success, result.Outcome);
        Assert.Equal(aBytes, env.ReadActiveSlot()); // auth.json preserved
        Assert.True(a.IsActive);

        var routing = env.RoutingStore.ReadRoutingState(env.Paths.Codex.ConfigTomlPath);
        Assert.Equal("openai", routing.ModelProvider);

        Assert.True(env.Proc.CloseCalled);
        Assert.Contains(env.Proc.Relaunched, p => p.Pid == 401);
    }

    [Fact]
    public async Task ChatGptA_SameAccount_AlreadyOpenAi_IsNoOp_DoesNotTouchProcesses()
    {
        using var env = new TargetSwitchTestEnv();
        var aBytes = Sample.AuthJson(accountId: "acct_A");
        var a = env.AddChatGptProfile("A", aBytes, active: true);
        env.SetActiveSlot(aBytes);
        File.WriteAllText(env.Paths.Codex.ConfigTomlPath, "model_provider = \"openai\"\n");
        env.Proc.Running = [DesktopApp()];

        var result = await env.TargetSwitch.SwitchToChatGptAsync(a.Id, env.ChatGptProfiles, AutoOpts);

        Assert.Equal(TargetSwitchOutcome.Success, result.Outcome);
        Assert.False(env.Proc.CloseCalled);
        Assert.Empty(env.Proc.Relaunched);
        Assert.Equal(aBytes, env.ReadActiveSlot());
    }

    [Fact]
    public void PathAuthority_ChatGPTMode_WritesExactCodexAuthPath_ReadByCodex()
    {
        using var env = new TargetSwitchTestEnv();
        var expectedAuthPath = Path.Combine(env.Paths.Codex.CodexHome, "auth.json");
        var expectedConfigPath = Path.Combine(env.Paths.Codex.CodexHome, "config.toml");

        Assert.Equal(expectedAuthPath, env.Paths.Codex.ActiveAuthPath);
        Assert.Equal(expectedConfigPath, env.Paths.Codex.ConfigTomlPath);
    }

    [Fact]
    public async Task ApiProviderIsolation_AddingAndConfiguringProviders_DoesNotAlterChatGptSwitching()
    {
        using var env = new TargetSwitchTestEnv();

        // Add API provider
        var apiId = Guid.NewGuid();
        var apiProf = new ApiProviderProfile
        {
            Id = apiId,
            CatalogProviderId = "router-cheap",
            StableCodexProviderId = ApiProviderProfile.GenerateStableCodexProviderId(apiId),
            Nickname = "Router.Cheap",
            BaseUrl = "https://router.cheap/v1",
            SelectedModel = "gpt-5.6-sol"
        };
        env.ApiStore.Save(apiProf);
        env.SecretStore.SaveApiKey(apiId, "sk-isolated-secret-999");

        // Perform ChatGPT A -> B switch
        var aBytes = Sample.AuthJson(accountId: "acct_A");
        var bBytes = Sample.AuthJson(accountId: "acct_B");
        var a = env.AddChatGptProfile("A", aBytes, active: true);
        var b = env.AddChatGptProfile("B", bBytes, active: false);
        env.SetActiveSlot(aBytes);
        env.Proc.Running = [DesktopApp()];

        var result = await env.TargetSwitch.SwitchToChatGptAsync(b.Id, env.ChatGptProfiles, AutoOpts);

        Assert.Equal(TargetSwitchOutcome.Success, result.Outcome);
        Assert.Equal(bBytes, env.ReadActiveSlot());
        Assert.True(b.IsActive);
        Assert.False(a.IsActive);

        // API provider store is unaffected
        var storedApi = env.ApiStore.GetById(apiId);
        Assert.NotNull(storedApi);
        Assert.Equal("Router.Cheap", storedApi.Nickname);
        Assert.True(env.SecretStore.HasApiKey(apiId));
    }
}

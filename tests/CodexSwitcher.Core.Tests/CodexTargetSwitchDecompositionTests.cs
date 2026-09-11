using System.Security.Cryptography;
using CodexSwitcher.Core.Accounts.Models;
using CodexSwitcher.Core.Common.Enums;
using CodexSwitcher.Core.Common.Errors;
using CodexSwitcher.Core.Common.Lifecycle;
using CodexSwitcher.Core.Providers.Models;
using CodexSwitcher.Core.Routing.Contracts;
using CodexSwitcher.Core.Routing.Models;
using CodexSwitcher.Core.Routing.Services;
using CodexSwitcher.Core.Tests.TestSupport;
using Xunit;

namespace CodexSwitcher.Core.Tests;

public sealed class CodexTargetSwitchDecompositionTests
{
    private sealed class DecompositionTestEnv : IDisposable
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
        public SwitchPlanBuilder PlanBuilder { get; }
        public SwitchTransactionExecutor Executor { get; }
        public CodexTargetSwitchService Facade { get; }
        public List<ProfileMetadata> ChatGptProfiles { get; } = [];

        public DecompositionTestEnv()
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
            PlanBuilder = new SwitchPlanBuilder(ApiStore, SecretStore, BrokerInstaller, RoutingStore, Paths.Codex);
            Executor = new SwitchTransactionExecutor(ChatGptSwitch, RoutingStore, ApiStore, Proc, Fs, Clock, Audit, Paths.Codex);
            Facade = new CodexTargetSwitchService(PlanBuilder, Executor);
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

    private static SwitchExecutionOptions DoNothingOpts =>
        new(CloseReopenMode.DoNothing, TimeSpan.FromSeconds(1), BackupsToKeep: 10);

    [Fact]
    public void SwitchPlanBuilder_BuildApiProviderPlan_ValidProfile_ContainsZeroSecrets()
    {
        using var env = new DecompositionTestEnv();
        var id = Guid.NewGuid();
        var profile = new ApiProviderProfile
        {
            Id = id,
            CatalogProviderId = "test-prov",
            StableCodexProviderId = ApiProviderProfile.GenerateStableCodexProviderId(id),
            Nickname = "Test Prov",
            BaseUrl = "https://api.test/v1",
            SelectedModel = "gpt-5.6-sol"
        };
        env.ApiStore.Save(profile);
        env.SecretStore.SaveApiKey(id, "super-secret-key-12345");

        var plan = env.PlanBuilder.BuildApiProviderPlan(id, DoNothingOpts);

        Assert.IsType<ApiProviderSwitchPlan>(plan);
        var apiPlan = (ApiProviderSwitchPlan)plan;
        Assert.Equal(id, apiPlan.TargetProfile.Id);
        Assert.Equal("gpt-5.6-sol", apiPlan.TargetProfile.SelectedModel);
        Assert.NotNull(apiPlan.BrokerPath);

        // Assert plan record type does not define any secret properties or fields
        var properties = typeof(ApiProviderSwitchPlan).GetProperties();
        Assert.DoesNotContain(properties, p => p.Name.Contains("Key", StringComparison.OrdinalIgnoreCase) && p.PropertyType == typeof(string));
        Assert.DoesNotContain(properties, p => p.Name.Contains("Secret", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(properties, p => p.Name.Contains("Token", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SwitchPlanBuilder_BuildApiProviderPlan_MissingKey_ReturnsInvalidPlanAndMarksCredentialMissing()
    {
        using var env = new DecompositionTestEnv();
        var id = Guid.NewGuid();
        var profile = new ApiProviderProfile
        {
            Id = id,
            CatalogProviderId = "test-prov",
            StableCodexProviderId = ApiProviderProfile.GenerateStableCodexProviderId(id),
            Nickname = "No Key Prov"
        };
        env.ApiStore.Save(profile);
        // Do not save secret

        var plan = env.PlanBuilder.BuildApiProviderPlan(id, DoNothingOpts);

        Assert.IsType<InvalidSwitchPlan>(plan);
        var invalid = (InvalidSwitchPlan)plan;
        Assert.Equal(ErrorCategory.DecryptionFailed, invalid.Category);
        Assert.Contains("missing", invalid.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        var savedProfile = env.ApiStore.GetById(id);
        Assert.Equal(ApiProviderProfileStatus.CredentialMissing, savedProfile!.Status);
    }

    [Fact]
    public void SwitchPlanBuilder_BuildChatGptPlan_DistinguishesModesCorrectly()
    {
        using var env = new DecompositionTestEnv();
        var aBytes = Sample.AuthJson(accountId: "acct_A");
        var bBytes = Sample.AuthJson(accountId: "acct_B");
        var a = env.AddChatGptProfile("A", aBytes, active: true);
        var b = env.AddChatGptProfile("B", bBytes, active: false);

        // Case 1: Account switch (A -> B)
        File.WriteAllText(env.Paths.Codex.ConfigTomlPath, "model_provider = \"openai\"\n");
        var plan1 = env.PlanBuilder.BuildChatGptPlan(b.Id, env.ChatGptProfiles, AutoOpts);
        Assert.IsType<ChatGptAccountSwitchPlan>(plan1);

        // Case 2: Same account, but API routing active -> ReturnRouting
        File.WriteAllText(env.Paths.Codex.ConfigTomlPath, "model_provider = \"switchboard_custom\"\n");
        var plan2 = env.PlanBuilder.BuildChatGptPlan(a.Id, env.ChatGptProfiles, AutoOpts);
        Assert.IsType<ChatGptReturnRoutingSwitchPlan>(plan2);

        // Case 3: Same account, OpenAI already active -> NoOp
        File.WriteAllText(env.Paths.Codex.ConfigTomlPath, "model_provider = \"openai\"\n");
        var plan3 = env.PlanBuilder.BuildChatGptPlan(a.Id, env.ChatGptProfiles, AutoOpts);
        Assert.IsType<ChatGptNoOpSwitchPlan>(plan3);
    }

    [Fact]
    public async Task SwitchCompensationCoordinator_CompensatesConfigAndProcessesInReverseOrder()
    {
        using var env = new DecompositionTestEnv();
        var originalToml = "model_provider = \"original\"\n";
        var modifiedToml = "model_provider = \"mutated\"\n";
        File.WriteAllText(env.Paths.Codex.ConfigTomlPath, modifiedToml);

        var coordinator = new SwitchCompensationCoordinator(env.RoutingStore, env.Proc, env.Audit, env.Paths.Codex);
        coordinator.RegisterConfigBackup(System.Text.Encoding.UTF8.GetBytes(originalToml));

        var procInfo = new CodexProcessInfo(555, "Codex", @"C:\Apps\Codex\Codex.exe", null, CodexProcessKind.DesktopApp);
        coordinator.RegisterCapturedProcesses([procInfo]);

        await coordinator.CompensateAsync("test-action", "forced failure");

        // config restored
        Assert.Equal(originalToml, File.ReadAllText(env.Paths.Codex.ConfigTomlPath));
        // desktop relaunched
        Assert.Contains(env.Proc.Relaunched, p => p.Pid == 555);
        // audit recorded
        Assert.Contains(env.Audit.Entries, r => r.Action == "test-action" && r.Outcome == "rolled-back");
    }

    [Fact]
    public async Task SwitchTransactionExecutor_TamperedAuthJsonDuringApiSwitch_TriggersRollback()
    {
        using var env = new DecompositionTestEnv();
        var initialAuth = "{\"auth_mode\":\"original\"}"u8.ToArray();
        env.SetActiveSlot(initialAuth);

        var originalConfig = "model_provider = \"openai\"\n";
        File.WriteAllText(env.Paths.Codex.ConfigTomlPath, originalConfig);

        var id = Guid.NewGuid();
        var profile = new ApiProviderProfile
        {
            Id = id,
            CatalogProviderId = "test-prov",
            StableCodexProviderId = ApiProviderProfile.GenerateStableCodexProviderId(id),
            Nickname = "Test Prov",
            BaseUrl = "https://api.test/v1"
        };
        env.ApiStore.Save(profile);
        env.SecretStore.SaveApiKey(id, "secret-key");

        // Tamper auth.json right when config is written by configuring Fs hook
        // We'll simulate tampering by changing auth.json before switch if we hook write
        var plan = (ApiProviderSwitchPlan)env.PlanBuilder.BuildApiProviderPlan(id, DoNothingOpts);

        // Mutate auth.json right before verification by using a fault in FileSystem:
        // When ActiveAuthPath is read the second time (post bytes), return tampered bytes
        var readCount = 0;
        env.Fs.OnReadAllBytes = path =>
        {
            if (path == env.Paths.Codex.ActiveAuthPath)
            {
                readCount++;
                if (readCount >= 2)
                {
                    return "{\"auth_mode\":\"TAMPERED\"}"u8.ToArray();
                }
            }
            return null;
        };

        var result = await env.Executor.ExecuteAsync(plan);

        Assert.Equal(TargetSwitchOutcome.RolledBack, result.Outcome);
        Assert.Contains("CRITICAL: auth.json bytes modified", result.Message);
        // Config.toml restored to original
        Assert.Equal(originalConfig, File.ReadAllText(env.Paths.Codex.ConfigTomlPath));
    }

    [Fact]
    public async Task CodexTargetSwitchService_SerializesConcurrentSwitchesViaSingleLock()
    {
        using var env = new DecompositionTestEnv();
        var initialAuth = "{\"auth_mode\":\"chatgpt\"}"u8.ToArray();
        env.SetActiveSlot(initialAuth);
        File.WriteAllText(env.Paths.Codex.ConfigTomlPath, "model_provider = \"openai\"\n");

        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();
        var profA = new ApiProviderProfile { Id = idA, StableCodexProviderId = ApiProviderProfile.GenerateStableCodexProviderId(idA), Nickname = "A", BaseUrl = "https://a.test/v1" };
        var profB = new ApiProviderProfile { Id = idB, StableCodexProviderId = ApiProviderProfile.GenerateStableCodexProviderId(idB), Nickname = "B", BaseUrl = "https://b.test/v1" };

        env.ApiStore.Save(profA);
        env.ApiStore.Save(profB);
        env.SecretStore.SaveApiKey(idA, "key-a");
        env.SecretStore.SaveApiKey(idB, "key-b");

        // Launch concurrent switches to A and B
        var taskA = env.Facade.SwitchToApiProviderAsync(idA, DoNothingOpts);
        var taskB = env.Facade.SwitchToApiProviderAsync(idB, DoNothingOpts);

        var results = await Task.WhenAll(taskA, taskB);

        Assert.All(results, r => Assert.Equal(TargetSwitchOutcome.Success, r.Outcome));

        // Final state in config.toml is deterministically one of the two, not corrupt
        var finalState = env.RoutingStore.ReadRoutingState(env.Paths.Codex.ConfigTomlPath);
        Assert.True(finalState.ModelProvider == profA.StableCodexProviderId || finalState.ModelProvider == profB.StableCodexProviderId);
    }
}

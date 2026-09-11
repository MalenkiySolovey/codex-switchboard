using System.Diagnostics;
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

public sealed class SecretLeakRegressionTests
{
    private const string SyntheticSecretMarker = "sk-SYNTHETIC_SWITCHBOARD_DO_NOT_LEAK_123456";
    private readonly PhysicalFileSystem _fs = new();
    private readonly DpapiSecretProtector _protector = new();

    

    [Fact]
    public async Task FullApiSwitchLifecycle_LeavesZeroPlaintextSecretLeaks()
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

        var apiProfileId = Guid.NewGuid();
        var apiProfile = new ApiProviderProfile
        {
            Id = apiProfileId,
            CatalogProviderId = "router-cheap",
            StableCodexProviderId = ApiProviderProfile.GenerateStableCodexProviderId(apiProfileId),
            Nickname = "Router.Cheap",
            BaseUrl = "https://router.cheap/v1",
            SelectedModel = "gpt-5.6-sol",
            KeyPreview = ApiProviderProfile.ComputeKeyPreview(SyntheticSecretMarker)
        };
        apiStore.Save(apiProfile);
        secretStore.SaveApiKey(apiProfileId, SyntheticSecretMarker);

        var options = new SwitchExecutionOptions(CloseReopenMode.DoNothing, TimeSpan.FromSeconds(5), 5);
        var result = await targetSwitch.SwitchToApiProviderAsync(apiProfileId, options);
        Assert.Equal(TargetSwitchOutcome.Success, result.Outcome);

        // Scan all text files in both temp.Root and paths.Codex.CodexHome for plaintext leak
        var searchDirs = new[] { temp.Root, paths.Codex.CodexHome };
        var foundLeaks = new List<string>();

        foreach (var dir in searchDirs)
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories))
            {
                // Encrypted DPAPI binary blob is allowed to hold the ciphertext
                if (file.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    var text = File.ReadAllText(file);
                    if (text.Contains(SyntheticSecretMarker, StringComparison.Ordinal))
                    {
                        foundLeaks.Add(file);
                    }
                }
                catch
                {
                    // Ignore unreadable files
                }
            }
        }

        Assert.Empty(foundLeaks);
    }
}

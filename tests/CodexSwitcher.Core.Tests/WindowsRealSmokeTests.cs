using System.Security.Cryptography;
using CodexSwitcher.Core.Abstractions;
using CodexSwitcher.Core.Models;
using CodexSwitcher.Core.Services;
using CodexSwitcher.Core.Tests.TestSupport;
using CodexSwitcher.Infra;
using CodexSwitcher.Infra.Codex;
using CodexSwitcher.Infra.Io;
using CodexSwitcher.Infra.Processes;
using CodexSwitcher.Infra.Security;
using Xunit;

namespace CodexSwitcher.Core.Tests;

/// <summary>
/// Bounded local Windows smoke test validating real filesystem contracts,
/// real DPAPI cryptography, real process discovery, and zero-secret-leak invariants.
/// </summary>
public sealed class WindowsRealSmokeTests
{
    [Fact]
    public void RealProcessDiscovery_DoesNotThrow_AndFindsRunningSessionProcesses()
    {
        var procManager = new CodexProcessManager();
        var processes = procManager.FindRunningCodexProcesses();

        // Must return without exceptions
        Assert.NotNull(processes);

        foreach (var proc in processes)
        {
            Assert.True(proc.Pid > 0);
            Assert.False(string.IsNullOrWhiteSpace(proc.ProcessName));
            Assert.False(string.IsNullOrWhiteSpace(proc.ExecutablePath));
            Assert.True(proc.Kind is CodexProcessKind.DesktopApp or CodexProcessKind.Cli);
        }
    }

    [Fact]
    public async Task RealWindowsFilesystem_SyntheticAccountSwitch_PreservesInvariantAndZeroSecretLeak()
    {
        using var temp = new TempDir();
        var fs = new PhysicalFileSystem();
        var protector = new DpapiSecretProtector();
        var brokerExe = TestKeyBrokerLocator.FindKeyBrokerBinary();
        var paths = new AppPaths(temp.Root, Path.Combine(temp.Root, ".codex"));
        paths.EnsureDirectories();
        Directory.CreateDirectory(paths.Codex.CodexHome);

        var vault = new VaultService(protector, fs, paths.VaultDir);
        var profileStore = new ProfileStore(fs, paths.ProfilesPath);
        var secretStore = new ApiKeySecretStore(protector, fs, paths.ApiKeysDir);
        var apiStore = new ApiProviderStore(fs, paths.ApiProvidersPath, secretStore);
        var routingStore = new CodexRoutingConfigStore(fs, paths);
        var brokerInstaller = new KeyBrokerInstaller(paths, fs, brokerExe);
        var fakeProcesses = new FakeProcessManager();
        var clock = new FakeClock();
        var audit = new FakeAudit();

        var chatGptSwitch = new SwitchService(vault, profileStore, fs, fakeProcesses, new FakeConfigStore(), clock, audit, paths.Codex, paths.BackupsDir);
        var targetSwitch = new CodexTargetSwitchService(
            chatGptSwitch, routingStore, apiStore, secretStore, brokerInstaller,
            fakeProcesses, fs, clock, audit, paths.Codex);

        var syntheticSecretA = "synth-rt-token-account-A-999999";
        var syntheticSecretB = "synth-rt-token-account-B-888888";

        var aBytes = Sample.AuthJson(accountId: "acct_A", refreshToken: syntheticSecretA);
        var bBytes = Sample.AuthJson(accountId: "acct_B", refreshToken: syntheticSecretB);

        var profA = new ProfileMetadata
        {
            Id = Guid.NewGuid(),
            Nickname = "Synthetic Account A",
            AccountEmail = "a@synthetic.test",
            CreatedAt = clock.UtcNow,
            IsActive = true,
            HealthStatus = HealthStatus.Valid
        };
        profA.BlobFingerprint = vault.SaveBlob(profA.Id, aBytes);

        var profB = new ProfileMetadata
        {
            Id = Guid.NewGuid(),
            Nickname = "Synthetic Account B",
            AccountEmail = "b@synthetic.test",
            CreatedAt = clock.UtcNow,
            IsActive = false,
            HealthStatus = HealthStatus.Valid
        };
        profB.BlobFingerprint = vault.SaveBlob(profB.Id, bBytes);

        var profiles = new List<ProfileMetadata> { profA, profB };
        profileStore.SaveAll(profiles);

        // Place Account A in live auth.json slot
        fs.WriteAllBytesAtomic(paths.Codex.ActiveAuthPath, aBytes);
        fs.WriteAllTextAtomic(paths.Codex.ConfigTomlPath, "model_provider = \"openai\"\n");

        fakeProcesses.Running = [new CodexProcessInfo(5555, "ChatGPT", @"C:\Apps\ChatGPT.exe", null, CodexProcessKind.DesktopApp)];

        var options = new SwitchExecutionOptions(CloseReopenMode.Automatic, TimeSpan.FromSeconds(1), 5);

        // 1. Switch A -> B
        var result1 = await targetSwitch.SwitchToChatGptAsync(profB.Id, profiles, options);
        Assert.Equal(TargetSwitchOutcome.Success, result1.Outcome);

        // Invariant: auth.json has Account B
        var liveSlotB = fs.ReadAllBytes(paths.Codex.ActiveAuthPath);
        Assert.Equal(bBytes, liveSlotB);
        Assert.True(profB.IsActive);
        Assert.False(profA.IsActive);
        Assert.True(fakeProcesses.CloseCalled);
        Assert.Contains(fakeProcesses.Relaunched, p => p.Pid == 5555);

        // 2. Switch B -> A (reverse transition)
        var result2 = await targetSwitch.SwitchToChatGptAsync(profA.Id, profiles, options);
        Assert.Equal(TargetSwitchOutcome.Success, result2.Outcome);

        // Invariant: auth.json has Account A
        var liveSlotA = fs.ReadAllBytes(paths.Codex.ActiveAuthPath);
        Assert.Equal(aBytes, liveSlotA);
        Assert.True(profA.IsActive);
        Assert.False(profB.IsActive);

        // 3. Verify zero plaintext secret leaks in audit entries
        foreach (var entry in audit.Entries)
        {
            Assert.DoesNotContain(syntheticSecretA, entry.Action);
            Assert.DoesNotContain(syntheticSecretA, entry.Outcome);
            if (entry.Detail is not null)
            {
                Assert.DoesNotContain(syntheticSecretA, entry.Detail);
                Assert.DoesNotContain(syntheticSecretB, entry.Detail);
            }
        }
    }
}

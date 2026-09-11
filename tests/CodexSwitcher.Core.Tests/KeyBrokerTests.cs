using System.Diagnostics;
using CodexSwitcher.Core.Abstractions;
using CodexSwitcher.Core.Services;
using CodexSwitcher.Core.Tests.TestSupport;
using CodexSwitcher.Infra;
using CodexSwitcher.Infra.Io;
using CodexSwitcher.Infra.Security;
using Xunit;

namespace CodexSwitcher.Core.Tests;

public sealed class KeyBrokerTests
{
    private readonly PhysicalFileSystem _fs = new();
    private readonly DpapiSecretProtector _protector = new();

    

    [Fact]
    public void KeyBroker_WithValidKey_OutputsRawTokenAndReturnsZero()
    {
        using var temp = new TempDir();
        var brokerExe = TestKeyBrokerLocator.FindKeyBrokerBinary();
        var appPaths = new AppPaths(temp.Root);
        appPaths.EnsureDirectories();

        var secretStore = new ApiKeySecretStore(_protector, _fs, appPaths.ApiKeysDir);
        var keyId = Guid.NewGuid();
        var rawKey = "sk-valid-key-broker-test-123456";
        secretStore.SaveApiKey(keyId, rawKey);

        var psi = new ProcessStartInfo
        {
            FileName = brokerExe,
            Arguments = $"--key-id {keyId:D}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.Environment["CODEXSWITCHBOARD_HOME"] = temp.Root;

        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        Assert.Equal(0, proc.ExitCode);
        Assert.Equal(rawKey, stdout.TrimEnd('\r', '\n'));
        Assert.Empty(stderr);
    }

    [Fact]
    public void KeyBroker_WithMissingArguments_ReturnsExitCode1()
    {
        var brokerExe = TestKeyBrokerLocator.FindKeyBrokerBinary();
        var psi = new ProcessStartInfo
        {
            FileName = brokerExe,
            Arguments = "",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var proc = Process.Start(psi)!;
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        Assert.Equal(1, proc.ExitCode);
        Assert.Contains("Error: Invalid arguments", stderr);
    }

    [Fact]
    public void KeyBroker_WithInvalidGuid_ReturnsExitCode1()
    {
        var brokerExe = TestKeyBrokerLocator.FindKeyBrokerBinary();
        var psi = new ProcessStartInfo
        {
            FileName = brokerExe,
            Arguments = "--key-id not-a-guid",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var proc = Process.Start(psi)!;
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        Assert.Equal(1, proc.ExitCode);
        Assert.Contains("Error: Invalid arguments", stderr);
    }

    [Fact]
    public void KeyBroker_WithMissingKeyFile_ReturnsExitCode3()
    {
        using var temp = new TempDir();
        var brokerExe = TestKeyBrokerLocator.FindKeyBrokerBinary();

        var psi = new ProcessStartInfo
        {
            FileName = brokerExe,
            Arguments = $"--key-id {Guid.NewGuid():D}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.Environment["CODEXSWITCHBOARD_HOME"] = temp.Root;

        using var proc = Process.Start(psi)!;
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        Assert.Equal(3, proc.ExitCode);
        Assert.Contains("API key credential not found", stderr);
    }

    [Fact]
    public void KeyBrokerInstaller_InstallsAndVerifiesIntegrity()
    {
        using var temp = new TempDir();
        var brokerExe = TestKeyBrokerLocator.FindKeyBrokerBinary();
        var paths = new AppPaths(temp.Root);
        var installer = new KeyBrokerInstaller(paths, _fs, brokerExe);

        Assert.False(installer.IsInstalledAndValid());

        var installedPath = installer.EnsureInstalled();
        Assert.True(File.Exists(installedPath));
        Assert.True(installer.IsInstalledAndValid());

        // Tamper with installed file
        File.WriteAllBytes(installedPath, new byte[] { 1, 2, 3, 4 });
        Assert.False(installer.IsInstalledAndValid());

        // EnsureInstalled should repair it
        installer.EnsureInstalled();
        Assert.True(installer.IsInstalledAndValid());
    }
}

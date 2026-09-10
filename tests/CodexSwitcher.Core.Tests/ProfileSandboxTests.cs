using System.Text;
using CodexSwitcher.Core.Abstractions;
using CodexSwitcher.Core.Security;
using CodexSwitcher.Core.Tests.TestSupport;
using CodexSwitcher.Infra.Codex;
using CodexSwitcher.Infra.Io;
using Xunit;

namespace CodexSwitcher.Core.Tests;

public sealed class ProfileSandboxTests
{
    [Fact]
    public void Create_WritesAuthAndConfig_InRestrictedSandbox()
    {
        using var temp = new TempDir();
        var fs = new PhysicalFileSystem();
        var profileId = Guid.NewGuid();
        var authJsonBytes = Encoding.UTF8.GetBytes("{\"tokens\":{\"access_token\":\"fake\"}}");

        using var sandbox = ProfileSandbox.Create(temp.Root, profileId, authJsonBytes, fs);

        Assert.True(Directory.Exists(sandbox.DirectoryPath));
        Assert.True(File.Exists(sandbox.AuthJsonPath));
        Assert.True(File.Exists(sandbox.ConfigTomlPath));

        var writtenAuth = File.ReadAllBytes(sandbox.AuthJsonPath);
        Assert.Equal(authJsonBytes, writtenAuth);

        var configToml = File.ReadAllText(sandbox.ConfigTomlPath);
        Assert.Contains("cli_auth_credentials_store = \"file\"", configToml);

        Assert.Equal(Fingerprint.Compute(authJsonBytes), sandbox.InitialFingerprint);
    }

    [Fact]
    public void InspectMutation_DetectsCredentialRotation()
    {
        using var temp = new TempDir();
        var fs = new PhysicalFileSystem();
        var profileId = Guid.NewGuid();
        var originalBytes = Encoding.UTF8.GetBytes("{\"access_token\":\"original\"}");

        using var sandbox = ProfileSandbox.Create(temp.Root, profileId, originalBytes, fs);

        // Before modification: not mutated
        var (mutatedBefore, currentBytesBefore, _) = sandbox.InspectMutation();
        Assert.False(mutatedBefore);
        Assert.Equal(originalBytes, currentBytesBefore);

        // Simulate CLI updating auth.json in sandbox
        var updatedBytes = Encoding.UTF8.GetBytes("{\"access_token\":\"rotated_new_token\"}");
        File.WriteAllBytes(sandbox.AuthJsonPath, updatedBytes);

        // After modification: mutated detected
        var (mutatedAfter, currentBytesAfter, newFp) = sandbox.InspectMutation();
        Assert.True(mutatedAfter);
        Assert.Equal(updatedBytes, currentBytesAfter);
        Assert.Equal(Fingerprint.Compute(updatedBytes), newFp);
    }

    [Fact]
    public void Dispose_DeletesSandboxDirectory_LeavesWorkRootIntact()
    {
        using var temp = new TempDir();
        var fs = new PhysicalFileSystem();
        var profileId = Guid.NewGuid();
        var authJsonBytes = Encoding.UTF8.GetBytes("{\"test\":\"data\"}");

        string sandboxPath;
        using (var sandbox = ProfileSandbox.Create(temp.Root, profileId, authJsonBytes, fs))
        {
            sandboxPath = sandbox.DirectoryPath;
            Assert.True(Directory.Exists(sandboxPath));
        }

        // After dispose: sandbox directory is deleted
        Assert.False(Directory.Exists(sandboxPath));
        // Work root is still intact
        Assert.True(Directory.Exists(temp.Root));
    }
}

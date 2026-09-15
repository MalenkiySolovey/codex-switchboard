using CodexSwitcher.Core.Tests.TestSupport;

namespace CodexSwitcher.Core.Tests;

/// <summary>Verifica a detecção do perfil ativo por fingerprint e por sub (drift). Ver §4.6.</summary>
public sealed class ReconciliationServiceTests
{
    private static (ReconciliationService svc, CodexPaths paths, PhysicalFileSystem fs) Make(TempDir dir)
    {
        var fs = new PhysicalFileSystem();
        var paths = CodexPaths.ForHome(dir.Combine(".codex"));
        return (new ReconciliationService(fs, paths), paths, fs);
    }

    [Fact]
    public void ExactFingerprintMatch_SetsActive()
    {
        using var dir = new TempDir();
        var (svc, paths, fs) = Make(dir);
        var bytes = Sample.AuthJson(accountId: "A");
        var p = new ProfileMetadata { BlobFingerprint = Fingerprint.Compute(bytes) };
        fs.WriteAllBytesAtomic(paths.ActiveAuthPath, bytes);

        var result = svc.Reconcile([p]);

        Assert.Equal(ActiveMatch.Exact, result.Match);
        Assert.Equal(p.Id, result.ActiveProfileId);
        Assert.True(p.IsActive);
    }

    [Fact]
    public void SameSubButDifferentContent_ReportsDrift()
    {
        using var dir = new TempDir();
        var (svc, paths, fs) = Make(dir);
        var token = Sample.Jwt(sub: "sub-123");
        var stored = Sample.AuthJson(idToken: token, refreshToken: "rt-old");
        var active = Sample.AuthJson(idToken: token, refreshToken: "rt-new"); // mesmo sub, conteúdo novo
        var p = new ProfileMetadata { BlobFingerprint = Fingerprint.Compute(stored), AccountSub = "sub-123" };
        fs.WriteAllBytesAtomic(paths.ActiveAuthPath, active);

        var result = svc.Reconcile([p]);

        Assert.Equal(ActiveMatch.SameAccountDrifted, result.Match);
        Assert.True(p.IsActive);
    }

    [Fact]
    public void UnmanagedAccount_NoActiveProfile()
    {
        using var dir = new TempDir();
        var (svc, paths, fs) = Make(dir);
        var p = new ProfileMetadata { BlobFingerprint = "different", AccountSub = "sub-known" };
        fs.WriteAllBytesAtomic(paths.ActiveAuthPath, Sample.AuthJson(idToken: Sample.Jwt(sub: "sub-other")));

        var result = svc.Reconcile([p]);

        Assert.Equal(ActiveMatch.None, result.Match);
        Assert.Null(result.ActiveProfileId);
        Assert.False(p.IsActive);
    }

    [Fact]
    public void NoActiveSlotFile_NoActive()
    {
        using var dir = new TempDir();
        var (svc, _, _) = Make(dir);
        var p = new ProfileMetadata { BlobFingerprint = "x", IsActive = true };

        var result = svc.Reconcile([p]);

        Assert.Equal(ActiveMatch.None, result.Match);
        Assert.False(p.IsActive); // limpa flag obsoleta
    }
}

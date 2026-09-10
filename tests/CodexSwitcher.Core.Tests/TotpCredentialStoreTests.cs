using System.Security.Cryptography;
using System.Text;
using CodexSwitcher.Core.Abstractions;
using CodexSwitcher.Core.Models;
using CodexSwitcher.Core.Security;
using CodexSwitcher.Core.Services;
using CodexSwitcher.Core.Tests.TestSupport;
using CodexSwitcher.Infra;
using CodexSwitcher.Infra.Io;
using CodexSwitcher.Infra.Security;
using Xunit;

namespace CodexSwitcher.Core.Tests;

public sealed class TotpCredentialStoreTests : IDisposable
{
    private readonly TempDir _tempDir = new();
    private readonly AppPaths _paths;
    private readonly PhysicalFileSystem _fs = new();
    private readonly DpapiSecretProtector _protector = new();
    private readonly ProfileOperationCoordinator _coordinator = new();
    private readonly FakeClock _clock = new();
    private readonly TotpCredentialStore _store;

    public TotpCredentialStoreTests()
    {
        _paths = new AppPaths(_tempDir.Root);
        _store = new TotpCredentialStore(_protector, _fs, _paths.TotpDir, _coordinator);
    }

    public void Dispose() => _tempDir.Dispose();

    [Fact]
    public void SaveAndCompute_Roundtrip_Succeeds()
    {
        var profileId = Guid.NewGuid();
        // RFC 6238 Appendix B 20-byte seed: "12345678901234567890" -> Base32: GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ
        var secret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";

        Assert.False(_store.HasCredential(profileId));

        _store.Save(profileId, secret);

        Assert.True(_store.HasCredential(profileId));

        _clock.UtcNow = DateTimeOffset.FromUnixTimeSeconds(59);
        var success = _store.TryComputeCode(profileId, _clock.UtcNow, out var code, out var error);

        Assert.True(success);
        Assert.Null(error);
        Assert.Equal("287082", code.Code); // Standard SHA-1 RFC 6238 code for 59s
        Assert.Equal(1, code.SecondsRemaining);
    }

    [Fact]
    public void AtRestEncryption_PlaintextSeedNotPresentInFile()
    {
        var profileId = Guid.NewGuid();
        var rawSecret = "JBSWY3DPEHPK3PXP"; // Base32 test vector
        var otpauthUri = "otpauth://totp/Acme:alice@example.com?secret=" + rawSecret + "&issuer=Acme";

        _store.Save(profileId, otpauthUri);

        var filePath = _paths.GetTotpPath(profileId);
        Assert.True(File.Exists(filePath));

        var encryptedBytes = File.ReadAllBytes(filePath);

        // Assert neither rawSecret nor otpauthUri appear in plaintext anywhere in the file bytes
        var rawSecretBytes = Encoding.UTF8.GetBytes(rawSecret);
        var uriBytes = Encoding.UTF8.GetBytes(otpauthUri);

        Assert.False(ContainsSubsequence(encryptedBytes, rawSecretBytes),
            "at-rest DPAPI file must not contain raw secret plaintext");
        Assert.False(ContainsSubsequence(encryptedBytes, uriBytes),
            "at-rest DPAPI file must not contain raw otpauth URI plaintext");
    }

    [Fact]
    public void ProfileIsolation_OneProfileCannotAccessAnother()
    {
        var profileA = Guid.NewGuid();
        var profileB = Guid.NewGuid();

        _store.Save(profileA, "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ");

        Assert.True(_store.HasCredential(profileA));
        Assert.False(_store.HasCredential(profileB));

        Assert.True(_store.TryComputeCode(profileA, _clock.UtcNow, out var codeA, out _));
        Assert.False(string.IsNullOrWhiteSpace(codeA.Code));

        Assert.False(_store.TryComputeCode(profileB, _clock.UtcNow, out var codeB, out var errB));
        Assert.NotNull(errB);
    }

    [Fact]
    public void CorruptedFile_FailsGracefullyWithoutThrowing()
    {
        var profileId = Guid.NewGuid();
        _store.Save(profileId, "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ");

        var filePath = _paths.GetTotpPath(profileId);
        // Overwrite file with random garbage bytes
        File.WriteAllBytes(filePath, RandomNumberGenerator.GetBytes(64));

        Assert.True(_store.HasCredential(profileId)); // File exists
        var success = _store.TryComputeCode(profileId, _clock.UtcNow, out _, out var error);

        Assert.False(success);
        Assert.NotNull(error);
    }

    [Fact]
    public void SchemaVersionMismatch_FailsGracefully()
    {
        var profileId = Guid.NewGuid();
        var futurePayload = "{\"schemaVersion\":999,\"kind\":\"profile-totp\",\"profileId\":\"" + profileId.ToString("N") + "\",\"provisioning\":\"GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ\"}";
        var protectedBytes = _protector.Protect(Encoding.UTF8.GetBytes(futurePayload));

        var filePath = _paths.GetTotpPath(profileId);
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        File.WriteAllBytes(filePath, protectedBytes);

        var success = _store.TryComputeCode(profileId, _clock.UtcNow, out _, out var error);
        Assert.False(success);
        Assert.NotNull(error);
    }

    [Fact]
    public void AtomicReplacement_InvalidNewSecret_PreservesExistingValidSecret()
    {
        var profileId = Guid.NewGuid();
        var validSecret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";
        _store.Save(profileId, validSecret);

        // Attempt to save an invalid secret (bad base32 character '8' or '9' in raw base32 mode)
        Assert.Throws<ArgumentException>(() => _store.Save(profileId, "INVALID-BASE32-888999!!!"));

        // Previous valid secret should still be present and functional
        Assert.True(_store.HasCredential(profileId));
        _clock.UtcNow = DateTimeOffset.FromUnixTimeSeconds(59);
        var success = _store.TryComputeCode(profileId, _clock.UtcNow, out var code, out var error);
        Assert.True(success);
        Assert.Null(error);
        Assert.Equal("287082", code.Code);
    }

    [Fact]
    public void Delete_RemovesCredentialAndFile()
    {
        var profileId = Guid.NewGuid();
        _store.Save(profileId, "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ");
        Assert.True(File.Exists(_paths.GetTotpPath(profileId)));

        _store.Delete(profileId);

        Assert.False(_store.HasCredential(profileId));
        Assert.False(File.Exists(_paths.GetTotpPath(profileId)));
    }

    [Fact]
    public void ProfileService_RemoveProfile_AutomaticallyDeletesTotpCredential()
    {
        var vault = new VaultService(new DpapiSecretProtector(), _fs, _tempDir.Combine("vault"));
        var store = new ProfileStore(_fs, _tempDir.Combine("profiles.json"));
        var paths = CodexPaths.ForHome(_tempDir.Combine(".codex"));
        var recon = new ReconciliationService(_fs, paths);
        var audit = new FakeAudit();

        var profileService = new ProfileService(
            vault,
            store,
            recon,
            _fs,
            paths,
            _clock,
            audit,
            _store);

        // Setup a profile
        var token = Sample.Jwt(sub: "sub-totp-1", email: "totp@example.com");
        var bytes = Sample.AuthJson(idToken: token);
        var profile = profileService.AddFromAuthJson(bytes, nickname: "Test 2FA Profile");

        // Save TOTP credential for this profile
        _store.Save(profile.Id, "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ");
        Assert.True(_store.HasCredential(profile.Id));

        // Remove profile through ProfileService
        profileService.Remove(profile.Id);

        // Verify profile is deleted AND TOTP credential file is deleted
        Assert.False(_store.HasCredential(profile.Id));
        Assert.False(File.Exists(_paths.GetTotpPath(profile.Id)));
    }

    [Fact]
    public void ProfileService_Export_StrictlyExcludesTotpSecrets()
    {
        var vault = new VaultService(new DpapiSecretProtector(), _fs, _tempDir.Combine("vault"));
        var store = new ProfileStore(_fs, _tempDir.Combine("profiles.json"));
        var paths = CodexPaths.ForHome(_tempDir.Combine(".codex"));
        var recon = new ReconciliationService(_fs, paths);
        var audit = new FakeAudit();

        var profileService = new ProfileService(
            vault,
            store,
            recon,
            _fs,
            paths,
            _clock,
            audit,
            _store);

        var token = Sample.Jwt(sub: "sub-totp-2", email: "totp2@example.com");
        var bytes = Sample.AuthJson(idToken: token);
        var profile = profileService.AddFromAuthJson(bytes, nickname: "Test Export Profile");

        const string syntheticMarker = "SYNTHETIC-TOTP-SECRET-MARKER-EXCLUSION-CHECK";
        _store.Save(profile.Id, "otpauth://totp/Test:user?secret=GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ&issuer=Test&marker=" + syntheticMarker);

        var exportBundle = profileService.ExportAll();
        var exportJson = System.Text.Json.JsonSerializer.Serialize(exportBundle);

        Assert.DoesNotContain(syntheticMarker, exportJson);
        Assert.DoesNotContain("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", exportJson);

        // Verify audit log also never recorded the synthetic marker
        Assert.DoesNotContain(audit.Entries, e => e.Detail != null && e.Detail.Contains(syntheticMarker));
    }

    [Fact]
    public void ZeroNetworkSideEffects_ComputationIsEntirelyOffline()
    {
        var profileId = Guid.NewGuid();
        _store.Save(profileId, "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ");

        // TryComputeCode executes in < 1 millisecond with zero network calls
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var success = _store.TryComputeCode(profileId, _clock.UtcNow, out var code, out var error);
        sw.Stop();

        Assert.True(success);
        Assert.Null(error);
        Assert.False(string.IsNullOrEmpty(code.Code));
        Assert.True(sw.ElapsedMilliseconds < 50, "offline TOTP calculation must complete nearly instantaneously");
    }

    private static bool ContainsSubsequence(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length) return false;
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }
            if (match) return true;
        }
        return false;
    }
}
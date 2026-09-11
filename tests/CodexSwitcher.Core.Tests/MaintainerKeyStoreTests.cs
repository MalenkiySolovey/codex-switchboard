using System.Security.Cryptography;
using CodexSwitcher.CatalogSigner;
using CodexSwitcher.Core.Catalog;
using CodexSwitcher.Core.Tests.TestSupport;
using Xunit;

namespace CodexSwitcher.Core.Tests;

public sealed class MaintainerKeyStoreTests
{
    [Fact]
    public void DefaultKeyStore_ContainsValidOfficialKey_MatchingPublicFingerprint()
    {
        // The official maintainer signing key is protected by Windows DPAPI and stored
        // locally on the maintainer machine. In environments where the key is not provisioned
        // (such as CI runners), this verification is skipped.
        if (!MaintainerKeyStore.KeyExists())
        {
            return;
        }

        using var key = MaintainerKeyStore.LoadPrivateKey();
        Assert.NotNull(key);

        var pubBytes = key.ExportSubjectPublicKeyInfo();
        var pubB64 = Convert.ToBase64String(pubBytes);
        Assert.Equal(CatalogSecurityConstants.OfficialCatalogPublicKeyBase64, pubB64);
    }

    [Fact]
    public void MaintainerKeyStore_CustomPath_RoundTripAndSign()
    {
        using var temp = new TempDir();
        var customKeyPath = temp.Combine("test-maintainer-key.bin");

        using var originalKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        MaintainerKeyStore.SavePrivateKey(originalKey, customKeyPath);

        Assert.True(File.Exists(customKeyPath));

        // Disk bytes must be encrypted, not plaintext PKCS8
        var diskBytes = File.ReadAllBytes(customKeyPath);
        var originalPkcs8 = originalKey.ExportPkcs8PrivateKey();
        Assert.False(diskBytes.SequenceEqual(originalPkcs8));

        using var loadedKey = MaintainerKeyStore.LoadPrivateKey(customKeyPath);
        Assert.NotNull(loadedKey);
        Assert.Equal(Convert.ToBase64String(originalKey.ExportSubjectPublicKeyInfo()),
                     Convert.ToBase64String(loadedKey.ExportSubjectPublicKeyInfo()));

        var testPayload = "CustomPayloadToSign"u8.ToArray();
        var sigB64 = MaintainerKeyStore.SignCatalog(testPayload, customKeyPath);

        var verified = CatalogSignatureVerifier.VerifySignatureWithPublicKey(
            testPayload,
            Convert.FromBase64String(sigB64),
            Convert.ToBase64String(originalKey.ExportSubjectPublicKeyInfo()));
        Assert.True(verified);
    }

    [Fact]
    public void LoadPrivateKey_NonExistentPath_ReturnsNull()
    {
        var result = MaintainerKeyStore.LoadPrivateKey(@"C:\non\existent\path\key.bin");
        Assert.Null(result);
    }
}

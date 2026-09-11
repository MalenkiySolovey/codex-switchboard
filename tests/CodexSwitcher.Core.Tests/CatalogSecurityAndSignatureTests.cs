using System.Security.Cryptography;
using System.Text;
using CodexSwitcher.Core.Catalog;
using Xunit;

namespace CodexSwitcher.Core.Tests;

public sealed class CatalogSecurityAndSignatureTests
{
    [Fact]
    public void OfficialCatalog_Signature_VerifiesSuccessfully()
    {
        var catalogPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "catalog", "providers.catalog.json");
        var sigPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "catalog", "providers.catalog.sig");

        Assert.True(File.Exists(catalogPath), $"Official catalog not found at {catalogPath}");
        Assert.True(File.Exists(sigPath), $"Official catalog sig not found at {sigPath}");

        var catalogBytes = File.ReadAllBytes(catalogPath);
        var sigBase64 = File.ReadAllText(sigPath).Trim();

        var verified = CatalogSignatureVerifier.VerifyOfficialSignature(catalogBytes, sigBase64);
        Assert.True(verified, "Detached signature on official providers.catalog.json failed to verify.");
    }

    [Fact]
    public void TamperedCatalogPayload_FailsVerification()
    {
        var catalogPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "catalog", "providers.catalog.json");
        var sigPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "catalog", "providers.catalog.sig");

        var catalogBytes = File.ReadAllBytes(catalogPath);
        var sigBase64 = File.ReadAllText(sigPath).Trim();

        // Tamper by 1 byte
        var tamperedBytes = (byte[])catalogBytes.Clone();
        tamperedBytes[^1] ^= 0xFF;

        var verified = CatalogSignatureVerifier.VerifyOfficialSignature(tamperedBytes, sigBase64);
        Assert.False(verified, "Tampered catalog payload must fail signature verification.");
    }

    [Fact]
    public void TamperedSignature_FailsVerification()
    {
        var catalogPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "catalog", "providers.catalog.json");
        var sigPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "catalog", "providers.catalog.sig");

        var catalogBytes = File.ReadAllBytes(catalogPath);
        var sigBytes = Convert.FromBase64String(File.ReadAllText(sigPath).Trim());
        sigBytes[0] ^= 0xFF;
        var tamperedSigBase64 = Convert.ToBase64String(sigBytes);

        var verified = CatalogSignatureVerifier.VerifyOfficialSignature(catalogBytes, tamperedSigBase64);
        Assert.False(verified, "Tampered signature string must fail verification.");
    }

    [Fact]
    public void InvalidBase64Signature_ReturnsFalseGracefully()
    {
        var bytes = Encoding.UTF8.GetBytes("{ \"test\": 1 }");
        var verified = CatalogSignatureVerifier.VerifyOfficialSignature(bytes, "not-valid-base64!@#$");
        Assert.False(verified);
    }

    [Fact]
    public void ForeignPublicKey_DoesNotVerifyOfficialSignature()
    {
        var catalogPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "catalog", "providers.catalog.json");
        var sigPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "catalog", "providers.catalog.sig");

        var catalogBytes = File.ReadAllBytes(catalogPath);
        var sigBytes = Convert.FromBase64String(File.ReadAllText(sigPath).Trim());

        using var foreignEcdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var foreignPubBase64 = Convert.ToBase64String(foreignEcdsa.ExportSubjectPublicKeyInfo());

        var verified = CatalogSignatureVerifier.VerifySignatureWithPublicKey(catalogBytes, sigBytes, foreignPubBase64);
        Assert.False(verified, "Official signature must not verify with an arbitrary foreign key.");
    }
}

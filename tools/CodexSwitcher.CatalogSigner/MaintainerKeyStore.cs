using System.Security.Cryptography;
using CodexSwitcher.Core.Catalog;
using CodexSwitcher.Infra.Security;

namespace CodexSwitcher.CatalogSigner;

/// <summary>
/// Maintainer-only secure key store for the official catalog signing private key.
/// Strictly isolated from user runtime data (%LOCALAPPDATA%\CodexSwitchboardDev\signing\).
/// Protected at rest using DPAPI CurrentUser and restrictive directory ACLs.
/// </summary>
public static class MaintainerKeyStore
{
    public static string DefaultSigningDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexSwitchboardDev", "signing");

    public static string DefaultKeyPath =>
        Path.Combine(DefaultSigningDir, "official-catalog-key.bin");

    public static bool KeyExists(string? customPath = null) =>
        File.Exists(customPath ?? DefaultKeyPath);

    /// <summary>
    /// Encrypts and stores the private ECDSA key in the maintainer-only DPAPI store.
    /// </summary>
    public static void SavePrivateKey(ECDsa key, string? customPath = null)
    {
        ArgumentNullException.ThrowIfNull(key);

        var path = customPath ?? DefaultKeyPath;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
            DirectoryHardening.TryRestrictToCurrentUser(dir);
        }

        var pkcs8Bytes = key.ExportPkcs8PrivateKey();
        try
        {
            var cipher = ProtectedData.Protect(pkcs8Bytes, null, DataProtectionScope.CurrentUser);
            var tempPath = path + ".tmp." + Guid.NewGuid().ToString("N");
            File.WriteAllBytes(tempPath, cipher);
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8Bytes);
        }
    }

    /// <summary>
    /// Loads and decrypts the private ECDSA key on demand.
    /// </summary>
    public static ECDsa? LoadPrivateKey(string? customPath = null)
    {
        var path = customPath ?? DefaultKeyPath;
        if (!File.Exists(path))
            return null;

        var cipher = File.ReadAllBytes(path);
        byte[] plaintext;
        try
        {
            plaintext = ProtectedData.Unprotect(cipher, null, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException)
        {
            return null;
        }

        try
        {
            var ecdsa = ECDsa.Create();
            ecdsa.ImportPkcs8PrivateKey(plaintext, out _);
            return ecdsa;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>
    /// Imports a private key from an existing PEM file, validates public key continuity,
    /// protects it in the maintainer store, and verifies a round-trip test.
    /// </summary>
    public static (bool Success, string Message) ImportFromPem(string pemPath, string? targetPath = null)
    {
        if (!File.Exists(pemPath))
            return (false, $"Source PEM file not found: {pemPath}");

        var pemText = File.ReadAllText(pemPath);
        using var ecdsa = ECDsa.Create();
        try
        {
            ecdsa.ImportFromPem(pemText);
        }
        catch (Exception ex)
        {
            return (false, $"Failed to parse PEM key: {ex.Message}");
        }

        // Verify public key continuity
        var derivedPubBytes = ecdsa.ExportSubjectPublicKeyInfo();
        var derivedPubB64 = Convert.ToBase64String(derivedPubBytes);

        if (!string.Equals(derivedPubB64, CatalogSecurityConstants.OfficialCatalogPublicKeyBase64, StringComparison.Ordinal))
        {
            return (false, "Public key mismatch! The private key does not correspond to the embedded official public key.");
        }

        // Save protected key
        SavePrivateKey(ecdsa, targetPath);

        // Verify round-trip loading
        using var roundtripKey = LoadPrivateKey(targetPath);
        if (roundtripKey == null)
            return (false, "Failed to reload key from DPAPI store after save.");

        // Verify test signing
        var testPayload = "CodexSwitchboard.CatalogSigner.Validation"u8.ToArray();
        var sigBytes = roundtripKey.SignData(testPayload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var sigB64 = Convert.ToBase64String(sigBytes);

        if (!CatalogSignatureVerifier.VerifyOfficialSignature(testPayload, sigB64))
        {
            return (false, "Round-trip signature verification failed with embedded public key.");
        }

        return (true, $"Private key successfully imported and verified in maintainer DPAPI store at {targetPath ?? DefaultKeyPath}.");
    }

    /// <summary>
    /// Signs catalog bytes using the protected maintainer key.
    /// </summary>
    public static string SignCatalog(byte[] catalogBytes, string? customPath = null)
    {
        using var key = LoadPrivateKey(customPath);
        if (key == null)
            throw new InvalidOperationException($"Signing key not found or decryption failed at {customPath ?? DefaultKeyPath}.");

        var sigBytes = key.SignData(catalogBytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return Convert.ToBase64String(sigBytes);
    }
}

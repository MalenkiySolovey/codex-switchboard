using System.Security.Cryptography;

namespace CodexSwitcher.Core.Catalog;

/// <summary>
/// Cryptographic verifier for detached signatures on official catalog distributions.
/// Enforces non-repudiation and authenticity before external catalog activation.
/// </summary>
public static class CatalogSignatureVerifier
{
    /// <summary>
    /// Verifies the detached signature of catalog bytes using the embedded official public key.
    /// </summary>
    public static bool VerifyOfficialSignature(byte[] catalogBytes, string base64Signature)
    {
        if (catalogBytes == null || catalogBytes.Length == 0 || string.IsNullOrWhiteSpace(base64Signature))
            return false;

        byte[] signatureBytes;
        try
        {
            signatureBytes = Convert.FromBase64String(base64Signature.Trim());
        }
        catch (FormatException)
        {
            return false;
        }

        return VerifySignatureWithPublicKey(catalogBytes, signatureBytes, CatalogSecurityConstants.OfficialCatalogPublicKeyBase64);
    }

    /// <summary>
    /// Verifies the signature of data using a provided SubjectPublicKeyInfo Base64 key.
    /// Supports IEEE P1363 (standard 64-byte raw format) and DER sequence formats.
    /// </summary>
    public static bool VerifySignatureWithPublicKey(byte[] data, byte[] signatureBytes, string publicKeyBase64)
    {
        if (data == null || signatureBytes == null || signatureBytes.Length == 0 || string.IsNullOrWhiteSpace(publicKeyBase64))
            return false;

        try
        {
            var pubKeyBytes = Convert.FromBase64String(publicKeyBase64.Trim());
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(pubKeyBytes, out _);

            // Attempt IEEE P1363 format (default for 64-byte ECDSA P-256 signatures)
            if (signatureBytes.Length == 64)
            {
                if (ecdsa.VerifyData(data, signatureBytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                    return true;
            }

            // Also attempt RFC 3279 DER format
            if (ecdsa.VerifyData(data, signatureBytes, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
                return true;

            return false;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}

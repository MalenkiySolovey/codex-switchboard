using CodexSwitcher.Core.Accounts.Contracts;
using CodexSwitcher.Core.Accounts.Formatting;
using CodexSwitcher.Core.Accounts.Models;
using CodexSwitcher.Core.Accounts.Services;
using CodexSwitcher.Core.Common.Dispatcher;
using CodexSwitcher.Core.Common.Enums;
using CodexSwitcher.Core.Common.Environment;
using CodexSwitcher.Core.Common.Errors;
using CodexSwitcher.Core.Common.Lifecycle;
using CodexSwitcher.Core.Common.Logging;
using CodexSwitcher.Core.Common.Storage;
using CodexSwitcher.Core.Common.Time;
using CodexSwitcher.Core.Providers.Catalog;
using CodexSwitcher.Core.Providers.Contracts;
using CodexSwitcher.Core.Providers.Models;
using CodexSwitcher.Core.Providers.Services;
using CodexSwitcher.Core.Routing.Contracts;
using CodexSwitcher.Core.Routing.Models;
using CodexSwitcher.Core.Routing.Services;
using CodexSwitcher.Core.Security.Secrets;
using CodexSwitcher.Core.Security.Totp;
using CodexSwitcher.Core.Security.Verification;
using CodexSwitcher.Core.Settings.Contracts;
using CodexSwitcher.Core.Settings.Models;
using CodexSwitcher.Core.Threads.Contracts;
using CodexSwitcher.Core.Threads.Models;
using CodexSwitcher.Core.Transfer.Contracts;
using CodexSwitcher.Core.Transfer.Models;
using CodexSwitcher.Core.Transfer.Services;
using CodexSwitcher.Core.Usage.Contracts;
using CodexSwitcher.Core.Usage.Formatting;
using CodexSwitcher.Core.Usage.Models;
using CodexSwitcher.Core.Usage.Services;
using System.Security.Cryptography;

namespace CodexSwitcher.Core.Providers.Catalog;

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

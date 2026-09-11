namespace CodexSwitcher.Core.Catalog;

/// <summary>
/// Cryptographic public verification keys and security parameters for official catalog distribution.
/// </summary>
public static class CatalogSecurityConstants
{
    /// <summary>
    /// Official embedded ECDSA P-256 public verification key in SubjectPublicKeyInfo DER Base64 format.
    /// The private signing key never exists in the product repository.
    /// </summary>
    public const string OfficialCatalogPublicKeyBase64 =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEJrh422tGrwF4SF0aHyG4ImX1T6nb3SkPwZ1/3PPg9rXES5wsX7Jn0etKUY9e2SQWg+dQmlY0AucPpsO4Kl7PDQ==";

    /// <summary>Maximum allowed catalog file size (5 MB) to prevent denial of service.</summary>
    public const int MaxCatalogSizeBytes = 5 * 1024 * 1024;

    /// <summary>Maximum allowed response size for automatic capability probes (512 KB).</summary>
    public const int MaxProbeResponseSizeBytes = 512 * 1024;

    /// <summary>Supported schema version for this application binary.</summary>
    public const int SupportedSchemaVersion = 1;
}

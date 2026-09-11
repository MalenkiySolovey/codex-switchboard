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
namespace CodexSwitcher.Core.Providers.Catalog;

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

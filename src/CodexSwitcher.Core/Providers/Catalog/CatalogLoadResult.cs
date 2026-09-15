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

public enum CatalogLoadStatus
{
    Supported = 0,
    TooNewSchema = 1,
    RequiresNewerApp = 2,
    InvalidJson = 3,
    InvalidSchema = 4,
    SignatureInvalid = 5,
    MissingSignature = 6,
    SizeLimitExceeded = 7,
    FileNotFound = 8,
}

public enum CatalogSourceLayer
{
    EmbeddedBootstrap = 0,
    OfficialExternal = 1,
    LastKnownGood = 2,
    LocalCustom = 3,
}

/// <summary>
/// Detailed result of loading and validating a provider catalog across all layers.
/// </summary>
public sealed record CatalogLoadResult
{
    public required ProviderCatalog Catalog { get; init; }
    public required CatalogSourceLayer ActiveLayer { get; init; }
    public required CatalogLoadStatus Status { get; init; }
    public string? DiagnosticMessage { get; init; }
    public int CatalogVersion => Catalog.CatalogVersion;
    public int SchemaVersion => Catalog.SchemaVersion;
    public int ProviderCount => Catalog.Providers.Count;
}

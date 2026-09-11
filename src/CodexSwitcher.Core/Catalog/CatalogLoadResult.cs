namespace CodexSwitcher.Core.Catalog;

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

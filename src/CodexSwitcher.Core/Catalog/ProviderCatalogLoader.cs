using System.Reflection;
using System.Text.Json;
using CodexSwitcher.Core.Abstractions;

namespace CodexSwitcher.Core.Catalog;

/// <summary>
/// Multilayer provider catalog loader with cryptographic signature verification,
/// last-known-good resilience, local catalog overlay, and embedded fallback.
/// Guarantees that invalid or malicious external catalogs never brick application startup.
/// </summary>
public sealed class ProviderCatalogLoader
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly IFileSystem _fs;
    private readonly string _catalogPath;
    private readonly string _catalogSigPath;
    private readonly string _catalogPreviousPath;
    private readonly string _localCatalogPath;
    private readonly string _currentAppVersion;

    public ProviderCatalogLoader(
        IFileSystem fs,
        string catalogPath,
        string catalogSigPath,
        string catalogPreviousPath,
        string localCatalogPath,
        string currentAppVersion = "0.1.3")
    {
        _fs = fs ?? throw new ArgumentNullException(nameof(fs));
        _catalogPath = catalogPath ?? throw new ArgumentNullException(nameof(catalogPath));
        _catalogSigPath = catalogSigPath ?? throw new ArgumentNullException(nameof(catalogSigPath));
        _catalogPreviousPath = catalogPreviousPath ?? throw new ArgumentNullException(nameof(catalogPreviousPath));
        _localCatalogPath = localCatalogPath ?? throw new ArgumentNullException(nameof(localCatalogPath));
        _currentAppVersion = currentAppVersion ?? "0.1.3";
    }

    /// <summary>
    /// Loads the active provider catalog, executing the full fallback ladder:
    /// Official External -> Last-Known-Good -> Embedded Bootstrap.
    /// Safely overlays valid local custom descriptors from providers.local.json.
    /// </summary>
    public CatalogLoadResult LoadCatalog()
    {
        // Step 1: Attempt Official External Catalog
        if (_fs.FileExists(_catalogPath))
        {
            var externalResult = TryLoadOfficialExternal();
            if (externalResult != null)
                return OverlayLocalCatalogIfPresent(externalResult);
        }

        // Step 2: Attempt Last-Known-Good Catalog
        if (_fs.FileExists(_catalogPreviousPath))
        {
            var lkgResult = TryLoadLastKnownGood();
            if (lkgResult != null)
                return OverlayLocalCatalogIfPresent(lkgResult);
        }

        // Step 3: Fall back to Embedded Bootstrap Catalog
        var bootstrapResult = LoadBootstrapCatalog();
        return OverlayLocalCatalogIfPresent(bootstrapResult);
    }

    /// <summary>
    /// Loads the embedded bootstrap catalog compiled into the assembly.
    /// This is guaranteed to be available and safe.
    /// </summary>
    public CatalogLoadResult LoadBootstrapCatalog()
    {
        try
        {
            var assembly = typeof(ProviderCatalogLoader).Assembly;
            var resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("providers.catalog.json", StringComparison.OrdinalIgnoreCase));

            if (resourceName == null)
            {
                // Fallback: minimal built-in catalog if resource not found in build
                return new CatalogLoadResult
                {
                    Catalog = CreateMinimalFallbackCatalog(),
                    ActiveLayer = CatalogSourceLayer.EmbeddedBootstrap,
                    Status = CatalogLoadStatus.Supported,
                    DiagnosticMessage = "Loaded hardcoded minimal fallback catalog.",
                };
            }

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                return new CatalogLoadResult
                {
                    Catalog = CreateMinimalFallbackCatalog(),
                    ActiveLayer = CatalogSourceLayer.EmbeddedBootstrap,
                    Status = CatalogLoadStatus.Supported,
                    DiagnosticMessage = "Manifest resource stream was null.",
                };
            }

            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();
            var catalog = JsonSerializer.Deserialize<ProviderCatalog>(json, SerializerOptions);

            if (catalog != null && ValidateCatalogSemantics(catalog, out _))
            {
                return new CatalogLoadResult
                {
                    Catalog = catalog,
                    ActiveLayer = CatalogSourceLayer.EmbeddedBootstrap,
                    Status = CatalogLoadStatus.Supported,
                };
            }
        }
        catch (Exception ex)
        {
            // Even if embedded loading encounters an issue, return safe hardcoded catalog
            return new CatalogLoadResult
            {
                Catalog = CreateMinimalFallbackCatalog(),
                ActiveLayer = CatalogSourceLayer.EmbeddedBootstrap,
                Status = CatalogLoadStatus.Supported,
                DiagnosticMessage = $"Embedded load exception: {ex.Message}",
            };
        }

        return new CatalogLoadResult
        {
            Catalog = CreateMinimalFallbackCatalog(),
            ActiveLayer = CatalogSourceLayer.EmbeddedBootstrap,
            Status = CatalogLoadStatus.Supported,
        };
    }

    /// <summary>
    /// Validates and atomically installs a candidate catalog and signature file as the official external catalog.
    /// Backs up the current active catalog to last-known-good before activation.
    /// </summary>
    public bool TryInstallExternalCatalog(byte[] candidateCatalogBytes, string base64Signature, out string? errorMessage)
    {
        errorMessage = null;

        var validation = ValidateCandidate(candidateCatalogBytes, base64Signature);
        if (validation.Status != CatalogLoadStatus.Supported || validation.Catalog == null)
        {
            errorMessage = validation.DiagnosticMessage ?? $"Catalog validation failed: {validation.Status}";
            return false;
        }

        try
        {
            var dir = Path.GetDirectoryName(_catalogPath);
            if (!string.IsNullOrEmpty(dir))
                _fs.CreateDirectory(dir);

            // Back up current external catalog to previous/last-known-good
            if (_fs.FileExists(_catalogPath))
            {
                try
                {
                    var existingBytes = _fs.ReadAllBytes(_catalogPath);
                    _fs.WriteAllBytesAtomic(_catalogPreviousPath, existingBytes);
                }
                catch
                {
                    // Non-fatal if backup fails, proceed with safe atomic write
                }
            }

            // Atomically write new catalog and signature
            _fs.WriteAllBytesAtomic(_catalogPath, candidateCatalogBytes);
            _fs.WriteAllTextAtomic(_catalogSigPath, base64Signature.Trim());
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = $"Failed to write catalog files: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Validates candidate catalog bytes and signature without installing or activating them.
    /// </summary>
    public CatalogLoadResult ValidateCandidate(byte[] catalogBytes, string? base64Signature)
    {
        if (catalogBytes == null || catalogBytes.Length == 0)
        {
            return new CatalogLoadResult
            {
                Catalog = new ProviderCatalog(),
                ActiveLayer = CatalogSourceLayer.OfficialExternal,
                Status = CatalogLoadStatus.FileNotFound,
                DiagnosticMessage = "Catalog content is empty or null.",
            };
        }

        if (catalogBytes.Length > CatalogSecurityConstants.MaxCatalogSizeBytes)
        {
            return new CatalogLoadResult
            {
                Catalog = new ProviderCatalog(),
                ActiveLayer = CatalogSourceLayer.OfficialExternal,
                Status = CatalogLoadStatus.SizeLimitExceeded,
                DiagnosticMessage = $"Catalog file exceeds size limit of {CatalogSecurityConstants.MaxCatalogSizeBytes} bytes.",
            };
        }

        if (string.IsNullOrWhiteSpace(base64Signature))
        {
            return new CatalogLoadResult
            {
                Catalog = new ProviderCatalog(),
                ActiveLayer = CatalogSourceLayer.OfficialExternal,
                Status = CatalogLoadStatus.MissingSignature,
                DiagnosticMessage = "Official catalog requires a valid detached signature (.sig).",
            };
        }

        if (!CatalogSignatureVerifier.VerifyOfficialSignature(catalogBytes, base64Signature))
        {
            return new CatalogLoadResult
            {
                Catalog = new ProviderCatalog(),
                ActiveLayer = CatalogSourceLayer.OfficialExternal,
                Status = CatalogLoadStatus.SignatureInvalid,
                DiagnosticMessage = "Catalog signature verification failed against embedded official public key.",
            };
        }

        ProviderCatalog? catalog;
        try
        {
            catalog = JsonSerializer.Deserialize<ProviderCatalog>(catalogBytes, SerializerOptions);
        }
        catch (Exception ex)
        {
            return new CatalogLoadResult
            {
                Catalog = new ProviderCatalog(),
                ActiveLayer = CatalogSourceLayer.OfficialExternal,
                Status = CatalogLoadStatus.InvalidJson,
                DiagnosticMessage = $"JSON deserialization error: {ex.Message}",
            };
        }

        if (catalog == null)
        {
            return new CatalogLoadResult
            {
                Catalog = new ProviderCatalog(),
                ActiveLayer = CatalogSourceLayer.OfficialExternal,
                Status = CatalogLoadStatus.InvalidSchema,
                DiagnosticMessage = "Catalog deserialized to null.",
            };
        }

        if (catalog.SchemaVersion > CatalogSecurityConstants.SupportedSchemaVersion)
        {
            return new CatalogLoadResult
            {
                Catalog = catalog,
                ActiveLayer = CatalogSourceLayer.OfficialExternal,
                Status = CatalogLoadStatus.TooNewSchema,
                DiagnosticMessage = $"Catalog schema version {catalog.SchemaVersion} exceeds maximum supported version {CatalogSecurityConstants.SupportedSchemaVersion}.",
            };
        }

        if (IsAppVersionTooOld(catalog.MinAppVersion, _currentAppVersion))
        {
            return new CatalogLoadResult
            {
                Catalog = catalog,
                ActiveLayer = CatalogSourceLayer.OfficialExternal,
                Status = CatalogLoadStatus.RequiresNewerApp,
                DiagnosticMessage = $"Catalog requires minimum app version {catalog.MinAppVersion}, current app is {_currentAppVersion}.",
            };
        }

        if (!ValidateCatalogSemantics(catalog, out var semanticError))
        {
            return new CatalogLoadResult
            {
                Catalog = catalog,
                ActiveLayer = CatalogSourceLayer.OfficialExternal,
                Status = CatalogLoadStatus.InvalidSchema,
                DiagnosticMessage = semanticError,
            };
        }

        return new CatalogLoadResult
        {
            Catalog = catalog,
            ActiveLayer = CatalogSourceLayer.OfficialExternal,
            Status = CatalogLoadStatus.Supported,
        };
    }

    private CatalogLoadResult? TryLoadOfficialExternal()
    {
        try
        {
            if (!_fs.FileExists(_catalogSigPath))
                return null;

            var catalogBytes = _fs.ReadAllBytes(_catalogPath);
            var sig = _fs.ReadAllText(_catalogSigPath);

            var validation = ValidateCandidate(catalogBytes, sig);
            if (validation.Status == CatalogLoadStatus.Supported && validation.Catalog != null)
            {
                // Update previous known-good asynchronously or atomically if different
                TryUpdateLastKnownGood(catalogBytes);
                return validation;
            }
        }
        catch
        {
            // Any IO or processing failure falls back to last-known-good
        }

        return null;
    }

    private CatalogLoadResult? TryLoadLastKnownGood()
    {
        try
        {
            var lkgBytes = _fs.ReadAllBytes(_catalogPreviousPath);
            if (lkgBytes.Length > CatalogSecurityConstants.MaxCatalogSizeBytes)
                return null;

            var catalog = JsonSerializer.Deserialize<ProviderCatalog>(lkgBytes, SerializerOptions);
            if (catalog != null &&
                catalog.SchemaVersion <= CatalogSecurityConstants.SupportedSchemaVersion &&
                !IsAppVersionTooOld(catalog.MinAppVersion, _currentAppVersion) &&
                ValidateCatalogSemantics(catalog, out _))
            {
                return new CatalogLoadResult
                {
                    Catalog = catalog,
                    ActiveLayer = CatalogSourceLayer.LastKnownGood,
                    Status = CatalogLoadStatus.Supported,
                    DiagnosticMessage = "Loaded from last-known-good fallback catalog.",
                };
            }
        }
        catch
        {
            // Fall through to embedded bootstrap
        }

        return null;
    }

    private CatalogLoadResult OverlayLocalCatalogIfPresent(CatalogLoadResult baseResult)
    {
        if (!_fs.FileExists(_localCatalogPath))
            return baseResult;

        try
        {
            var localBytes = _fs.ReadAllBytes(_localCatalogPath);
            if (localBytes.Length > CatalogSecurityConstants.MaxCatalogSizeBytes)
                return baseResult;

            var localCatalog = JsonSerializer.Deserialize<ProviderCatalog>(localBytes, SerializerOptions);
            if (localCatalog?.Providers == null || localCatalog.Providers.Count == 0)
                return baseResult;

            // Deep clone or construct a merged catalog
            var merged = new ProviderCatalog
            {
                Schema = baseResult.Catalog.Schema,
                SchemaVersion = baseResult.Catalog.SchemaVersion,
                CatalogVersion = baseResult.Catalog.CatalogVersion,
                UpdatedAt = baseResult.Catalog.UpdatedAt,
                MinAppVersion = baseResult.Catalog.MinAppVersion,
                Providers = new List<ProviderDescriptor>(baseResult.Catalog.Providers),
            };

            foreach (var localProvider in localCatalog.Providers)
            {
                if (ValidateProviderDescriptor(localProvider, out _))
                {
                    // Replace existing by ID or append new
                    var existingIdx = merged.Providers.FindIndex(p => string.Equals(p.Id, localProvider.Id, StringComparison.OrdinalIgnoreCase));
                    if (existingIdx >= 0)
                    {
                        merged.Providers[existingIdx] = localProvider;
                    }
                    else
                    {
                        merged.Providers.Add(localProvider);
                    }
                }
            }

            return new CatalogLoadResult
            {
                Catalog = merged,
                ActiveLayer = baseResult.ActiveLayer == CatalogSourceLayer.OfficialExternal
                    ? CatalogSourceLayer.LocalCustom
                    : baseResult.ActiveLayer,
                Status = CatalogLoadStatus.Supported,
                DiagnosticMessage = $"{baseResult.DiagnosticMessage} Overlaid local custom providers from {_localCatalogPath}.".Trim(),
            };
        }
        catch
        {
            return baseResult;
        }
    }

    private void TryUpdateLastKnownGood(byte[] catalogBytes)
    {
        try
        {
            _fs.WriteAllBytesAtomic(_catalogPreviousPath, catalogBytes);
        }
        catch
        {
            // Best effort
        }
    }

    public static bool ValidateCatalogSemantics(ProviderCatalog catalog, out string? error)
    {
        error = null;
        if (catalog.Providers == null || catalog.Providers.Count == 0)
        {
            error = "Catalog contains no providers.";
            return false;
        }

        foreach (var p in catalog.Providers)
        {
            if (!ValidateProviderDescriptor(p, out error))
                return false;
        }

        return true;
    }

    public static bool ValidateProviderDescriptor(ProviderDescriptor p, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(p.Id))
        {
            error = "Provider ID cannot be empty.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(p.DisplayName))
        {
            error = $"Provider '{p.Id}' has empty display name.";
            return false;
        }

        if (p.Match?.ExactHosts == null || p.Match.ExactHosts.Count == 0)
        {
            error = $"Provider '{p.Id}' must define at least one exactHost in match criteria.";
            return false;
        }

        if (p.TrustedHosts == null || p.TrustedHosts.Count == 0)
        {
            error = $"Provider '{p.Id}' must define at least one trustedHost.";
            return false;
        }

        if (p.Routes == null || p.Routes.Count == 0)
        {
            error = $"Provider '{p.Id}' must define at least one route.";
            return false;
        }

        foreach (var route in p.Routes)
        {
            if (string.IsNullOrWhiteSpace(route.Id) || string.IsNullOrWhiteSpace(route.BaseUrl))
            {
                error = $"Provider '{p.Id}' has an invalid route with missing ID or BaseUrl.";
                return false;
            }
        }

        return true;
    }

    public static bool IsAppVersionTooOld(string? requiredMinVersion, string currentAppVersion)
    {
        if (string.IsNullOrWhiteSpace(requiredMinVersion))
            return false;

        var cleanRequired = CleanVersion(requiredMinVersion);
        var cleanCurrent = CleanVersion(currentAppVersion);

        if (Version.TryParse(cleanRequired, out var reqVer) && Version.TryParse(cleanCurrent, out var curVer))
        {
            return curVer < reqVer;
        }

        return false;
    }

    private static string CleanVersion(string v)
    {
        var hyphen = v.IndexOf('-');
        if (hyphen >= 0)
            v = v[..hyphen];
        return v.Trim();
    }

    private static ProviderCatalog CreateMinimalFallbackCatalog()
    {
        return new ProviderCatalog
        {
            SchemaVersion = 1,
            CatalogVersion = 1,
            UpdatedAt = DateTimeOffset.UtcNow,
            MinAppVersion = "0.1.0",
            Providers = new List<ProviderDescriptor>
            {
                new()
                {
                    Id = "router-cheap",
                    DisplayName = "Router.Cheap",
                    Description = "High-performance OpenAI-compatible proxy with multiple global routes.",
                    Match = new ProviderMatchCriteria
                    {
                        ExactHosts = new List<string> { "router.cheap", "direct.router-cheap.com" }
                    },
                    Codex = new ProviderCodexConfig
                    {
                        WireApi = "responses",
                        AuthStrategy = "bearer",
                        DefaultModel = "gpt-5.6-sol"
                    },
                    Routes = new List<ProviderRoute>
                    {
                        new() { Id = "primary", DisplayName = "Primary", BaseUrl = "https://router.cheap/v1", IsDefault = true },
                        new() { Id = "reserve", DisplayName = "Direct Reserve", BaseUrl = "https://direct.router-cheap.com/v1", IsReserve = true }
                    },
                    TrustedHosts = new List<string> { "router.cheap", "direct.router-cheap.com" },
                    Capabilities = new ProviderCapabilities
                    {
                        Models = new ProviderCapabilityRecipe
                        {
                            Status = Models.CapabilityStatus.Supported,
                            Strategy = "openai-models",
                            Request = new RecipeRequest { Method = "GET", Path = "/models", Auth = "bearer" }
                        },
                        Balance = new ProviderCapabilityRecipe
                        {
                            Status = Models.CapabilityStatus.Unknown,
                            Strategy = "unknown"
                        },
                        Usage = new ProviderCapabilityRecipe
                        {
                            Status = Models.CapabilityStatus.Unknown,
                            Strategy = "unknown"
                        }
                    }
                }
            }
        };
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using CodexSwitcher.Core.Common.Storage;
using CodexSwitcher.Core.Providers.Contracts;
using CodexSwitcher.Core.Providers.Models;
using CodexSwitcher.Core.Providers.Services;
using CodexSwitcher.Core.Routing.Contracts;
using CodexSwitcher.Infra.Common.Paths;

namespace CodexSwitcher.Infra.Codex.Runtime;

/// <summary>
/// Generates version-compatible, runtime-adaptive Codex model catalogs for external models
/// requiring context windows larger than the 272k fallback limit (e.g. Grok 500k, 1M).
/// Emits a merged effective catalog (bundled runtime models + Switchboard custom models)
/// so unrelated runtime models are never hidden.
/// Keys catalog cache by runtime path + version + SHA256, invalidating upon Codex updates.
/// Validates generated catalog schema against the active production runtime before persistence.
/// </summary>
public sealed class CodexModelCatalogService : ICodexModelCatalogService
{
    public const long FallbackContextCeiling = 272_000;

    private readonly IFileSystem _fs;
    private readonly AppPaths _paths;
    private readonly ICodexRuntimeResolver? _runtimeResolver;
    private readonly ICodexModelMetadataResolver _metadataResolver;
    private readonly IEffectiveModelDescriptorResolver _descriptorResolver;
    private readonly Action<string, string>? _customValidator;
    private readonly object _sync = new();

    private string? _cachedRuntimeKey;
    private List<Dictionary<string, object?>>? _cachedBundledModels;
    private readonly Dictionary<string, (long Context, string Path, string ContentHash)> _catalogCache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public CodexModelCatalogService(IFileSystem fs, AppPaths paths)
        : this(fs, paths, null, null, null, null)
    {
    }

    public CodexModelCatalogService(
        IFileSystem fs,
        AppPaths paths,
        ICodexRuntimeResolver? runtimeResolver,
        ICodexModelMetadataResolver? metadataResolver,
        Action<string, string>? customValidator)
        : this(fs, paths, runtimeResolver, metadataResolver, customValidator, null)
    {
    }

    public CodexModelCatalogService(
        IFileSystem fs,
        AppPaths paths,
        ICodexRuntimeResolver? runtimeResolver = null,
        ICodexModelMetadataResolver? metadataResolver = null,
        Action<string, string>? customValidator = null,
        IEffectiveModelDescriptorResolver? descriptorResolver = null)
    {
        _fs = fs ?? throw new ArgumentNullException(nameof(fs));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _runtimeResolver = runtimeResolver;
        _metadataResolver = metadataResolver ?? new CodexModelMetadataResolver();
        _customValidator = customValidator;
        _descriptorResolver = descriptorResolver ?? new EffectiveModelDescriptorResolver(_metadataResolver);
    }

    public string? EnsureModelCatalog(string modelSlug, long? contextWindowTokens) =>
        EnsureModelCatalog(modelSlug, contextWindowTokens, null, null);

    public string? EnsureModelCatalog(string modelSlug, long? contextWindowTokens, CodexModelOverrides? modelOverrides) =>
        EnsureModelCatalog(modelSlug, contextWindowTokens, modelOverrides, null);

    public string? EnsureModelCatalog(
        string modelSlug,
        long? contextWindowTokens = null,
        CodexModelOverrides? modelOverrides = null,
        EffectiveToolPolicy? toolPolicy = null)
    {
        if (string.IsNullOrWhiteSpace(modelSlug))
        {
            return null;
        }

        var effectiveContext = modelOverrides?.ContextWindowTokens ?? contextWindowTokens;
        var needsCatalog = (effectiveContext.HasValue && effectiveContext.Value > FallbackContextCeiling) ||
                           (modelOverrides != null && (modelOverrides.ReasoningEffort != CodexReasoningEffort.Default || modelOverrides.Verbosity != CodexVerbosity.Default)) ||
                           (toolPolicy != null && (!toolPolicy.AllowCustomFreeformApplyPatch || !toolPolicy.AllowToolSearch));

        if (!needsCatalog)
        {
            return null;
        }

        lock (_sync)
        {
            var requestedContext = effectiveContext ?? FallbackContextCeiling;

            // Resolve model limit fact (Blocker B)
            var fact = _metadataResolver.ResolveLimitFact(modelSlug);
            var maxContext = fact.IsKnown && fact.DocumentedMaxContext.HasValue
                ? fact.DocumentedMaxContext.Value
                : Math.Max(requestedContext, FallbackContextCeiling);

            var targetContext = Math.Min(requestedContext, maxContext);

            // Detect runtime identity, version, and SHA256 (Blocker A / Invalidation Requirement 6)
            var runtimeInfo = _runtimeResolver?.ResolveCurrentRuntime();
            var runtimeSha256 = ComputeRuntimeSha256(runtimeInfo?.ExecutablePath);
            var runtimeKey = $"{runtimeInfo?.ExecutablePath}|{runtimeInfo?.Version}|{runtimeSha256}";

            if (!string.Equals(_cachedRuntimeKey, runtimeKey, StringComparison.OrdinalIgnoreCase))
            {
                _catalogCache.Clear();
                _cachedBundledModels = null;
                _cachedRuntimeKey = runtimeKey;
            }

            var cacheKey = $"{modelSlug}|{targetContext}|patch:{toolPolicy?.AllowCustomFreeformApplyPatch}|search:{toolPolicy?.AllowToolSearch}";

            if (_catalogCache.TryGetValue(cacheKey, out var cached) &&
                cached.Context == targetContext &&
                _fs.FileExists(cached.Path))
            {
                return cached.Path;
            }

            var dir = _paths.CatalogsDir;
            if (!_fs.DirectoryExists(dir))
            {
                _fs.CreateDirectory(dir);
            }

            var safeSlug = string.Join("_", modelSlug.Split(Path.GetInvalidFileNameChars()));
            var catalogPath = Path.Combine(dir, $"catalog-{safeSlug}.json");

            var isLegacy = IsLegacyRuntime(runtimeInfo?.Version);

            string catalogJson;
            if (isLegacy)
            {
                var legacyEntry = CreateLegacyModelEntry(modelSlug, modelSlug, targetContext, maxContext);
                catalogJson = JsonSerializer.Serialize(new[] { legacyEntry }, JsonOptions);
            }
            else
            {
                var modernEntry = CreateModernModelEntry(modelSlug, modelSlug, targetContext, maxContext, modelOverrides, toolPolicy);
                var payload = new Dictionary<string, object> { ["models"] = new[] { modernEntry } };
                catalogJson = JsonSerializer.Serialize(payload, JsonOptions);
            }

            // Write candidate catalog (atomic UTF-8 without BOM)
            _fs.WriteAllTextAtomic(catalogPath, catalogJson);

            // Fail-safe validation against production runtime BEFORE considering active
            try
            {
                ValidateCatalog(runtimeInfo?.ExecutablePath, catalogPath);
            }
            catch
            {
                try { _fs.Delete(catalogPath); } catch { }
                throw;
            }

            var contentHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(catalogJson)));
            _catalogCache[modelSlug] = (targetContext, catalogPath, contentHash);
            return catalogPath;
        }
    }

    public string? EnsureProfileModelCatalog(
        ApiProviderProfile profile,
        string? modelSlug = null,
        long? contextWindowTokens = null,
        CodexModelOverrides? modelOverrides = null,
        EffectiveToolPolicy? toolPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var slug = !string.IsNullOrWhiteSpace(modelSlug)
            ? modelSlug
            : (!string.IsNullOrWhiteSpace(profile.SelectedModel) ? profile.SelectedModel : "default-model");

        var effectiveContext = modelOverrides?.ContextWindowTokens ?? contextWindowTokens ?? profile.ModelOverrides?.ContextWindowTokens;
        var effectiveOverrides = modelOverrides ?? profile.ModelOverrides;

        var needsCatalog = (effectiveContext.HasValue && effectiveContext.Value > FallbackContextCeiling) ||
                           (effectiveOverrides != null && (effectiveOverrides.ReasoningEffort != CodexReasoningEffort.Default || effectiveOverrides.Verbosity != CodexVerbosity.Default)) ||
                           (toolPolicy != null && (!toolPolicy.AllowCustomFreeformApplyPatch || !toolPolicy.AllowToolSearch)) ||
                           (profile.ModelInventory != null && profile.ModelInventory.Models.Count > 0);

        if (!needsCatalog)
        {
            return null;
        }

        lock (_sync)
        {
            var runtimeInfo = _runtimeResolver?.ResolveCurrentRuntime();
            var runtimeSha256 = ComputeRuntimeSha256(runtimeInfo?.ExecutablePath);
            var runtimeFingerprint = !string.IsNullOrWhiteSpace(runtimeSha256)
                ? runtimeSha256[..Math.Min(12, runtimeSha256.Length)].ToLowerInvariant()
                : "default";
            var runtimeKey = $"{runtimeInfo?.ExecutablePath}|{runtimeInfo?.Version}|{runtimeSha256}";

            if (!string.Equals(_cachedRuntimeKey, runtimeKey, StringComparison.OrdinalIgnoreCase))
            {
                _catalogCache.Clear();
                _cachedBundledModels = null;
                _cachedRuntimeKey = runtimeKey;
            }

            profile.ModelInventory ??= new ApiProviderModelInventory();
            profile.ModelInventory.EnsureSelectedModelMigrated(slug, profile.Nickname, effectiveContext, effectiveOverrides);

            var isLegacy = IsLegacyRuntime(runtimeInfo?.Version);
            var enabledModels = new List<ApiProviderModelItem>(profile.ModelInventory.GetEnabledModels());
            if (enabledModels.Count == 0)
            {
                enabledModels.Add(new ApiProviderModelItem
                {
                    Slug = slug,
                    DisplayName = profile.Nickname ?? slug,
                    Enabled = true,
                    ContextWindow = effectiveContext,
                    UserOverrides = effectiveOverrides
                });
            }

            // Deterministic ordering by Slug Ordinal
            enabledModels.Sort((a, b) => string.Compare(a.Slug, b.Slug, StringComparison.Ordinal));

            var effectiveToolPolicy = toolPolicy ?? EffectiveToolPolicy.Resolve(profile);
            var entries = new List<Dictionary<string, object?>>();

            foreach (var m in enabledModels)
            {
                var isSelected = string.Equals(m.Slug, slug, StringComparison.OrdinalIgnoreCase);
                var descriptor = _descriptorResolver.ResolveDescriptor(
                    m,
                    profile,
                    catalogDescriptor: null,
                    toolPolicy: effectiveToolPolicy,
                    isLegacyRuntime: isLegacy,
                    isSelectedModel: isSelected);

                entries.Add(_descriptorResolver.BuildCatalogEntry(descriptor, isLegacyRuntime: isLegacy));
            }

            string catalogJson;
            if (isLegacy)
            {
                catalogJson = JsonSerializer.Serialize(entries, JsonOptions);
            }
            else
            {
                var payload = new Dictionary<string, object> { ["models"] = entries };
                catalogJson = JsonSerializer.Serialize(payload, JsonOptions);
            }

            var fullContentHash = ComputeCatalogFullHash(catalogJson);
            var contentFingerprint = fullContentHash[..16];
            var profileDir = Path.Combine(_paths.CatalogsDir, profile.Id.ToString("D"), runtimeFingerprint, contentFingerprint);
            var catalogPath = Path.Combine(profileDir, "models.json");
            var cacheKey = $"profile:{profile.Id}:{runtimeFingerprint}:{contentFingerprint}";

            if (_catalogCache.TryGetValue(cacheKey, out var cached) && _fs.FileExists(cached.Path))
            {
                return cached.Path;
            }

            if (_fs.FileExists(catalogPath))
            {
                var existingBytes = _fs.ReadAllBytes(catalogPath);
                var existingHash = Convert.ToHexString(SHA256.HashData(existingBytes)).ToLowerInvariant();
                if (!string.Equals(existingHash, fullContentHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Catalog content collision or corruption detected at '{catalogPath}'. Existing content hash does not match computed catalog hash.");
                }

                _catalogCache[cacheKey] = (effectiveContext ?? FallbackContextCeiling, catalogPath, contentFingerprint);
                return catalogPath;
            }

            if (!_fs.DirectoryExists(profileDir))
            {
                _fs.CreateDirectory(profileDir);
            }

            _fs.WriteAllTextAtomic(catalogPath, catalogJson);

            try
            {
                ValidateCatalog(runtimeInfo?.ExecutablePath, catalogPath);
            }
            catch
            {
                try { _fs.Delete(catalogPath); } catch { }
                throw;
            }

            _catalogCache[cacheKey] = (effectiveContext ?? FallbackContextCeiling, catalogPath, contentFingerprint);
            return catalogPath;
        }
    }

    /// <summary>
    /// Computes the 16-character lowercase hex SHA-256 fingerprint of the serialized catalog JSON.
    /// </summary>
    public static string ComputeCatalogContentHash(string catalogJson)
    {
        ArgumentNullException.ThrowIfNull(catalogJson);
        var bytes = System.Text.Encoding.UTF8.GetBytes(catalogJson);
        var full = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return full[..16];
    }

    /// <summary>
    /// Computes the full 64-character lowercase hex SHA-256 hash of the serialized catalog JSON.
    /// </summary>
    public static string ComputeCatalogFullHash(string catalogJson)
    {
        ArgumentNullException.ThrowIfNull(catalogJson);
        var bytes = System.Text.Encoding.UTF8.GetBytes(catalogJson);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private Dictionary<string, object?> CreateLegacyModelEntry(
        string slug,
        string? displayName,
        long targetContext,
        long maxContext)
    {
        var desc = new EffectiveModelDescriptor(
            Slug: slug,
            DisplayName: displayName ?? slug,
            ContextWindow: targetContext,
            MaxContextWindow: maxContext,
            ReasoningEffort: CodexReasoningEffort.Default,
            Verbosity: CodexVerbosity.Default,
            AllowFreeformApplyPatch: true,
            AllowToolSearch: true,
            AllowHostedWebSearch: true,
            SupportsImages: true,
            SupportsStreaming: true,
            Priority: 1);
        return _descriptorResolver.BuildCatalogEntry(desc, isLegacyRuntime: true);
    }

    private Dictionary<string, object?> CreateModernModelEntry(
        string slug,
        string? displayName,
        long targetContext,
        long maxContext,
        CodexModelOverrides? modelOverrides,
        EffectiveToolPolicy? toolPolicy)
    {
        var desc = new EffectiveModelDescriptor(
            Slug: slug,
            DisplayName: displayName ?? slug,
            ContextWindow: targetContext,
            MaxContextWindow: maxContext,
            ReasoningEffort: modelOverrides?.ReasoningEffort ?? CodexReasoningEffort.Default,
            Verbosity: modelOverrides?.Verbosity ?? CodexVerbosity.Default,
            AllowFreeformApplyPatch: toolPolicy == null || toolPolicy.AllowCustomFreeformApplyPatch,
            AllowToolSearch: toolPolicy == null || toolPolicy.AllowToolSearch,
            AllowHostedWebSearch: toolPolicy == null || toolPolicy.AllowHostedWebSearch,
            SupportsImages: true,
            SupportsStreaming: true,
            Priority: 1);
        return _descriptorResolver.BuildCatalogEntry(desc, isLegacyRuntime: false);
    }

    public sealed record ProcessRunResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut);

    public static ProcessRunResult RunProcessDeadlockSafe(
        string fileName,
        string arguments,
        int timeoutMs = 10000,
        IReadOnlyDictionary<string, string?>? environment = null,
        CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        if (environment != null)
        {
            foreach (var kvp in environment)
            {
                if (kvp.Value is null)
                {
                    psi.EnvironmentVariables.Remove(kvp.Key);
                }
                else
                {
                    psi.EnvironmentVariables[kvp.Key] = kvp.Value;
                }
            }
        }

        using var process = new Process { StartInfo = psi };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start process: '{fileName}' with arguments '{arguments}'.");
        }

        using var timeoutCts = new CancellationTokenSource(timeoutMs);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);

        // 1. Start stdout ReadToEndAsync and stderr ReadToEndAsync concurrently
        var stdoutTask = process.StandardOutput.ReadToEndAsync(linkedCts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(linkedCts.Token);

        try
        {
            // 2. Wait for process completion with the bounded timeout
            if (!process.WaitForExit(timeoutMs) || cancellationToken.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                try { process.WaitForExit(1000); } catch { }
                return new ProcessRunResult(-1, string.Empty, string.Empty, TimedOut: true);
            }

            // 3. Await both read tasks concurrently
            Task.WhenAll(stdoutTask, stderrTask).GetAwaiter().GetResult();

            return new ProcessRunResult(process.ExitCode, stdoutTask.Result, stderrTask.Result, TimedOut: false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            try { process.WaitForExit(1000); } catch { }
            return new ProcessRunResult(-1, string.Empty, string.Empty, TimedOut: true);
        }
        catch
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
    }

    internal List<Dictionary<string, object?>> GetBundledModels(string? runtimeExe, string? runtimeVersion = null)
    {
        if (_cachedBundledModels != null)
        {
            return _cachedBundledModels;
        }

        if (!string.IsNullOrWhiteSpace(runtimeExe) && File.Exists(runtimeExe))
        {
            // Primary authority: "debug models --bundled" from the production runtime
            var result = RunProcessDeadlockSafe(runtimeExe, "debug models --bundled");
            if (!result.TimedOut && result.ExitCode == 0 && TryParseModels(result.StandardOutput, out var bundledModels))
            {
                _cachedBundledModels = bundledModels;
                return bundledModels;
            }

            // Fallback for runtimes where --bundled is not recognized
            result = RunProcessDeadlockSafe(runtimeExe, "debug models");
            if (!result.TimedOut && result.ExitCode == 0 && TryParseModels(result.StandardOutput, out bundledModels))
            {
                _cachedBundledModels = bundledModels;
                return bundledModels;
            }
        }

        // Extraction was not possible (no executable or runtime execution failed).
        // Fallback catalog logic is strictly gated to qualified versions (up to 0.155.x / baseline).
        if (!IsQualifiedFallbackRuntime(runtimeVersion))
        {
            throw new InvalidOperationException(
                $"Unable to extract bundled model catalog from Codex runtime '{runtimeExe ?? "unknown"}' (version: '{runtimeVersion ?? "unknown"}'). " +
                "Bundled catalog extraction failed and this runtime version is not qualified for bundled catalog fallback. " +
                "Fallback is only qualified for runtime versions <= 0.155.x or baseline environments.");
        }

        _cachedBundledModels = GetDefaultBundledModels();
        return _cachedBundledModels;
    }

    private static bool TryParseModels(string rawOutput, out List<Dictionary<string, object?>> models)
    {
        models = new();
        if (string.IsNullOrWhiteSpace(rawOutput))
        {
            return false;
        }

        var json = rawOutput.Trim();
        if (!json.StartsWith('{'))
        {
            var start = json.IndexOf('{');
            var end = json.LastIndexOf('}');
            if (start >= 0 && end > start)
            {
                json = json.Substring(start, end - start + 1);
            }
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("models", out var modelsElem) && modelsElem.ValueKind == JsonValueKind.Array)
            {
                var list = new List<Dictionary<string, object?>>();
                foreach (var elem in modelsElem.EnumerateArray())
                {
                    var dict = JsonSerializer.Deserialize<Dictionary<string, object?>>(elem.GetRawText(), JsonOptions);
                    if (dict != null)
                    {
                        list.Add(dict);
                    }
                }

                if (list.Count > 0)
                {
                    models = list;
                    return true;
                }
            }
        }
        catch
        {
            // Parse failure
        }

        return false;
    }

    public static bool IsQualifiedFallbackRuntime(string? versionStr)
    {
        if (string.IsNullOrWhiteSpace(versionStr) ||
            versionStr.Equals("unknown", StringComparison.OrdinalIgnoreCase) ||
            versionStr.Equals("not_found", StringComparison.OrdinalIgnoreCase) ||
            versionStr.Equals("baseline", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var clean = versionStr.TrimStart('v');
        var dash = clean.IndexOf('-');
        if (dash > 0)
        {
            clean = clean[..dash];
        }

        if (Version.TryParse(clean, out var ver))
        {
            return ver < new Version(0, 156, 0);
        }

        return false;
    }

    private static List<Dictionary<string, object?>> GetDefaultBundledModels()
    {
        return new List<Dictionary<string, object?>>
        {
            CreateDefaultBundledModel("gpt-6-astra", "gpt-6-astra", 10),
            CreateDefaultBundledModel("gpt-5.6-sol", "gpt-5.6-sol", 9),
            CreateDefaultBundledModel("gpt-5.6-terra", "gpt-5.6-terra", 8),
            CreateDefaultBundledModel("gpt-5.6-luna", "gpt-5.6-luna", 7),
            CreateDefaultBundledModel("gpt-5.5", "gpt-5.5", 6),
            CreateDefaultBundledModel("gpt-5.4", "gpt-5.4", 5),
        };
    }

    private static Dictionary<string, object?> CreateDefaultBundledModel(string slug, string displayName, int priority)
    {
        return new Dictionary<string, object?>
        {
            ["slug"] = slug,
            ["display_name"] = displayName,
            ["description"] = "OpenAI Codex standard model",
            ["default_reasoning_level"] = "none",
            ["supported_reasoning_levels"] = new object[]
            {
                new Dictionary<string, string> { ["effort"] = "none", ["description"] = "No reasoning effort" },
                new Dictionary<string, string> { ["effort"] = "low", ["description"] = "Fast responses with lighter reasoning" },
                new Dictionary<string, string> { ["effort"] = "medium", ["description"] = "Balances speed and reasoning depth" },
                new Dictionary<string, string> { ["effort"] = "high", ["description"] = "Greater reasoning depth for complex problems" },
                new Dictionary<string, string> { ["effort"] = "xhigh", ["description"] = "Extra high reasoning depth" }
            },
            ["shell_type"] = "unified_exec",
            ["visibility"] = "list",
            ["supported_in_api"] = true,
            ["priority"] = priority,
            ["context_window"] = FallbackContextCeiling,
            ["max_context_window"] = FallbackContextCeiling,
            ["support_verbosity"] = false,
            ["default_verbosity"] = "low",
            ["supports_reasoning_summaries"] = true,
            ["default_reasoning_summary"] = "none",
            ["apply_patch_tool_type"] = "freeform",
            ["web_search_tool_type"] = "text_and_image",
            ["truncation_policy"] = new Dictionary<string, object> { ["mode"] = "tokens", ["limit"] = 10000 },
            ["supports_parallel_tool_calls"] = true,
            ["supports_image_detail_original"] = true,
            ["supports_search_tool"] = true,
            ["input_modalities"] = new[] { "text", "image" },
            ["effective_context_window_percent"] = 95,
            ["experimental_supported_tools"] = Array.Empty<string>(),
            ["base_instructions"] = "You are a helpful AI assistant."
        };
    }

    private void ValidateCatalog(string? runtimeExe, string catalogPath)
    {
        if (_customValidator != null)
        {
            _customValidator(runtimeExe ?? string.Empty, catalogPath);
            return;
        }

        if (string.IsNullOrWhiteSpace(runtimeExe) || !File.Exists(runtimeExe))
        {
            return;
        }

        var tempValidationHome = Path.Combine(Path.GetTempPath(), "codex-catalog-val-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempValidationHome);
            var env = new Dictionary<string, string?> { ["CODEX_HOME"] = tempValidationHome };
            var arguments = $"-c model_catalog_json=\"{catalogPath.Replace('\\', '/')}\" debug models";
            var result = RunProcessDeadlockSafe(runtimeExe, arguments, timeoutMs: 10000, environment: env);

            if (result.TimedOut)
            {
                throw new InvalidOperationException($"Codex runtime validation timed out for catalog: {catalogPath}");
            }

            if (result.ExitCode != 0)
            {
                var err = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
                throw new InvalidOperationException($"Codex runtime rejected model catalog '{catalogPath}': {err.Trim()}");
            }
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException($"Failed to execute Codex runtime validation: {ex.Message}", ex);
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempValidationHome))
                {
                    Directory.Delete(tempValidationHome, true);
                }
            }
            catch { }
        }
    }

    private static string ComputeRuntimeSha256(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return "none";

        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch
        {
            return "error";
        }
    }

    private static bool IsLegacyRuntime(string? versionStr)
    {
        if (string.IsNullOrWhiteSpace(versionStr) || versionStr == "unknown" || versionStr == "not_found")
            return false;

        var clean = versionStr.TrimStart('v');
        var dash = clean.IndexOf('-');
        if (dash > 0)
            clean = clean[..dash];

        if (Version.TryParse(clean, out var ver))
        {
            return ver < new Version(0, 150, 0);
        }

        return false;
    }
}

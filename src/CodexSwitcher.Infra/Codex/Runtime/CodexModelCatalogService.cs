using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
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
        : this(fs, paths, null, null, null)
    {
    }

    public CodexModelCatalogService(
        IFileSystem fs,
        AppPaths paths,
        ICodexRuntimeResolver? runtimeResolver = null,
        ICodexModelMetadataResolver? metadataResolver = null,
        Action<string, string>? customValidator = null)
    {
        _fs = fs ?? throw new ArgumentNullException(nameof(fs));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _runtimeResolver = runtimeResolver;
        _metadataResolver = metadataResolver ?? new CodexModelMetadataResolver();
        _customValidator = customValidator;
    }

    public string? EnsureModelCatalog(string modelSlug, long? contextWindowTokens)
    {
        if (string.IsNullOrWhiteSpace(modelSlug) || !contextWindowTokens.HasValue || contextWindowTokens.Value <= FallbackContextCeiling)
        {
            return null;
        }

        lock (_sync)
        {
            var requestedContext = contextWindowTokens.Value;

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

            if (_catalogCache.TryGetValue(modelSlug, out var cached) &&
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
                // Legacy schema (< 0.150)
                var legacyEntry = new Dictionary<string, object?>
                {
                    ["slug"] = modelSlug,
                    ["display_name"] = modelSlug,
                    ["description"] = "Switchboard-managed model configuration",
                    ["context_window"] = targetContext,
                    ["max_context_window"] = maxContext,
                    ["supported_reasoning_levels"] = new[] { "none", "low", "medium", "high", "xhigh" },
                    ["default_reasoning_level"] = "none",
                    ["shell_type"] = "generic",
                    ["visibility"] = "visible",
                    ["supported_in_api"] = true,
                    ["priority"] = 1,
                    ["supports_streaming"] = true,
                    ["supports_tools"] = true,
                    ["supports_images"] = true
                };
                catalogJson = JsonSerializer.Serialize(new[] { legacyEntry }, JsonOptions);
            }
            else
            {
                // Modern schema (>= 0.150 / active production runtime 0.155)
                var modernEntry = new Dictionary<string, object?>
                {
                    ["slug"] = modelSlug,
                    ["display_name"] = modelSlug,
                    ["description"] = "Switchboard-managed model configuration",
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
                    ["priority"] = 1,
                    ["context_window"] = targetContext,
                    ["max_context_window"] = maxContext,
                    ["support_verbosity"] = false,
                    ["truncation_policy"] = new Dictionary<string, object> { ["mode"] = "tokens", ["limit"] = 10000 },
                    ["experimental_supported_tools"] = Array.Empty<string>(),
                    ["base_instructions"] = "You are a helpful AI assistant."
                };

                // Requirement 5: Merged effective catalog (bundled runtime models + custom model)
                var bundled = GetBundledModels(runtimeInfo?.ExecutablePath);
                var merged = new List<Dictionary<string, object?>> { modernEntry };
                foreach (var b in bundled)
                {
                    if (b.TryGetValue("slug", out var existingSlug) &&
                        string.Equals(existingSlug?.ToString(), modelSlug, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    merged.Add(b);
                }

                var payload = new Dictionary<string, object> { ["models"] = merged };
                catalogJson = JsonSerializer.Serialize(payload, JsonOptions);
            }

            // Write candidate catalog (atomic UTF-8 without BOM)
            _fs.WriteAllTextAtomic(catalogPath, catalogJson);

            // Fail-safe validation against production runtime BEFORE considering active
            ValidateCatalog(runtimeInfo?.ExecutablePath, catalogPath);

            var contentHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(catalogJson)));
            _catalogCache[modelSlug] = (targetContext, catalogPath, contentHash);
            return catalogPath;
        }
    }

    private List<Dictionary<string, object?>> GetBundledModels(string? runtimeExe)
    {
        if (_cachedBundledModels != null)
        {
            return _cachedBundledModels;
        }

        if (!string.IsNullOrWhiteSpace(runtimeExe) && File.Exists(runtimeExe))
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = runtimeExe,
                    Arguments = "debug models",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process != null && process.WaitForExit(5000) && process.ExitCode == 0)
                {
                    var stdout = process.StandardOutput.ReadToEnd();
                    using var doc = JsonDocument.Parse(stdout);
                    if (doc.RootElement.TryGetProperty("models", out var modelsElem) && modelsElem.ValueKind == JsonValueKind.Array)
                    {
                        var list = new List<Dictionary<string, object?>>();
                        foreach (var elem in modelsElem.EnumerateArray())
                        {
                            var dict = JsonSerializer.Deserialize<Dictionary<string, object?>>(elem.GetRawText(), JsonOptions);
                            if (dict != null) list.Add(dict);
                        }
                        if (list.Count > 0)
                        {
                            _cachedBundledModels = list;
                            return list;
                        }
                    }
                }
            }
            catch
            {
                // Fall back to built-in defaults if execution fails
            }
        }

        _cachedBundledModels = GetDefaultBundledModels();
        return _cachedBundledModels;
    }

    private static List<Dictionary<string, object?>> GetDefaultBundledModels()
    {
        return new List<Dictionary<string, object?>>
        {
            new() { ["slug"] = "gpt-6-astra", ["display_name"] = "gpt-6-astra", ["priority"] = 10 },
            new() { ["slug"] = "gpt-5.6-sol", ["display_name"] = "gpt-5.6-sol", ["priority"] = 9 },
            new() { ["slug"] = "gpt-5.6-terra", ["display_name"] = "gpt-5.6-terra", ["priority"] = 8 },
            new() { ["slug"] = "gpt-5.6-luna", ["display_name"] = "gpt-5.6-luna", ["priority"] = 7 },
            new() { ["slug"] = "gpt-5.5", ["display_name"] = "gpt-5.5", ["priority"] = 6 },
            new() { ["slug"] = "gpt-5.4", ["display_name"] = "gpt-5.4", ["priority"] = 5 },
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

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = runtimeExe,
                Arguments = $"-c model_catalog_json=\"{catalogPath.Replace('\\', '/')}\" debug models",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null) return;

            if (!process.WaitForExit(5000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                throw new InvalidOperationException($"Codex runtime validation timed out for catalog: {catalogPath}");
            }

            if (process.ExitCode != 0)
            {
                var stderr = process.StandardError.ReadToEnd();
                throw new InvalidOperationException($"Codex runtime rejected model catalog '{catalogPath}': {stderr.Trim()}");
            }
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException($"Failed to execute Codex runtime validation: {ex.Message}", ex);
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

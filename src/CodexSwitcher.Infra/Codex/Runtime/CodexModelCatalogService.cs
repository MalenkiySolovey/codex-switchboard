using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodexSwitcher.Core.Common.Storage;
using CodexSwitcher.Core.Routing.Contracts;
using CodexSwitcher.Infra.Common.Paths;

namespace CodexSwitcher.Infra.Codex.Runtime;

/// <summary>
/// Generates version-compatible minimal Codex model catalogs for external models
/// requiring context windows larger than the 272k fallback limit (e.g. Grok 500k, 1M).
/// Matches the schema validated by installed codex debug models (0.130.0-alpha.5).
/// </summary>
public sealed class CodexModelCatalogService : ICodexModelCatalogService
{
    public const long FallbackContextCeiling = 272_000;

    private readonly IFileSystem _fs;
    private readonly AppPaths _paths;
    private readonly object _sync = new();
    private readonly Dictionary<string, (long Context, string Path)> _catalogCache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public CodexModelCatalogService(IFileSystem fs, AppPaths paths)
    {
        _fs = fs ?? throw new ArgumentNullException(nameof(fs));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    public string? EnsureModelCatalog(string modelSlug, long? contextWindowTokens)
    {
        if (string.IsNullOrWhiteSpace(modelSlug) || !contextWindowTokens.HasValue || contextWindowTokens.Value <= FallbackContextCeiling)
        {
            return null;
        }

        lock (_sync)
        {
            var targetContext = contextWindowTokens.Value;
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

            var maxContext = Math.Max(targetContext, 1_000_000);

            var modelEntry = new Dictionary<string, object>
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

            var catalogJson = JsonSerializer.Serialize(new[] { modelEntry }, JsonOptions);
            _fs.WriteAllTextAtomic(catalogPath, catalogJson);

            _catalogCache[modelSlug] = (targetContext, catalogPath);
            return catalogPath;
        }
    }
}

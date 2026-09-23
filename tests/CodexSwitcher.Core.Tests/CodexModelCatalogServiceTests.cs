using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using CodexSwitcher.Core.Providers.Services;
using CodexSwitcher.Core.Routing.Contracts;
using CodexSwitcher.Core.Routing.Models;
using CodexSwitcher.Core.Tests.TestSupport;
using CodexSwitcher.Infra.Codex.Runtime;
using CodexSwitcher.Infra.Common.Paths;
using CodexSwitcher.Infra.Common.Storage;
using Xunit;

namespace CodexSwitcher.Core.Tests;

public sealed class CodexModelCatalogServiceTests
{
    private readonly PhysicalFileSystem _fs = new();

    private sealed class FakeRuntimeResolver : ICodexRuntimeResolver
    {
        public string? Version { get; set; }
        public string ExecutablePath { get; set; }

        public FakeRuntimeResolver(string? version, string executablePath = "C:\\fake\\codex.exe")
        {
            Version = version;
            ExecutablePath = executablePath;
        }

        public CodexRuntimeInfo ResolveCurrentRuntime(string? overridePath = null)
        {
            return new CodexRuntimeInfo(
                ExecutablePath,
                Version,
                "fake-id",
                CodexRuntimeCapabilities.Default);
        }

        public IReadOnlyList<CodexRuntimeCandidate> EnumerateCandidates() => [];

        public bool ValidateExecutable(string path, out string? version, out string? errorMessage)
        {
            version = Version;
            errorMessage = null;
            return true;
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData(128000L)]
    [InlineData(256000L)]
    [InlineData(272000L)]
    public void EnsureModelCatalog_WhenContextUnderCeiling_ReturnsNull(long? tokens)
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Root);
        var service = new CodexModelCatalogService(_fs, paths);

        var result = service.EnsureModelCatalog("grok-4.6", tokens);
        Assert.Null(result);
    }

    [Fact]
    public void EnsureModelCatalog_WhenContextExceedsCeiling_GeneratesValidModernJsonCatalog()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Root);
        var service = new CodexModelCatalogService(_fs, paths);

        var result = service.EnsureModelCatalog("grok-4.6", 500000);
        Assert.NotNull(result);
        Assert.True(File.Exists(result));

        var json = File.ReadAllText(result);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        Assert.True(doc.RootElement.TryGetProperty("models", out var modelsProp));
        Assert.Equal(JsonValueKind.Array, modelsProp.ValueKind);
        Assert.True(modelsProp.GetArrayLength() >= 1);

        var bytes = File.ReadAllBytes(result);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        Assert.DoesNotContain("model_supports_reasoning_summaries", json);

        var model = modelsProp[0];
        Assert.Equal("grok-4.6", model.GetProperty("slug").GetString());
        Assert.Equal(500000L, model.GetProperty("context_window").GetInt64());
        Assert.Equal(500000L, model.GetProperty("max_context_window").GetInt64());
        Assert.Equal("unified_exec", model.GetProperty("shell_type").GetString());
        Assert.True(model.GetProperty("supported_in_api").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(model.GetProperty("base_instructions").GetString()));

        var reasoningLevels = model.GetProperty("supported_reasoning_levels");
        Assert.Equal(JsonValueKind.Array, reasoningLevels.ValueKind);
        Assert.True(reasoningLevels.GetArrayLength() > 0);
        Assert.Equal("none", reasoningLevels[0].GetProperty("effort").GetString());
        Assert.False(string.IsNullOrWhiteSpace(reasoningLevels[0].GetProperty("description").GetString()));
    }

    [Fact]
    public void EnsureModelCatalog_WithMillionTokens_Grok420_SetsMaxContextWindowToOneMillion()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Root);
        var service = new CodexModelCatalogService(_fs, paths);

        var result = service.EnsureModelCatalog("grok-4.20", 1000000);
        Assert.NotNull(result);

        var json = File.ReadAllText(result);
        using var doc = JsonDocument.Parse(json);
        var model = doc.RootElement.GetProperty("models")[0];
        Assert.Equal(1000000L, model.GetProperty("context_window").GetInt64());
        Assert.Equal(1000000L, model.GetProperty("max_context_window").GetInt64());
    }

    [Fact]
    public void EnsureModelCatalog_WhenRequestedExceedsDocumentedMax_ClampsToDocumentedLimit()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Root);
        var service = new CodexModelCatalogService(_fs, paths);

        // grok-4.6 has documented limit of 500k; requesting 1M must be clamped
        var result = service.EnsureModelCatalog("grok-4.6", 1000000);
        Assert.NotNull(result);

        var json = File.ReadAllText(result);
        using var doc = JsonDocument.Parse(json);
        var model = doc.RootElement.GetProperty("models")[0];
        Assert.Equal(500000L, model.GetProperty("context_window").GetInt64());
        Assert.Equal(500000L, model.GetProperty("max_context_window").GetInt64());
    }

    [Fact]
    public void EnsureModelCatalog_WithUnknownModel_AllowsCustomContextAboveCeiling()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Root);
        var service = new CodexModelCatalogService(_fs, paths);

        var result = service.EnsureModelCatalog("custom-unknown-model", 2000000);
        Assert.NotNull(result);

        var json = File.ReadAllText(result);
        using var doc = JsonDocument.Parse(json);
        var model = doc.RootElement.GetProperty("models")[0];
        Assert.Equal(2000000L, model.GetProperty("context_window").GetInt64());
        Assert.Equal(2000000L, model.GetProperty("max_context_window").GetInt64());
    }

    [Fact]
    public void EnsureModelCatalog_WithLegacyRuntime_EmitsLegacyArraySchema()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Root);
        var legacyResolver = new FakeRuntimeResolver("0.130.0-alpha.5");
        var service = new CodexModelCatalogService(_fs, paths, runtimeResolver: legacyResolver);

        var result = service.EnsureModelCatalog("grok-4.6", 500000);
        Assert.NotNull(result);

        var json = File.ReadAllText(result);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal(1, doc.RootElement.GetArrayLength());

        var model = doc.RootElement[0];
        Assert.Equal("grok-4.6", model.GetProperty("slug").GetString());
        Assert.Equal(500000L, model.GetProperty("context_window").GetInt64());
        Assert.Equal("generic", model.GetProperty("shell_type").GetString());

        var reasoningLevels = model.GetProperty("supported_reasoning_levels");
        Assert.Equal(JsonValueKind.Array, reasoningLevels.ValueKind);
        Assert.Equal("none", reasoningLevels[0].GetString());
    }

    [Fact]
    public void EnsureModelCatalog_WhenRuntimeValidationFails_ThrowsInvalidOperationException()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Root);
        var service = new CodexModelCatalogService(
            _fs,
            paths,
            customValidator: (exe, path) => throw new InvalidOperationException("Runtime rejected model_catalog_json"));

        var ex = Assert.Throws<InvalidOperationException>(() => service.EnsureModelCatalog("grok-4.6", 500000));
        Assert.Contains("Runtime rejected", ex.Message);
    }

    [Fact]
    public void EnsureModelCatalog_SubsequentCalls_UsesCacheAndReturnsSamePath()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Root);
        var service = new CodexModelCatalogService(_fs, paths);

        var first = service.EnsureModelCatalog("grok-4.6", 500000);
        var second = service.EnsureModelCatalog("grok-4.6", 500000);

        Assert.NotNull(first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void EnsureModelCatalog_WhenRuntimeIdentityChanges_InvalidatesCache()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Root);
        var resolver = new FakeRuntimeResolver("0.155.0-alpha.9.2", "C:\\fake\\codex1.exe");
        var service = new CodexModelCatalogService(_fs, paths, runtimeResolver: resolver);

        var first = service.EnsureModelCatalog("grok-4.6", 500000);
        Assert.NotNull(first);

        // Mutate runtime resolver identity
        resolver.Version = "0.155.1";
        resolver.ExecutablePath = "C:\\fake\\codex2.exe";

        var second = service.EnsureModelCatalog("grok-4.6", 500000);
        Assert.NotNull(second);
    }

    [Fact]
    public void RunProcessDeadlockSafe_WithLargeOutputOnBothStreams_DoesNotDeadlock()
    {
        // Emit 250 KB on stdout and 250 KB on stderr concurrently.
        // This substantially exceeds typical OS pipe buffer capacities (e.g. 4-64 KB),
        // guaranteeing that sequential drain would deadlock.
        const int charCount = 250000;
        var script = $"$s = New-Object string ('O', {charCount}); $e = New-Object string ('E', {charCount}); [Console]::Out.Write($s); [Console]::Error.Write($e)";
        var arguments = $"-NoProfile -NonInteractive -Command \"{script}\"";

        var result = CodexModelCatalogService.RunProcessDeadlockSafe("powershell.exe", arguments, timeoutMs: 15000);

        Assert.False(result.TimedOut);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(charCount, result.StandardOutput.Length);
        Assert.Equal(charCount, result.StandardError.Length);
        Assert.All(result.StandardOutput, c => Assert.Equal('O', c));
        Assert.All(result.StandardError, c => Assert.Equal('E', c));
    }

    [Fact]
    public void RunProcessDeadlockSafe_WhenProcessHangs_KillsProcessAndTimesOut()
    {
        var arguments = "-NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 10\"";
        var result = CodexModelCatalogService.RunProcessDeadlockSafe("powershell.exe", arguments, timeoutMs: 500);

        Assert.True(result.TimedOut);
        Assert.Equal(-1, result.ExitCode);
    }

    [Fact]
    public void IsQualifiedFallbackRuntime_CorrectlyIdentifiesSupportedVersions()
    {
        Assert.True(CodexModelCatalogService.IsQualifiedFallbackRuntime(null));
        Assert.True(CodexModelCatalogService.IsQualifiedFallbackRuntime(string.Empty));
        Assert.True(CodexModelCatalogService.IsQualifiedFallbackRuntime("unknown"));
        Assert.True(CodexModelCatalogService.IsQualifiedFallbackRuntime("baseline"));
        Assert.True(CodexModelCatalogService.IsQualifiedFallbackRuntime("0.150.0"));
        Assert.True(CodexModelCatalogService.IsQualifiedFallbackRuntime("0.155.0"));
        Assert.True(CodexModelCatalogService.IsQualifiedFallbackRuntime("0.155.99"));
        Assert.True(CodexModelCatalogService.IsQualifiedFallbackRuntime("v0.155.0-alpha.1"));

        Assert.False(CodexModelCatalogService.IsQualifiedFallbackRuntime("0.156.0"));
        Assert.False(CodexModelCatalogService.IsQualifiedFallbackRuntime("0.199.0"));
        Assert.False(CodexModelCatalogService.IsQualifiedFallbackRuntime("1.0.0"));
        Assert.False(CodexModelCatalogService.IsQualifiedFallbackRuntime("invalid.version.string"));
    }

    [Fact]
    public void GetBundledModels_UnknownFutureRuntime_ThrowsActionableException()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Root);
        var service = new CodexModelCatalogService(_fs, paths);

        // Unknown future runtime where extraction cannot succeed
        var ex = Assert.Throws<InvalidOperationException>(() =>
            service.GetBundledModels("C:\\nonexistent\\codex.exe", "0.199.0"));

        Assert.Contains("0.199.0", ex.Message);
        Assert.Contains("not qualified for bundled catalog fallback", ex.Message);
    }

    [Fact]
    public void EnsureModelCatalog_WhenValidationFails_DeletesCandidateCatalogAndNeverActivates()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Root);
        var service = new CodexModelCatalogService(
            _fs,
            paths,
            customValidator: (exe, path) => throw new InvalidOperationException("Runtime schema validation rejected catalog."));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            service.EnsureModelCatalog("test-model", 500000));
        Assert.Contains("Runtime schema validation rejected catalog", ex.Message);

        var candidatePath = Path.Combine(paths.CatalogsDir, "catalog-test-model.json");
        Assert.False(File.Exists(candidatePath), "Failed candidate catalog must be deleted from disk and never activated.");
    }

    [Fact]
    public void GetBundledModels_ProductionBundledExtraction_SucceedsIfProductionCodexInstalled()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var codexExe = Path.Combine(localAppData, "OpenAI", "Codex", "bin", "codex.exe");
        if (!File.Exists(codexExe))
        {
            return; // Skip if codex is not installed in local environment
        }

        using var temp = new TempDir();
        var paths = new AppPaths(temp.Root);
        var service = new CodexModelCatalogService(_fs, paths);

        var models = service.GetBundledModels(codexExe, "0.155.0");
        Assert.NotNull(models);
        Assert.NotEmpty(models);
        Assert.Contains(models, m => m.ContainsKey("slug") && !string.IsNullOrWhiteSpace(m["slug"]?.ToString()));
    }

    [Fact]
    public void EnsureProfileModelCatalog_CreatesIsolatedProfileDirectory_AndNeverIncludesOpenAiModels()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Root);
        var service = new CodexModelCatalogService(_fs, paths);

        var profile = new ApiProviderProfile
        {
            Id = Guid.NewGuid(),
            Nickname = "Test Provider",
            CatalogProviderId = "openrouter",
            SelectedModel = "deepseek-v4.1-flash",
            ModelInventory = new ApiProviderModelInventory
            {
                Models = [
                    new ApiProviderModelItem { Slug = "deepseek-v4.1-flash", DisplayName = "DeepSeek Flash", Enabled = true },
                    new ApiProviderModelItem { Slug = "anthropic/claude-3-opus", DisplayName = "Claude Opus", Enabled = true }
                ]
            }
        };

        var catalogPath = service.EnsureProfileModelCatalog(profile, "deepseek-v4.1-flash", 500000);
        Assert.NotNull(catalogPath);
        Assert.True(File.Exists(catalogPath));

        // Invariant: Path is scoped by profile GUID
        Assert.Contains(profile.Id.ToString("D"), catalogPath);

        var json = File.ReadAllText(catalogPath);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("models", out var modelsProp));
        Assert.Equal(JsonValueKind.Array, modelsProp.ValueKind);

        // Invariant: Contains only models from profile
        var slugs = new List<string>();
        foreach (var m in modelsProp.EnumerateArray())
        {
            slugs.Add(m.GetProperty("slug").GetString()!);
        }

        Assert.Contains("deepseek-v4.1-flash", slugs);
        Assert.Contains("anthropic/claude-3-opus", slugs);

        // Invariant: Bundled OpenAI models must NEVER be present!
        Assert.DoesNotContain("gpt-4", slugs);
        Assert.DoesNotContain("gpt-4o", slugs);
        Assert.DoesNotContain("o1", slugs);
        Assert.DoesNotContain("o3-mini", slugs);
    }
}

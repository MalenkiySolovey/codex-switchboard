using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using CodexSwitcher.Core.Providers.Models;
using CodexSwitcher.Core.Routing.Models;
using CodexSwitcher.Core.Tests.TestSupport;
using CodexSwitcher.Core.Threads.Contracts;
using CodexSwitcher.Core.Threads.Models;
using CodexSwitcher.Infra.Codex.Runtime;
using CodexSwitcher.Infra.Codex.Threads;
using CodexSwitcher.Infra.Common.Paths;
using CodexSwitcher.Infra.Common.Storage;
using Xunit;

namespace CodexSwitcher.Core.Tests;

public sealed class ProductionRuntimePrecedenceRegressionTests
{
    private sealed class MockAppServerClient : ICodexAppServerClient
    {
        public bool IsRunning { get; set; } = true;
        public Task<JsonElement> RequestAsync(string method, object? parameters = null, TimeSpan? timeout = null, System.Threading.CancellationToken cancellationToken = default)
        {
            if (method == "thread/fork")
            {
                return Task.FromResult(JsonDocument.Parse(@"{
                    ""modelProvider"": ""prov_b"",
                    ""model"": ""model-b"",
                    ""thread"": { ""id"": ""forked-id"", ""ephemeral"": false }
                }").RootElement);
            }
            if (method == "thread/read")
            {
                return Task.FromResult(JsonDocument.Parse(@"{
                    ""thread"": { ""id"": ""forked-id"" }
                }").RootElement);
            }
            return Task.FromResult(JsonDocument.Parse("{}").RootElement);
        }
        public Task NotifyAsync(string method, object? parameters = null, System.Threading.CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StartAsync(System.Threading.CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public void Resolver_WhenUnversionedAndVersionedExist_SelectsProductionVersionedBinary()
    {
        using var temp = new TempDir();
        var binDir = Path.Combine(temp.Root, "bin");
        var hashDir = Path.Combine(binDir, "80f78947ad880e6e");
        Directory.CreateDirectory(hashDir);

        var unversionedExe = Path.Combine(binDir, "codex.exe");
        var versionedExe = Path.Combine(hashDir, "codex.exe");
        File.WriteAllText(unversionedExe, "unversioned dummy");
        File.WriteAllText(versionedExe, "versioned dummy");

        var resolver = new CodexRuntimeResolver(
            baseBinDirectory: binDir,
            probeInspector: path =>
            {
                if (path.Equals(versionedExe, StringComparison.OrdinalIgnoreCase))
                {
                    return ("0.155.0-alpha.16.3", true);
                }
                if (path.Equals(unversionedExe, StringComparison.OrdinalIgnoreCase))
                {
                    return ("0.130.0-alpha.5", false);
                }
                return ("unknown", false);
            });

        var runtime = resolver.ResolveCurrentRuntime();

        Assert.Equal(versionedExe, runtime.ExecutablePath);
        Assert.Equal("0.155.0-alpha.16.3", runtime.Version);
        Assert.NotEqual(unversionedExe, runtime.ExecutablePath);
    }

    [Fact]
    public void ModelCatalogService_SelectsProductionVersionedBinaryForValidation()
    {
        using var temp = new TempDir();
        var binDir = Path.Combine(temp.Root, "bin");
        var hashDir = Path.Combine(binDir, "80f78947ad880e6e");
        Directory.CreateDirectory(hashDir);

        var unversionedExe = Path.Combine(binDir, "codex.exe");
        var versionedExe = Path.Combine(hashDir, "codex.exe");
        File.WriteAllText(unversionedExe, "unversioned dummy");
        File.WriteAllText(versionedExe, "versioned dummy");

        var resolver = new CodexRuntimeResolver(
            baseBinDirectory: binDir,
            probeInspector: path =>
            {
                if (path.Equals(versionedExe, StringComparison.OrdinalIgnoreCase))
                {
                    return ("0.155.0-alpha.16.3", true);
                }
                return ("0.130.0-alpha.5", false);
            });

        string? validatedExe = null;
        var fs = new PhysicalFileSystem();
        var paths = new AppPaths(temp.Root);
        var catalogService = new CodexModelCatalogService(
            fs,
            paths,
            runtimeResolver: resolver,
            customValidator: (exe, path) => validatedExe = exe);

        var profile = new ApiProviderProfile
        {
            Id = Guid.NewGuid(),
            Nickname = "Test Profile",
            SelectedModel = "test-model",
            ModelInventory = new ApiProviderModelInventory
            {
                Models = new List<ApiProviderModelItem>
                {
                    new() { Slug = "test-model", Enabled = true, ContextWindow = 200000 }
                }
            }
        };

        catalogService.EnsureProfileModelCatalog(profile);

        Assert.NotNull(validatedExe);
        Assert.Equal(versionedExe, validatedExe);
        Assert.NotEqual(unversionedExe, validatedExe);
    }

    [Fact]
    public async Task ThreadHandoffService_SelectsProductionVersionedBinaryForAppServer()
    {
        using var temp = new TempDir();
        var binDir = Path.Combine(temp.Root, "bin");
        var hashDir = Path.Combine(binDir, "80f78947ad880e6e");
        Directory.CreateDirectory(hashDir);

        var unversionedExe = Path.Combine(binDir, "codex.exe");
        var versionedExe = Path.Combine(hashDir, "codex.exe");
        File.WriteAllText(unversionedExe, "unversioned dummy");
        File.WriteAllText(versionedExe, "versioned dummy");

        var resolver = new CodexRuntimeResolver(
            baseBinDirectory: binDir,
            probeInspector: path =>
            {
                if (path.Equals(versionedExe, StringComparison.OrdinalIgnoreCase))
                {
                    return ("0.155.0-alpha.16.3", true);
                }
                return ("0.130.0-alpha.5", false);
            });

        string? appServerExe = null;
        var handoffService = new CodexThreadHandoffService(async () =>
        {
            var runtime = resolver.ResolveCurrentRuntime();
            appServerExe = runtime.ExecutablePath;
            await Task.Yield();
            return new MockAppServerClient();
        });

        var result = await handoffService.ForkThreadAsync("src-id", "prov_b", "model-b");

        Assert.NotNull(result);
        Assert.Equal("forked-id", result.ForkedThreadId);
        Assert.NotNull(appServerExe);
        Assert.Equal(versionedExe, appServerExe);
        Assert.NotEqual(unversionedExe, appServerExe);
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using CodexSwitcher.Core.Routing.Models;
using CodexSwitcher.Core.Providers.Models;
using CodexSwitcher.Core.Settings.Models;
using CodexSwitcher.Infra.Codex.Runtime;
using CodexSwitcher.Infra.Codex.Threads;
using CodexSwitcher.Infra.Common.Paths;
using CodexSwitcher.Core.Tests.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace CodexSwitcher.Core.Tests;

public sealed class LiveProductionRuntimeQualificationTests
{
    private readonly ITestOutputHelper _output;

    public LiveProductionRuntimeQualificationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task QualifyProfileCatalogThroughCurrentProductionRuntimeModelList()
    {
        var resolver = new CodexRuntimeResolver();
        var runtime = resolver.ResolveCurrentRuntime();
        _output.WriteLine($"Resolved Runtime Path: {runtime.ExecutablePath}");
        _output.WriteLine($"Resolved Runtime Version: {runtime.Version}");

        if (string.IsNullOrWhiteSpace(runtime.ExecutablePath) || !File.Exists(runtime.ExecutablePath))
        {
            _output.WriteLine("Production runtime executable not found on host. Skipping live qualification test.");
            return;
        }

        using var temp = new TempDir();
        var codexHome = Path.Combine(temp.Root, ".codex");
        var paths = new AppPaths(Path.Combine(temp.Root, "switchboard"), codexHome);
        paths.EnsureDirectories();
        Directory.CreateDirectory(codexHome);

        var profileId = Guid.NewGuid();
        var profile = new ApiProviderProfile
        {
            Id = profileId,
            StableCodexProviderId = ApiProviderProfile.GenerateStableCodexProviderId(profileId),
            Nickname = "Runtime catalog qualification",
            BaseUrl = "https://api.example.invalid/v1",
            SelectedModel = "qualification-model-selected",
            ModelInventory = new ApiProviderModelInventory
            {
                SelectedModel = "qualification-model-selected",
                Models =
                [
                    new ApiProviderModelItem { Slug = "qualification-model-alternate", Enabled = true },
                    new ApiProviderModelItem { Slug = "qualification-model-selected", Enabled = true },
                    new ApiProviderModelItem { Slug = "qualification-model-disabled", Enabled = false },
                ],
            },
        };

        var catalogService = new CodexModelCatalogService(
            new PhysicalFileSystem(),
            paths,
            runtimeResolver: resolver);
        var catalogPath = catalogService.EnsureProfileModelCatalog(profile);
        Assert.False(string.IsNullOrWhiteSpace(catalogPath));
        Assert.True(File.Exists(catalogPath));
        _output.WriteLine($"Catalog content: {File.ReadAllText(catalogPath)}");

        var escapedCatalogPath = catalogPath!.Replace("\\", "\\\\", StringComparison.Ordinal);
        var providerId = profile.StableCodexProviderId;
        File.WriteAllText(
            paths.Codex.ConfigTomlPath,
            $"model_provider = \"{providerId}\"\nmodel = \"{profile.SelectedModel}\"\nmodel_catalog_json = \"{escapedCatalogPath}\"\n\n" +
            $"[model_providers.{providerId}]\nname = \"Runtime catalog qualification\"\nbase_url = \"{profile.BaseUrl}\"\nwire_api = \"responses\"\n");

        var verifier = new CodexRuntimeModelCatalogVerifier(
            resolver,
            new AppSettings { CodexExecutablePathOverride = runtime.ExecutablePath },
            paths,
            new SwitchboardCodexProcessRegistry(),
            new CodexRoutingConfigStore(new PhysicalFileSystem(), paths));
        var expectedEnabled = profile.ModelInventory.GetEnabledModels().Select(model => model.Slug).ToArray();
        await using (var client = new CodexAppServerClient(runtime.ExecutablePath, codexHome))
        {
            await client.StartAsync();
            var modelList = await client.RequestAsync("model/list", new { includeHidden = true });
            _output.WriteLine($"Raw model/list: {modelList.GetRawText()}");
        }

        var verification = await verifier.VerifyAsync(
            providerId,
            profile.SelectedModel!,
            expectedEnabled);

        _output.WriteLine($"Catalog: {catalogPath}");
        _output.WriteLine($"Expected enabled models: {string.Join(", ", expectedEnabled)}");
        _output.WriteLine($"Observed model/list entries: {string.Join(", ", verification.ObservedModels)}");
        Assert.True(verification.Succeeded, verification.FailureReason);
        Assert.Contains("qualification-model-selected", verification.ObservedModels);
        Assert.Contains("qualification-model-alternate", verification.ObservedModels);
        Assert.DoesNotContain("qualification-model-disabled", verification.ObservedModels);
    }

    [Fact]
    public async Task QualifyProductionRuntime_ProviderBlockRetentionMatrix()
    {
        var resolver = new CodexRuntimeResolver();
        var runtime = resolver.ResolveCurrentRuntime();

        _output.WriteLine($"Resolved Runtime Path: {runtime.ExecutablePath}");
        _output.WriteLine($"Resolved Runtime Version: {runtime.Version}");

        if (string.IsNullOrWhiteSpace(runtime.ExecutablePath) || !File.Exists(runtime.ExecutablePath))
        {
            _output.WriteLine("Production runtime executable not found on host. Skipping live qualification test.");
            return;
        }

        Assert.Contains("0.155.0", runtime.Version);

        // Prepare isolated CODEX_HOME
        var tempCodexHome = Path.Combine(Path.GetTempPath(), "codex_qual_prod_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempCodexHome);

        try
        {
            var configPath = Path.Combine(tempCodexHome, "config.toml");
            var authPath = Path.Combine(tempCodexHome, "auth.json");
            File.WriteAllText(authPath, "{}");

            // Initial config: provider A and provider B both present
            var initialToml = @"# Qualification Initial Config
model = ""model-a""
model_provider = ""switchboard_prov_a""

[model_providers.switchboard_prov_a]
name = ""Provider A""
base_url = ""https://api.example-a.com/v1""
wire_format = ""responses""

[model_providers.switchboard_prov_b]
name = ""Provider B""
base_url = ""https://api.example-b.com/v1""
wire_format = ""responses""
";
            File.WriteAllText(configPath, initialToml);

            string threadAId;
            string? threadAPath = null;

            // Step 1: Start Client 1 under Provider A and create persistent thread
            {
                var client1 = new CodexAppServerClient(runtime.ExecutablePath, tempCodexHome);
                await client1.StartAsync();
                try
                {
                    var startParams = new Dictionary<string, object?>
                    {
                        ["modelProvider"] = "switchboard_prov_a",
                        ["model"] = "model-a",
                        ["ephemeral"] = false
                    };

                    var startResp = await client1.RequestAsync("thread/start", startParams, TimeSpan.FromSeconds(30));
                    _output.WriteLine($"thread/start response: {startResp.GetRawText()}");

                    if (startResp.TryGetProperty("thread", out var tEl) && tEl.TryGetProperty("id", out var tidEl))
                    {
                        threadAId = tidEl.GetString()!;
                        if (tEl.TryGetProperty("path", out var pEl))
                        {
                            threadAPath = pEl.GetString();
                        }
                    }
                    else if (startResp.TryGetProperty("id", out var idEl))
                    {
                        threadAId = idEl.GetString()!;
                    }
                    else
                    {
                        throw new InvalidOperationException("Could not extract thread ID from thread/start.");
                    }

                    Assert.False(string.IsNullOrWhiteSpace(threadAId));
                    _output.WriteLine($"Created threadAId: {threadAId}, path: {threadAPath}");
                    _output.WriteLine($"File.Exists(threadAPath) before inject: {File.Exists(threadAPath)}");

                    try
                    {
                        var injectItem = new
                        {
                            type = "message",
                            role = "user",
                            content = new object[]
                            {
                                new { type = "input_text", text = "hello qualification" }
                            }
                        };
                        var injectResp = await client1.RequestAsync("thread/inject_items", new { threadId = threadAId, items = new object[] { injectItem } }, TimeSpan.FromSeconds(20));
                        _output.WriteLine($"injectItems response: {injectResp.GetRawText()}");
                    }
                    catch (Exception ex)
                    {
                        _output.WriteLine($"injectItems exception: {ex.Message}");
                    }

                    _output.WriteLine($"File.Exists(threadAPath) after inject: {File.Exists(threadAPath)}");

                    // Verify thread/read on newly created thread
                    var readResp = await client1.RequestAsync("thread/read", new { threadId = threadAId }, TimeSpan.FromSeconds(20));
                    Assert.Equal(JsonValueKind.Object, readResp.ValueKind);
                    _output.WriteLine($"Initial thread/read OK");

                    // Verify thread/list
                    var listResp = await client1.RequestAsync("thread/list", new { modelProviders = Array.Empty<string>() }, TimeSpan.FromSeconds(20));
                    Assert.Equal(JsonValueKind.Object, listResp.ValueKind);
                    _output.WriteLine($"Initial thread/list OK");

                    // CASE 1: Source Provider Block PRESENT -> Test thread/read and thread/fork
                    _output.WriteLine("--- CASE 1: Historical source provider block PRESENT ---");
                    var case1Read = await client1.RequestAsync("thread/read", new { threadId = threadAId }, TimeSpan.FromSeconds(20));
                    Assert.Equal(JsonValueKind.Object, case1Read.ValueKind);
                    _output.WriteLine("CASE 1 thread/read: PASS");

                    var forkParamsCase1 = new Dictionary<string, object?>
                    {
                        ["threadId"] = threadAId,
                        ["modelProvider"] = "switchboard_prov_b",
                        ["model"] = "model-b",
                        ["ephemeral"] = false
                    };
                    var case1Fork = await client1.RequestAsync("thread/fork", forkParamsCase1, TimeSpan.FromSeconds(30));
                    _output.WriteLine($"CASE 1 thread/fork response: {case1Fork.GetRawText()}");
                    Assert.Equal(JsonValueKind.Object, case1Fork.ValueKind);
                    _output.WriteLine("CASE 1 thread/fork: PASS");
                }
                finally
                {
                    await client1.StopAsync();
                }
            }

            // Step 2: REMOVE Provider A from config.toml completely!
            // Only Provider B remains.
            var tomlWithoutA = @"# Qualification Config Without Provider A
model = ""model-b""
model_provider = ""switchboard_prov_b""

[model_providers.switchboard_prov_b]
name = ""Provider B""
base_url = ""https://api.example-b.com/v1""
wire_format = ""responses""
";
            File.WriteAllText(configPath, tomlWithoutA);

            // Step 3: Start brand NEW app-server from production runtime (Client 2)
            _output.WriteLine("--- CASE 2: Historical source provider block ABSENT ---");
            {
                var client2 = new CodexAppServerClient(runtime.ExecutablePath, tempCodexHome);
                await client2.StartAsync();
                try
                {
                    // Test CASE 2: thread/read on historical thread created under removed provider A
                    var case2Read = await client2.RequestAsync("thread/read", new { threadId = threadAId }, TimeSpan.FromSeconds(20));
                    Assert.Equal(JsonValueKind.Object, case2Read.ValueKind);
                    _output.WriteLine("CASE 2 thread/read: PASS");

                    // Test CASE 2: thread/fork from historical thread without provider A registered
                    var forkParamsCase2 = new Dictionary<string, object?>
                    {
                        ["threadId"] = threadAId,
                        ["modelProvider"] = "switchboard_prov_b",
                        ["model"] = "model-b",
                        ["ephemeral"] = false
                    };
                    var case2Fork = await client2.RequestAsync("thread/fork", forkParamsCase2, TimeSpan.FromSeconds(30));
                    _output.WriteLine($"CASE 2 thread/fork response: {case2Fork.GetRawText()}");

                    string forkedThreadId = string.Empty;
                    bool ephemeral = true;
                    string? returnedForkedFrom = null;
                    string? returnedPath = null;

                    if (case2Fork.TryGetProperty("thread", out var ftEl))
                    {
                        if (ftEl.TryGetProperty("id", out var fidEl)) forkedThreadId = fidEl.GetString() ?? string.Empty;
                        if (ftEl.TryGetProperty("ephemeral", out var ephEl)) ephemeral = ephEl.GetBoolean();
                        if (ftEl.TryGetProperty("forkedFromId", out var ffiEl)) returnedForkedFrom = ffiEl.GetString();
                        if (ftEl.TryGetProperty("path", out var pEl)) returnedPath = pEl.GetString();
                    }
                    else if (case2Fork.TryGetProperty("id", out var fidRoot))
                    {
                        forkedThreadId = fidRoot.GetString() ?? string.Empty;
                    }

                    Assert.False(string.IsNullOrWhiteSpace(forkedThreadId), "Forked thread ID must be returned.");
                    Assert.NotEqual(threadAId, forkedThreadId);
                    Assert.False(ephemeral, "Forked thread must not be ephemeral.");

                    if (!string.IsNullOrWhiteSpace(returnedForkedFrom))
                    {
                        Assert.Equal(threadAId, returnedForkedFrom);
                    }

                    if (!string.IsNullOrWhiteSpace(returnedPath))
                    {
                        Assert.True(File.Exists(returnedPath), $"Returned path must exist on disk: {returnedPath}");
                    }

                    _output.WriteLine("CASE 2 thread/fork: PASS");
                    _output.WriteLine($"Forked Thread ID: {forkedThreadId}");
                    _output.WriteLine($"Forked From ID: {returnedForkedFrom}");
                    _output.WriteLine($"Forked Path: {returnedPath}");
                }
                finally
                {
                    await client2.StopAsync();
                }
            }
        }
        finally
        {
            try { Directory.Delete(tempCodexHome, recursive: true); } catch { }
        }
    }
}

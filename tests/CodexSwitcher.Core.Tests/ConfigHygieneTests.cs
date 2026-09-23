using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using CodexSwitcher.Core.Providers.Models;
using CodexSwitcher.Core.Routing.Models;
using CodexSwitcher.Core.Tests.TestSupport;
using CodexSwitcher.Infra.Codex.Routing;
using CodexSwitcher.Infra.Common.Paths;
using CodexSwitcher.Infra.Common.Storage;
using Xunit;

namespace CodexSwitcher.Core.Tests;

public sealed class ConfigHygieneTests
{
    private static readonly string[] SampleArgs = ["--key-id", "k1"];
    private readonly PhysicalFileSystem _fs = new();

    private const string ContaminatedHumanQaToml = @"# User-configured Codex environment
model = ""deepseek-v4.1-flash""
model_reasoning_effort = ""xhigh""
model_provider = ""openai""
model_context_window = 500000
model_catalog_json = ""C:\\Users\\Malenkiy_Solovey\\AppData\\Local\\CodexSwitchboard\\catalogs\\catalog-grok-4.7.json""

[model_providers.openrouter]
name = ""OpenRouter""
base_url = ""https://openrouter.ai/api/v1""
wire_format = ""responses""

[model_providers.routercheap]
name = ""Router Cheap""
base_url = ""https://routercheap.com/api/v1""
wire_format = ""responses""

[model_providers.hejuapi]
name = ""HeJu API""
base_url = ""https://www.hejuapi.com/v1""
wire_format = ""responses""

[model_providers.switchboard_854241da704e]
name = ""Orphan 1""
base_url = ""https://orphan1.com""

[model_providers.switchboard_86c289acb211]
name = ""Orphan 2""
base_url = ""https://orphan2.com""

[model_providers.switchboard_a48017efb1b3]
name = ""Orphan 3""
base_url = ""https://orphan3.com""

[model_providers.switchboard_168df71a76f9]
name = ""Orphan 4""
base_url = ""https://orphan4.com""

[model_providers.switchboard_94fbb6c65e41]
name = ""Orphan 5""
base_url = ""https://orphan5.com""

# User MCP configuration
[mcp_servers.filesystem]
command = ""npx""
args = [""-y"", ""@modelcontextprotocol/server-filesystem""]
";

    [Fact]
    public void CleanOrphanProviderBlocks_RemovesDeadSwitchboardBlocks_AndPreservesUserProvidersAndMcp()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Root);
        var configPath = Path.Combine(temp.Root, "config.toml");
        File.WriteAllText(configPath, ContaminatedHumanQaToml);

        var store = new CodexRoutingConfigStore(_fs, paths);

        // No active Switchboard profiles matching the 5 orphan IDs
        var activeIds = new HashSet<string>();

        store.CleanOrphanProviderBlocks(configPath, activeIds);

        var contentAfter = File.ReadAllText(configPath);

        // INVARIANT: All 5 orphan switchboard_* blocks MUST be removed
        Assert.DoesNotContain("switchboard_854241da704e", contentAfter);
        Assert.DoesNotContain("switchboard_86c289acb211", contentAfter);
        Assert.DoesNotContain("switchboard_a48017efb1b3", contentAfter);
        Assert.DoesNotContain("switchboard_168df71a76f9", contentAfter);
        Assert.DoesNotContain("switchboard_94fbb6c65e41", contentAfter);

        // INVARIANT: User-configured providers MUST be 100% preserved
        Assert.Contains("[model_providers.openrouter]", contentAfter);
        Assert.Contains(@"base_url = ""https://openrouter.ai/api/v1""", contentAfter);
        Assert.Contains("[model_providers.routercheap]", contentAfter);
        Assert.Contains(@"base_url = ""https://routercheap.com/api/v1""", contentAfter);
        Assert.Contains("[model_providers.hejuapi]", contentAfter);
        Assert.Contains(@"base_url = ""https://www.hejuapi.com/v1""", contentAfter);

        // INVARIANT: MCP servers and comments MUST be preserved
        Assert.Contains("[mcp_servers.filesystem]", contentAfter);
        Assert.Contains("# User MCP configuration", contentAfter);
    }

    [Fact]
    public void ReconcileAndRepairContaminatedConfig_RestoresCleanOpenAiFromContaminatedState()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Root);
        var configPath = Path.Combine(temp.Root, "config.toml");
        File.WriteAllText(configPath, ContaminatedHumanQaToml);

        // Create poisoned routing-state.json
        var statePath = paths.RoutingStatePath;
        Directory.CreateDirectory(paths.Root);
        var poisonedState = @"{
            ""BaselineValues"": {
                ""model_context_window"": 500000,
                ""model_catalog_json"": ""C:\\Users\\Malenkiy_Solovey\\AppData\\Local\\CodexSwitchboard\\catalogs\\catalog-grok-4.7.json"",
                ""model_reasoning_effort"": ""xhigh"",
                ""model"": ""deepseek-v4.1-flash""
            },
            ""LastAppliedValues"": {
                ""model_context_window"": 500000
            }
        }";
        File.WriteAllText(statePath, poisonedState);

        var store = new CodexRoutingConfigStore(_fs, paths);
        var activeIds = new HashSet<string>();

        // Run reconciliation and repair
        store.ReconcileAndRepairContaminatedConfig(configPath, activeIds);

        var contentAfter = File.ReadAllText(configPath);

        // INVARIANT: Routing is restored to clean OpenAI
        Assert.Contains(@"model_provider = ""openai""", contentAfter);
        Assert.DoesNotContain("model_catalog_json", contentAfter);
        Assert.DoesNotContain("model_context_window", contentAfter);
        Assert.DoesNotContain("model_reasoning_effort", contentAfter);
        Assert.DoesNotContain("deepseek-v4.1-flash", contentAfter);

        // INVARIANT: Orphan blocks removed
        Assert.DoesNotContain("switchboard_854241da704e", contentAfter);

        // INVARIANT: User providers preserved
        Assert.Contains("[model_providers.openrouter]", contentAfter);
        Assert.Contains("[model_providers.routercheap]", contentAfter);
        Assert.Contains("[model_providers.hejuapi]", contentAfter);

        // INVARIANT: Poisoned baseline is cleared from state file
        var stateJson = File.ReadAllText(statePath);
        Assert.DoesNotContain("500000", stateJson);
        Assert.DoesNotContain("catalog-grok-4.7.json", stateJson);
    }

    [Fact]
    public void ReconcileAndRepairContaminatedConfig_WhenUserHasCustomContextWithoutSwitchboardCatalog_PreservesUserContext()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Root);
        var configPath = Path.Combine(temp.Root, "config.toml");

        // User intentionally configured 500k context without any Switchboard catalog
        var userToml = @"model_provider = ""openai""
model_context_window = 500000
model_reasoning_effort = ""xhigh""
";
        File.WriteAllText(configPath, userToml);

        var store = new CodexRoutingConfigStore(_fs, paths);
        store.ReconcileAndRepairContaminatedConfig(configPath, new HashSet<string>());

        var contentAfter = File.ReadAllText(configPath);
        // Fail-safe toward user preservation: No switchboard catalog => not treated as contamination
        Assert.Contains("model_context_window = 500000", contentAfter);
        Assert.Contains(@"model_reasoning_effort = ""xhigh""", contentAfter);
    }

    [Fact]
    public void ProviderBlockHygiene_WhenChatGptActive_ZeroSwitchboardBlocks_AndUserBlocksUntouched()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Root);
        var configPath = Path.Combine(temp.Root, "config.toml");

        var initialToml = @"model_provider = ""openai""
[model_providers.openrouter]
name = ""OpenRouter""
base_url = ""https://openrouter.ai/api/v1""

[model_providers.routercheap]
name = ""Router Cheap""
base_url = ""https://routercheap.com/api/v1""
";
        File.WriteAllText(configPath, initialToml);

        var store = new CodexRoutingConfigStore(_fs, paths);
        var provider = new CodexProviderBlock(
            "switchboard_active_profile",
            "Active API",
            "https://api.active.com/v1",
            "responses",
            @"C:\broker.exe",
            SampleArgs);

        // 1. Switch to API provider -> Exactly 1 Switchboard block
        store.ApplySwitchboardRouting(configPath, provider, "api-model");
        var stateApi = store.ReadRoutingState(configPath);
        Assert.Single(stateApi.SwitchboardProviders);
        Assert.True(stateApi.SwitchboardProviders.ContainsKey("switchboard_active_profile"));

        // 2. Switch back to ChatGPT -> Exactly 0 Switchboard blocks
        store.ReturnToOpenAi(configPath);
        var stateGpt = store.ReadRoutingState(configPath);
        Assert.Empty(stateGpt.SwitchboardProviders);

        // 3. User provider blocks untouched
        var finalToml = File.ReadAllText(configPath);
        Assert.Contains("[model_providers.openrouter]", finalToml);
        Assert.Contains("[model_providers.routercheap]", finalToml);
    }
}

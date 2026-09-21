using System;
using System.Collections.Generic;
using System.IO;
using CodexSwitcher.Core.Providers.Models;
using CodexSwitcher.Core.Routing.Contracts;
using CodexSwitcher.Core.Tests.TestSupport;
using CodexSwitcher.Infra.Codex.Routing;
using CodexSwitcher.Infra.Common.Paths;
using CodexSwitcher.Infra.Common.Storage;
using Xunit;

namespace CodexSwitcher.Core.Tests;

/// <summary>
/// Verifies config.toml ownership, baseline preservation, and clean target switching:
/// - Grok 500k -> Auto removes 500k override
/// - Grok 500k -> ChatGPT cleanly removes overrides without leaking
/// - Grok 500k -> 256k updates context window
/// - Pre-existing user root keys are restored on return to OpenAI
/// - Non-secret transport settings (timeouts, headers, query params) format properly
/// - Unrelated user comments, MCP tables, and custom root keys are preserved byte-for-byte.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1861:Avoid constant arrays as arguments", Justification = "Unit test fixture data.")]
public sealed class CodexRoutingConfigOwnershipTests
{
    private readonly PhysicalFileSystem _fs = new();

    private const string InitialUserToml = @"# User-owned Codex configuration
model_provider = ""openai""
model = ""gpt-5""
unrelated_user_key = ""keep_me_safe""

# Custom MCP server
[mcp_servers.my_server]
command = ""python""
args = [""server.py""]
";

    [Fact]
    public void Switch_ApiA_500k_To_ApiB_Auto_RemovesOverridesAndCatalog()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Root);
        var configPath = Path.Combine(temp.Root, "config.toml");
        File.WriteAllText(configPath, InitialUserToml);

        var store = new CodexRoutingConfigStore(_fs, paths);

        var providerA = new CodexProviderBlock(
            "switchboard_aaaa11112222", "Grok API", "https://api.x.ai/v1", "responses",
            @"C:\broker.exe", new[] { "--key-id", "key-a" });

        var overridesA = new CodexModelOverrides
        {
            ContextWindowTokens = 500000,
            AutoCompactTokenLimit = 400000,
            AutoCompactTokenLimitScope = CompactLimitScope.Total,
            ReasoningEffort = CodexReasoningEffort.High,
            ReasoningSummary = CodexReasoningSummary.Detailed
        };

        var catalogPathA = Path.Combine(temp.Root, "catalogs", "catalog-grok.json");

        // 1. Switch to API A (Grok 500k)
        store.ApplySwitchboardRouting(configPath, providerA, "grok-4.6", overridesA, catalogPathA);

        var contentA = File.ReadAllText(configPath);
        Assert.Contains(@"model_provider = ""switchboard_aaaa11112222""", contentA);
        Assert.Contains(@"model = ""grok-4.6""", contentA);
        Assert.Contains("model_context_window = 500000", contentA);
        Assert.Contains("model_auto_compact_token_limit = 400000", contentA);
        Assert.Contains(@"model_auto_compact_token_limit_scope = ""total""", contentA);
        Assert.Contains(@"model_reasoning_effort = ""high""", contentA);
        Assert.Contains(@"model_reasoning_summary = ""detailed""", contentA);
        Assert.Contains("model_catalog_json = ", contentA);
        Assert.Contains(@"unrelated_user_key = ""keep_me_safe""", contentA);

        // 2. Switch to API B (Auto context, no overrides)
        var providerB = new CodexProviderBlock(
            "switchboard_bbbb22223333", "Default API", "https://api.example.com/v1", "responses",
            @"C:\broker.exe", new[] { "--key-id", "key-b" });

        store.ApplySwitchboardRouting(configPath, providerB, "standard-model", modelOverrides: null, modelCatalogJson: null);

        var contentB = File.ReadAllText(configPath);
        Assert.Contains(@"model_provider = ""switchboard_bbbb22223333""", contentB);
        Assert.Contains(@"model = ""standard-model""", contentB);

        // INVARIANT: Overrides from Target A must be completely removed
        Assert.DoesNotContain("model_context_window", contentB);
        Assert.DoesNotContain("model_auto_compact_token_limit", contentB);
        Assert.DoesNotContain("model_auto_compact_token_limit_scope", contentB);
        Assert.DoesNotContain("model_reasoning_effort", contentB);
        Assert.DoesNotContain("model_reasoning_summary", contentB);
        Assert.DoesNotContain("model_catalog_json", contentB);

        // Unrelated user config must still be intact
        Assert.Contains(@"unrelated_user_key = ""keep_me_safe""", contentB);
        Assert.Contains("[mcp_servers.my_server]", contentB);
    }

    [Fact]
    public void Switch_ApiA_To_ChatGPT_RemovesAllGrokOverridesAndCatalog()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Root);
        var configPath = Path.Combine(temp.Root, "config.toml");
        File.WriteAllText(configPath, InitialUserToml);

        var store = new CodexRoutingConfigStore(_fs, paths);

        var providerA = new CodexProviderBlock(
            "switchboard_aaaa11112222", "Grok API", "https://api.x.ai/v1", "responses",
            @"C:\broker.exe", new[] { "--key-id", "key-a" });

        var overridesA = new CodexModelOverrides
        {
            ContextWindowTokens = 500000,
            ReasoningEffort = CodexReasoningEffort.High
        };

        var catalogPathA = Path.Combine(temp.Root, "catalogs", "catalog-grok.json");

        // Switch to API A
        store.ApplySwitchboardRouting(configPath, providerA, "grok-4.6", overridesA, catalogPathA);

        // Switch back to ChatGPT (OpenAI)
        store.ReturnToOpenAi(configPath);

        var contentAfter = File.ReadAllText(configPath);
        Assert.Contains(@"model_provider = ""openai""", contentAfter);

        // INVARIANT: No stale Grok overrides leak into ChatGPT
        Assert.DoesNotContain("model_context_window", contentAfter);
        Assert.DoesNotContain("model_reasoning_effort", contentAfter);
        Assert.DoesNotContain("model_catalog_json", contentAfter);

        Assert.Contains(@"unrelated_user_key = ""keep_me_safe""", contentAfter);
        Assert.Contains("[mcp_servers.my_server]", contentAfter);
    }

    [Fact]
    public void Switch_ApiA_500k_To_ApiC_256k_UpdatesOverridesDirectly()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Root);
        var configPath = Path.Combine(temp.Root, "config.toml");
        File.WriteAllText(configPath, InitialUserToml);

        var store = new CodexRoutingConfigStore(_fs, paths);

        var providerA = new CodexProviderBlock(
            "switchboard_aaaa11112222", "Grok API", "https://api.x.ai/v1", "responses",
            @"C:\broker.exe", new[] { "--key-id", "key-a" });
        var overridesA = new CodexModelOverrides { ContextWindowTokens = 500000 };

        store.ApplySwitchboardRouting(configPath, providerA, "grok-4.6", overridesA, "C:\\catalog.json");

        // Switch to Provider C with 256k (no catalog needed)
        var providerC = new CodexProviderBlock(
            "switchboard_cccc33334444", "Provider C", "https://c.example.com/v1", "responses",
            @"C:\broker.exe", new[] { "--key-id", "key-c" });
        var overridesC = new CodexModelOverrides { ContextWindowTokens = 256000 };

        store.ApplySwitchboardRouting(configPath, providerC, "model-c", overridesC, modelCatalogJson: null);

        var content = File.ReadAllText(configPath);
        Assert.Contains("model_context_window = 256000", content);
        Assert.DoesNotContain("500000", content);
        Assert.DoesNotContain("model_catalog_json", content);
    }

    [Fact]
    public void ExistingUserRootKey_IsRestoredWhenSwitchboardReverts()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Root);
        var configPath = Path.Combine(temp.Root, "config.toml");

        // User already had their own model_context_window = 128000 before Switchboard ran
        var userTomlWithContext = @"model_provider = ""openai""
model = ""gpt-4""
model_context_window = 128000
";
        File.WriteAllText(configPath, userTomlWithContext);

        var store = new CodexRoutingConfigStore(_fs, paths);
        var provider = new CodexProviderBlock(
            "switchboard_aaaa11112222", "Grok API", "https://api.x.ai/v1", "responses",
            @"C:\broker.exe", new[] { "--key-id", "key-a" });
        var overrides = new CodexModelOverrides { ContextWindowTokens = 500000 };

        store.ApplySwitchboardRouting(configPath, provider, "grok-4.6", overrides);

        var contentDuring = File.ReadAllText(configPath);
        Assert.Contains("model_context_window = 500000", contentDuring);

        // Return to OpenAI
        store.ReturnToOpenAi(configPath);

        var contentAfter = File.ReadAllText(configPath);
        // INVARIANT: User's pre-existing baseline value is preserved and restored!
        Assert.Contains("model_context_window = 128000", contentAfter);
    }

    [Fact]
    public void TransportOverrides_FormattedInProviderBlock()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Root);
        var configPath = Path.Combine(temp.Root, "config.toml");
        File.WriteAllText(configPath, InitialUserToml);

        var store = new CodexRoutingConfigStore(_fs, paths);

        var provider = new CodexProviderBlock(
            "switchboard_123456789abc",
            "Advanced Provider",
            "https://adv.example.com/v1",
            "responses",
            @"C:\broker.exe",
            new[] { "--key-id", "id" },
            5000,
            RequestMaxRetries: 3,
            StreamMaxRetries: 5,
            StreamIdleTimeoutMs: 30000,
            WebSocketConnectTimeoutMs: 10000,
            SupportsWebSockets: true,
            SupportsStandaloneWebSearch: false,
            QueryParams: new Dictionary<string, string> { ["api-version"] = "2024-02" },
            HttpHeaders: new Dictionary<string, string> { ["X-Custom-Client"] = "SwitchboardTest" });

        store.ApplySwitchboardRouting(configPath, provider, "adv-model");

        var content = File.ReadAllText(configPath);
        Assert.Contains("request_max_retries = 3", content);
        Assert.Contains("stream_max_retries = 5", content);
        Assert.Contains("stream_idle_timeout_ms = 30000", content);
        Assert.Contains("websocket_connect_timeout_ms = 10000", content);
        Assert.Contains("supports_websockets = true", content);
        Assert.Contains("supports_standalone_web_search = false", content);
        Assert.Contains("[model_providers.switchboard_123456789abc.query_params]", content);
        Assert.Contains(@"api-version = ""2024-02""", content);
        Assert.Contains("[model_providers.switchboard_123456789abc.http_headers]", content);
        Assert.Contains(@"X-Custom-Client = ""SwitchboardTest""", content);

        // Read routing state parses them back
        var state = store.ReadRoutingState(configPath);
        Assert.True(state.SwitchboardProviders.TryGetValue("switchboard_123456789abc", out var parsed));
        Assert.Equal(3UL, parsed.RequestMaxRetries);
        Assert.Equal(5UL, parsed.StreamMaxRetries);
        Assert.Equal(30000UL, parsed.StreamIdleTimeoutMs);
        Assert.Equal(10000UL, parsed.WebSocketConnectTimeoutMs);
        Assert.True(parsed.SupportsWebSockets);
        Assert.False(parsed.SupportsStandaloneWebSearch);
        Assert.NotNull(parsed.QueryParams);
        Assert.Equal("2024-02", parsed.QueryParams["api-version"]);
        Assert.NotNull(parsed.HttpHeaders);
        Assert.Equal("SwitchboardTest", parsed.HttpHeaders["X-Custom-Client"]);
    }
}

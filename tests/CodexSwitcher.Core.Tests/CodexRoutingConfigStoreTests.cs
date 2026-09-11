using CodexSwitcher.Core.Abstractions;
using CodexSwitcher.Core.Tests.TestSupport;
using CodexSwitcher.Infra;
using CodexSwitcher.Infra.Codex;
using CodexSwitcher.Infra.Io;
using Xunit;

namespace CodexSwitcher.Core.Tests;

public sealed class CodexRoutingConfigStoreTests
{
    private readonly PhysicalFileSystem _fs = new();

    private const string ComplexToml = @"# Top-level configuration for Codex
cli_auth_credentials_store = ""file""
model_provider = ""openai""
model = ""gpt-5.3-codex""
temperature = 0.2
unmanaged_root_key = ""should_be_preserved_123""

# Features table
[features]
enable_telemetry = false
fast_mode = true

# MCP servers configuration
[mcp_servers.filesystem]
command = ""npx""
args = [""-y"", ""@modelcontextprotocol/server-filesystem"", ""C:\\workspace""]
env = { DEBUG = ""true"", LOG_LEVEL = ""info"" }

# User-defined model provider
[model_providers.custom_company]
name = ""Internal Company LLM""
base_url = ""https://llm.internal.corp/v1""
wire_api = ""responses""

[model_providers.custom_company.auth]
command = ""cmd.exe""
args = [""/c"", ""echo token""]

# Quoted table name
[""profiles"".""developer-mode""]
instructions = """"
You are a senior engineer.
Preserve all formatting and comments.
""""
";

    [Fact]
    public void ReadRoutingState_OnComplexToml_ExtractsRootKeysAndFingerprint()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Root);
        var configPath = Path.Combine(temp.Root, "config.toml");
        File.WriteAllText(configPath, ComplexToml);

        var store = new CodexRoutingConfigStore(_fs, paths);
        var state = store.ReadRoutingState(configPath);

        Assert.Equal("openai", state.ModelProvider);
        Assert.Equal("gpt-5.3-codex", state.Model);
        Assert.NotEmpty(state.Fingerprint);
        Assert.Empty(state.SwitchboardProviders);
    }

    [Fact]
    public void ApplySwitchboardRouting_PreservesAllUnrelatedTablesAndComments()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Root);
        var configPath = Path.Combine(temp.Root, "config.toml");
        File.WriteAllText(configPath, ComplexToml);

        var store = new CodexRoutingConfigStore(_fs, paths);
        var provider = new CodexProviderBlock(
            "switchboard_112233445566",
            "Router.Cheap",
            "https://router.cheap/v1",
            "responses",
            @"C:\Program Files\Codex Switchboardroker\CodexSwitchboard.KeyBroker.exe",
            new[] { "--key-id", "d047e1ff-3e5f-4a0b-96ce-906cbdf09f3e" },
            5000);

        var newFp = store.ApplySwitchboardRouting(configPath, provider, "gpt-5.6-sol");
        Assert.NotEmpty(newFp);

        var content = File.ReadAllText(configPath);

        // Check new routing
        var state = store.ReadRoutingState(configPath);
        Assert.Equal("switchboard_112233445566", state.ModelProvider);
        Assert.Equal("gpt-5.6-sol", state.Model);
        Assert.True(state.SwitchboardProviders.ContainsKey("switchboard_112233445566"));

        // Check preservation
        Assert.Contains("# Top-level configuration", content);
        Assert.Contains(@"unmanaged_root_key = ""should_be_preserved_123""", content);
        Assert.Contains("# MCP servers configuration", content);
        Assert.Contains(@"[model_providers.custom_company]", content);
        Assert.Contains(@"[""profiles"".""developer-mode""]", content);
        Assert.Contains("Preserve all formatting and comments.", content);
    }

    [Fact]
    public void ConcurrentModification_ThrowsConcurrentModificationException()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Root);
        var configPath = Path.Combine(temp.Root, "config.toml");
        File.WriteAllText(configPath, ComplexToml);

        var store = new CodexRoutingConfigStore(_fs, paths);
        var provider = new CodexProviderBlock(
            "switchboard_112233445566",
            "Router.Cheap",
            "https://router.cheap/v1",
            "responses",
            @"C:roker.exe",
            new[] { "--key-id", "guid" },
            5000);

        // Pass an outdated expected fingerprint
        Assert.Throws<ConcurrentModificationException>(() =>
            store.ApplySwitchboardRouting(configPath, provider, "gpt-5.6-sol", expectedFingerprint: "stale_hash_123"));
    }

    [Fact]
    public void UpdateProviderRoute_ChangesBaseUrlOnly()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Root);
        var configPath = Path.Combine(temp.Root, "config.toml");
        File.WriteAllText(configPath, ComplexToml);

        var store = new CodexRoutingConfigStore(_fs, paths);
        var provider = new CodexProviderBlock(
            "switchboard_112233445566",
            "Router.Cheap",
            "https://router.cheap/v1",
            "responses",
            @"C:roker.exe",
            new[] { "--key-id", "guid" },
            5000);

        store.ApplySwitchboardRouting(configPath, provider, "gpt-5.6-sol");

        store.UpdateProviderRoute(configPath, "switchboard_112233445566", "https://direct.router-cheap.com/v1");

        var state = store.ReadRoutingState(configPath);
        Assert.Equal("https://direct.router-cheap.com/v1", state.SwitchboardProviders["switchboard_112233445566"].BaseUrl);
    }

    [Fact]
    public void ReturnToOpenAi_SetsModelProviderToOpenAi_PreservesProviderBlocks()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Root);
        var configPath = Path.Combine(temp.Root, "config.toml");
        File.WriteAllText(configPath, ComplexToml);

        var store = new CodexRoutingConfigStore(_fs, paths);
        var provider = new CodexProviderBlock(
            "switchboard_112233445566",
            "Router.Cheap",
            "https://router.cheap/v1",
            "responses",
            @"C:roker.exe",
            new[] { "--key-id", "guid" },
            5000);

        store.ApplySwitchboardRouting(configPath, provider, "gpt-5.6-sol");
        store.ReturnToOpenAi(configPath);

        var state = store.ReadRoutingState(configPath);
        Assert.Equal("openai", state.ModelProvider);
        Assert.True(state.SwitchboardProviders.ContainsKey("switchboard_112233445566"));
    }
    [Fact]
    public void ApplySwitchboardRouting_WithTwoSwitchboardProviders_BothCoexist()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Root);
        var configPath = Path.Combine(temp.Root, "config.toml");
        File.WriteAllText(configPath, "# Config\nmodel_provider = \"openai\"\n");

        var store = new CodexRoutingConfigStore(_fs, paths);
        var providerA = new CodexProviderBlock(
            "switchboard_aaaa11112222", "Provider A", "https://a.example/v1", "responses",
            @"C:\broker.exe", new[] { "--key-id", "key-a" });
        var providerB = new CodexProviderBlock(
            "switchboard_bbbb33334444", "Provider B", "https://b.example/v1", "responses",
            @"C:\broker.exe", new[] { "--key-id", "key-b" });

        store.ApplySwitchboardRouting(configPath, providerA, "model-a");
        store.ApplySwitchboardRouting(configPath, providerB, "model-b");

        var state = store.ReadRoutingState(configPath);
        Assert.Equal("switchboard_bbbb33334444", state.ModelProvider);
        Assert.Equal("model-b", state.Model);
        Assert.Equal(2, state.SwitchboardProviders.Count);
        Assert.True(state.SwitchboardProviders.ContainsKey("switchboard_aaaa11112222"));
        Assert.True(state.SwitchboardProviders.ContainsKey("switchboard_bbbb33334444"));
    }

    [Fact]
    public void ApplySwitchboardRouting_OnMissingConfig_CreatesValidToml()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Root);
        var configPath = Path.Combine(temp.Root, "config.toml");

        var store = new CodexRoutingConfigStore(_fs, paths);
        var provider = new CodexProviderBlock(
            "switchboard_12345678abcd", "Provider Fresh", "https://fresh.example/v1", "responses",
            @"C:\broker.exe", new[] { "--key-id", "key-fresh" });

        store.ApplySwitchboardRouting(configPath, provider, "model-fresh");

        Assert.True(File.Exists(configPath));
        var state = store.ReadRoutingState(configPath);
        Assert.Equal("switchboard_12345678abcd", state.ModelProvider);
        Assert.Equal("model-fresh", state.Model);
    }
}

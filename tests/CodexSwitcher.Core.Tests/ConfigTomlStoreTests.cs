using CodexSwitcher.Core.Tests.TestSupport;

namespace CodexSwitcher.Core.Tests;

/// <summary>
/// Garante que forçar cli_auth_credentials_store="file" preserva o resto do config.toml
/// (o do usuário é grande e valioso). Ver BUSINESS_RULES.md ponto 1.
/// </summary>
public sealed class ConfigTomlStoreTests
{
    private const string RealisticConfig =
        "model = \"gpt-5.5\"\n" +
        "model_reasoning_effort = \"medium\"\n" +
        "\n" +
        "[mcp]\n" +
        "remote_mcp_client_enabled = true\n" +
        "\n" +
        "[windows]\n" +
        "sandbox = \"elevated\"\n";

    private static (ConfigTomlStore store, string path) Make(TempDir dir, string content)
    {
        var fs = new PhysicalFileSystem();
        var path = dir.Combine("config.toml");
        fs.WriteAllTextAtomic(path, content);
        return (new ConfigTomlStore(fs), path);
    }

    [Fact]
    public void EnsureFileStore_KeyAbsent_InsertsBeforeFirstTable_PreservesEverything()
    {
        using var dir = new TempDir();
        var (store, path) = Make(dir, RealisticConfig);

        var changed = store.EnsureFileStore(path);

        Assert.True(changed);
        var text = File.ReadAllText(path);
        Assert.Contains("cli_auth_credentials_store = \"file\"", text);
        // Tudo preservado:
        Assert.Contains("model = \"gpt-5.5\"", text);
        Assert.Contains("[mcp]", text);
        Assert.Contains("remote_mcp_client_enabled = true", text);
        Assert.Contains("[windows]", text);
        Assert.Contains("sandbox = \"elevated\"", text);
        // A chave é de nível-raiz: aparece antes do primeiro cabeçalho de tabela.
        Assert.True(text.IndexOf("cli_auth_credentials_store", StringComparison.Ordinal)
                    < text.IndexOf("[mcp]", StringComparison.Ordinal));
    }

    [Fact]
    public void EnsureFileStore_KeyIsKeyring_ReplacesWithFile()
    {
        using var dir = new TempDir();
        var (store, path) = Make(dir, "cli_auth_credentials_store = \"keyring\"\n" + RealisticConfig);

        var changed = store.EnsureFileStore(path);

        Assert.True(changed);
        var text = File.ReadAllText(path);
        Assert.Contains("cli_auth_credentials_store = \"file\"", text);
        Assert.DoesNotContain("keyring", text);
    }

    [Fact]
    public void EnsureFileStore_AlreadyFile_NoChange_Idempotent()
    {
        using var dir = new TempDir();
        var (store, path) = Make(dir, "cli_auth_credentials_store = \"file\"\n" + RealisticConfig);
        var before = File.ReadAllText(path);

        var changed = store.EnsureFileStore(path);

        Assert.False(changed);
        Assert.Equal(before, File.ReadAllText(path)); // nem reescreve
    }

    [Fact]
    public void EnsureFileStore_MissingFile_CreatesWithKey()
    {
        using var dir = new TempDir();
        var fs = new PhysicalFileSystem();
        var path = dir.Combine("config.toml");
        var store = new ConfigTomlStore(fs);

        var changed = store.EnsureFileStore(path);

        Assert.True(changed);
        Assert.Contains("cli_auth_credentials_store = \"file\"", File.ReadAllText(path));
    }

    [Fact]
    public void ReadCredentialsStore_ReturnsCorrectKind()
    {
        using var dir = new TempDir();
        var (store1, p1) = Make(dir, RealisticConfig);
        Assert.Equal(CredentialsStoreKind.Unset, store1.ReadCredentialsStore(p1));

        var (store2, p2) = Make(new TempDir(), "cli_auth_credentials_store = \"keyring\"\n");
        Assert.Equal(CredentialsStoreKind.Keyring, store2.ReadCredentialsStore(p2));
    }

    [Fact]
    public void EnsureFileStore_DoesNotMatchKeyInsideTable()
    {
        using var dir = new TempDir();
        // Uma chave homônima dentro de uma tabela não deve ser considerada a raiz.
        var content = "model = \"x\"\n[some_table]\ncli_auth_credentials_store = \"keyring\"\n";
        var (store, path) = Make(dir, content);

        store.EnsureFileStore(path);

        var text = File.ReadAllText(path);
        // Deve ter inserido a chave de raiz (antes de [some_table]) e NÃO alterado a de dentro da tabela.
        var rootIdx = text.IndexOf("cli_auth_credentials_store = \"file\"", StringComparison.Ordinal);
        Assert.True(rootIdx >= 0);
        Assert.True(rootIdx < text.IndexOf("[some_table]", StringComparison.Ordinal));
        Assert.Contains("cli_auth_credentials_store = \"keyring\"", text); // a de dentro da tabela permanece
    }
}

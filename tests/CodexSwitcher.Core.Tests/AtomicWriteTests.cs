using System.Text;
using CodexSwitcher.Core.Tests.TestSupport;

namespace CodexSwitcher.Core.Tests;

/// <summary>
/// Prova a escrita atômica do slot ativo / cofre: substitui conteúdo sem deixar temporários e
/// sem estados intermediários corrompidos. Ver BUSINESS_RULES.md ponto 10.
/// </summary>
public sealed class AtomicWriteTests
{
    [Fact]
    public void WriteAtomic_CreatesFile_WithExactContent()
    {
        using var dir = new TempDir();
        var fs = new PhysicalFileSystem();
        var path = dir.Combine("auth.json");
        var content = Encoding.UTF8.GetBytes("""{"hello":"world"}""");

        fs.WriteAllBytesAtomic(path, content);

        Assert.Equal(content, File.ReadAllBytes(path));
    }

    [Fact]
    public void WriteAtomic_Overwrite_ReplacesContent()
    {
        using var dir = new TempDir();
        var fs = new PhysicalFileSystem();
        var path = dir.Combine("auth.json");

        fs.WriteAllBytesAtomic(path, Encoding.UTF8.GetBytes("old"));
        fs.WriteAllBytesAtomic(path, Encoding.UTF8.GetBytes("new-and-longer-content"));

        Assert.Equal("new-and-longer-content", File.ReadAllText(path));
    }

    [Fact]
    public void WriteAtomic_LeavesNoTempFiles()
    {
        using var dir = new TempDir();
        var fs = new PhysicalFileSystem();
        var path = dir.Combine("auth.json");

        fs.WriteAllBytesAtomic(path, Encoding.UTF8.GetBytes("data"));

        var leftovers = Directory.EnumerateFiles(dir.Root, "*.tmp-*").ToList();
        Assert.Empty(leftovers);
    }

    [Fact]
    public void WriteAtomic_CreatesMissingDirectory()
    {
        using var dir = new TempDir();
        var fs = new PhysicalFileSystem();
        var path = dir.Combine("nested", "deep", "auth.json");

        fs.WriteAllBytesAtomic(path, Encoding.UTF8.GetBytes("x"));

        Assert.True(File.Exists(path));
    }

    [Fact]
    public void WriteTextAtomic_IsUtf8_NoBom()
    {
        using var dir = new TempDir();
        var fs = new PhysicalFileSystem();
        var path = dir.Combine("profiles.json");

        fs.WriteAllTextAtomic(path, "áçã");

        var bytes = File.ReadAllBytes(path);
        // Sem BOM (EF BB BF) no início.
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        Assert.Equal("áçã", Encoding.UTF8.GetString(bytes));
    }
}

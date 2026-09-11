using CodexSwitcher.Core.Catalog;
using Xunit;

namespace CodexSwitcher.Core.Tests;

public sealed class SigningKeyLeakTests
{
    [Fact]
    public void RepositoryFiles_ContainNoPlaintextPrivateKeysOrKeyFiles()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        Assert.True(Directory.Exists(repoRoot), $"Repo root not found at {repoRoot}");

        var forbiddenMarkers = new[]
        {
            "-----" + "BEGIN " + "PRIVATE KEY" + "-----",
            "-----" + "BEGIN " + "EC PRIVATE KEY" + "-----",
            "-----" + "BEGIN " + "RSA PRIVATE KEY" + "-----",
        };

        var forbiddenExtensions = new[]
        {
            ".pfx",
            ".p12",
            ".pem"
        };

        var scannedFiles = 0;

        foreach (var file in Directory.EnumerateFiles(repoRoot, "*.*", SearchOption.AllDirectories))
        {
            var relPath = Path.GetRelativePath(repoRoot, file);

            // Skip git metadata, binary artifacts, build outputs, and IDE data
            if (relPath.StartsWith(".git", StringComparison.OrdinalIgnoreCase) ||
                relPath.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase) ||
                relPath.Contains(@"\obj\", StringComparison.OrdinalIgnoreCase) ||
                relPath.Contains(@"/.git/", StringComparison.OrdinalIgnoreCase) ||
                relPath.Contains(@"/bin/", StringComparison.OrdinalIgnoreCase) ||
                relPath.Contains(@"/obj/", StringComparison.OrdinalIgnoreCase) ||
                relPath.StartsWith(".vs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var ext = Path.GetExtension(file);
            Assert.DoesNotContain(ext, forbiddenExtensions);

            Assert.NotEqual("official_catalog_signing_key.pem", Path.GetFileName(file), StringComparer.OrdinalIgnoreCase);

            // Check contents of text files
            if (ext is ".cs" or ".json" or ".toml" or ".md" or ".txt" or ".xml" or ".csproj" or ".slnx" or ".sig")
            {
                var content = File.ReadAllText(file);
                foreach (var marker in forbiddenMarkers)
                {
                    Assert.DoesNotContain(marker, content);
                }
                scannedFiles++;
            }
        }

        Assert.True(scannedFiles > 20, $"Expected to scan repository files, but scanned only {scannedFiles}");
    }

    [Fact]
    public void EmbeddedPublicKey_IsPresentAndValid()
    {
        Assert.False(string.IsNullOrWhiteSpace(CatalogSecurityConstants.OfficialCatalogPublicKeyBase64));
        Assert.DoesNotContain("PRIVATE", CatalogSecurityConstants.OfficialCatalogPublicKeyBase64);
        var bytes = Convert.FromBase64String(CatalogSecurityConstants.OfficialCatalogPublicKeyBase64);
        Assert.True(bytes.Length > 0);
    }
}

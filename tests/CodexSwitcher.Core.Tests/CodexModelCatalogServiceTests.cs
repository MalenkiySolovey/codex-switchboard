using System.IO;
using System.Text.Json;
using CodexSwitcher.Core.Tests.TestSupport;
using CodexSwitcher.Infra.Codex.Runtime;
using CodexSwitcher.Infra.Common.Paths;
using CodexSwitcher.Infra.Common.Storage;
using Xunit;

namespace CodexSwitcher.Core.Tests;

public sealed class CodexModelCatalogServiceTests
{
    private readonly PhysicalFileSystem _fs = new();

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
    public void EnsureModelCatalog_WhenContextExceedsCeiling_GeneratesValidJsonCatalog()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Root);
        var service = new CodexModelCatalogService(_fs, paths);

        var result = service.EnsureModelCatalog("grok-4.6", 500000);
        Assert.NotNull(result);
        Assert.True(File.Exists(result));

        var json = File.ReadAllText(result);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal(1, doc.RootElement.GetArrayLength());

        var model = doc.RootElement[0];
        Assert.Equal("grok-4.6", model.GetProperty("slug").GetString());
        Assert.Equal(500000L, model.GetProperty("context_window").GetInt64());
        Assert.Equal(1000000L, model.GetProperty("max_context_window").GetInt64());
        Assert.True(model.GetProperty("supported_in_api").GetBoolean());
    }

    [Fact]
    public void EnsureModelCatalog_WithMillionTokens_ScalesMaxContextWindow()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Root);
        var service = new CodexModelCatalogService(_fs, paths);

        var result = service.EnsureModelCatalog("grok-4.20", 2000000);
        Assert.NotNull(result);

        var json = File.ReadAllText(result);
        using var doc = JsonDocument.Parse(json);
        var model = doc.RootElement[0];
        Assert.Equal(2000000L, model.GetProperty("context_window").GetInt64());
        Assert.Equal(2000000L, model.GetProperty("max_context_window").GetInt64());
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
}

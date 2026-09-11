using System.Text.Json;
using CodexSwitcher.Core.Catalog;
using Xunit;

namespace CodexSwitcher.Core.Tests;

public sealed class JsonPointerExtractorTests
{
    private const string SampleJson = """
    {
      "data": {
        "balance": 45.67,
        "balance_str": "123.45",
        "used_cents": 2500,
        "limits": [100, 200, 300],
        "nested/key": 99.9,
        "tilde~key": 88.8,
        "models": [
          { "id": "gpt-5.6-sol" },
          { "id": "claude-3-5-sonnet" },
          { "id": "deepseek-chat" }
        ],
        "string_models": [
          "model-alpha",
          "model-beta"
        ]
      },
      "currency": "USD",
      "status": "ok"
    }
    """;

    [Fact]
    public void TryExtractDecimal_DirectNumber_ExtractsSuccessfully()
    {
        using var doc = JsonDocument.Parse(SampleJson);
        var mapping = new JsonFieldMapping { Pointer = "/data/balance", Type = "decimal" };

        var ok = JsonPointerExtractor.TryExtractDecimal(doc.RootElement, mapping, out var val);
        Assert.True(ok);
        Assert.Equal(45.67m, val);
    }

    [Fact]
    public void TryExtractDecimal_StringNumber_ParsesSuccessfully()
    {
        using var doc = JsonDocument.Parse(SampleJson);
        var mapping = new JsonFieldMapping { Pointer = "/data/balance_str", Type = "decimal" };

        var ok = JsonPointerExtractor.TryExtractDecimal(doc.RootElement, mapping, out var val);
        Assert.True(ok);
        Assert.Equal(123.45m, val);
    }

    [Fact]
    public void TryExtractDecimal_ScaleTransform_MultipliesCorrectly()
    {
        using var doc = JsonDocument.Parse(SampleJson);
        var mapping = new JsonFieldMapping
        {
            Pointer = "/data/balance",
            Type = "decimal",
            Transform = "scale",
            ScaleFactor = 2.5m
        };

        var ok = JsonPointerExtractor.TryExtractDecimal(doc.RootElement, mapping, out var val);
        Assert.True(ok);
        Assert.Equal(45.67m * 2.5m, val);
    }

    [Fact]
    public void TryExtractDecimal_CentsToUnitsTransform_DividesBy100()
    {
        using var doc = JsonDocument.Parse(SampleJson);
        var mapping = new JsonFieldMapping
        {
            Pointer = "/data/used_cents",
            Type = "decimal",
            Transform = "centsToUnits"
        };

        var ok = JsonPointerExtractor.TryExtractDecimal(doc.RootElement, mapping, out var val);
        Assert.True(ok);
        Assert.Equal(25.00m, val);
    }

    [Fact]
    public void TryExtractDecimal_ArrayIndex_NavigatesCorrectly()
    {
        using var doc = JsonDocument.Parse(SampleJson);
        var mapping = new JsonFieldMapping { Pointer = "/data/limits/1", Type = "decimal" };

        var ok = JsonPointerExtractor.TryExtractDecimal(doc.RootElement, mapping, out var val);
        Assert.True(ok);
        Assert.Equal(200m, val);
    }

    [Fact]
    public void TryExtractDecimal_EscapedTokens_HandledPerRfc6901()
    {
        using var doc = JsonDocument.Parse(SampleJson);
        // ~1 encodes '/'
        var slashMapping = new JsonFieldMapping { Pointer = "/data/nested~1key", Type = "decimal" };
        var okSlash = JsonPointerExtractor.TryExtractDecimal(doc.RootElement, slashMapping, out var valSlash);
        Assert.True(okSlash);
        Assert.Equal(99.9m, valSlash);

        // ~0 encodes '~'
        var tildeMapping = new JsonFieldMapping { Pointer = "/data/tilde~0key", Type = "decimal" };
        var okTilde = JsonPointerExtractor.TryExtractDecimal(doc.RootElement, tildeMapping, out var valTilde);
        Assert.True(okTilde);
        Assert.Equal(88.8m, valTilde);
    }

    [Fact]
    public void TryExtractDecimal_MissingOrInvalidPointer_ReturnsFalse()
    {
        using var doc = JsonDocument.Parse(SampleJson);
        var mapping = new JsonFieldMapping { Pointer = "/data/non_existent", Type = "decimal" };

        var ok = JsonPointerExtractor.TryExtractDecimal(doc.RootElement, mapping, out _);
        Assert.False(ok);
    }

    [Fact]
    public void TryExtractString_ValidPointer_ReturnsString()
    {
        using var doc = JsonDocument.Parse(SampleJson);
        var ok = JsonPointerExtractor.TryExtractString(doc.RootElement, "/currency", out var curr);
        Assert.True(ok);
        Assert.Equal("USD", curr);
    }

    [Fact]
    public void TryExtractStringArray_ObjectsWithId_ExtractsAll()
    {
        using var doc = JsonDocument.Parse(SampleJson);
        var ok = JsonPointerExtractor.TryExtractStringArray(doc.RootElement, "/data/models", out var models);
        Assert.True(ok);
        Assert.Equal(3, models.Count);
        Assert.Contains("gpt-5.6-sol", models);
        Assert.Contains("claude-3-5-sonnet", models);
        Assert.Contains("deepseek-chat", models);
    }

    [Fact]
    public void TryExtractStringArray_PrimitiveStrings_ExtractsAll()
    {
        using var doc = JsonDocument.Parse(SampleJson);
        var ok = JsonPointerExtractor.TryExtractStringArray(doc.RootElement, "/data/string_models", out var models);
        Assert.True(ok);
        Assert.Equal(2, models.Count);
        Assert.Contains("model-alpha", models);
        Assert.Contains("model-beta", models);
    }
}

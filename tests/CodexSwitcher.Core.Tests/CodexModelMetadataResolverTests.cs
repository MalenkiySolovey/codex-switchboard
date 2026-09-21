using CodexSwitcher.Core.Providers.Services;
using Xunit;

namespace CodexSwitcher.Core.Tests;

public sealed class CodexModelMetadataResolverTests
{
    private readonly CodexModelMetadataResolver _resolver = new();

    [Theory]
    [InlineData("grok-4.6", 500_000, true)]
    [InlineData("grok-4.6-0205", 500_000, true)]
    [InlineData("grok-4.20", 1_000_000, true)]
    [InlineData("grok-beta", 131_072, true)]
    [InlineData("gpt-4o", 128_000, true)]
    [InlineData("o1", 200_000, true)]
    [InlineData("claude-3-5-sonnet", 200_000, true)]
    [InlineData("deepseek-chat", 64_000, true)]
    public void ResolveLimitFact_KnownModels_ReturnsAccurateDocumentedContext(string slug, long expectedMax, bool expectedKnown)
    {
        var fact = _resolver.ResolveLimitFact(slug);

        Assert.Equal(expectedKnown, fact.IsKnown);
        Assert.Equal(expectedMax, fact.DocumentedMaxContext);
        Assert.False(string.IsNullOrWhiteSpace(fact.SourceDescription));
    }

    [Theory]
    [InlineData("unknown-llama-3", null, false)]
    [InlineData("custom-provider-model", null, false)]
    [InlineData("", null, false)]
    [InlineData("   ", null, false)]
    public void ResolveLimitFact_UnknownOrEmptyModels_ReturnsUnverifiedFact(string slug, long? expectedMax, bool expectedKnown)
    {
        var fact = _resolver.ResolveLimitFact(slug);

        Assert.Equal(expectedKnown, fact.IsKnown);
        Assert.Equal(expectedMax, fact.DocumentedMaxContext);
    }

    [Fact]
    public void ResolveContextFact_ComputesEffectiveUsableContextAccurately()
    {
        // Grok-4.6: requested 1M exceeds documented 500k -> clamped to 500k
        var fact46 = _resolver.ResolveContextFact("grok-4.6", requestedContext: 1_000_000);
        Assert.Equal(500_000, fact46.DocumentedModelMax);
        Assert.Equal(1_000_000, fact46.RequestedContext);
        Assert.Equal(500_000, fact46.EffectiveUsableContext);

        // Grok-4.20: requested 1M matches documented 1M -> 1M
        var fact420 = _resolver.ResolveContextFact("grok-4.20", requestedContext: 1_000_000);
        Assert.Equal(1_000_000, fact420.DocumentedModelMax);
        Assert.Equal(1_000_000, fact420.EffectiveUsableContext);

        // Unknown model: requested 2M -> allows 2M
        var factUnknown = _resolver.ResolveContextFact("unlisted-custom-model", requestedContext: 2_000_000);
        Assert.Null(factUnknown.DocumentedModelMax);
        Assert.False(factUnknown.IsKnown);
        Assert.Equal(2_000_000, factUnknown.EffectiveUsableContext);
    }

    [Fact]
    public void CapabilityOwnership_DistinguishesModelFromRoutePassthrough()
    {
        var modelCaps = _resolver.ResolveCapabilities("grok-4.6", "codex-cli 0.155.0-alpha.9.2");
        Assert.True(modelCaps.Reasoning.IsSupported);
        Assert.True(modelCaps.Vision.IsSupported);
        Assert.True(modelCaps.ToolCalling.IsSupported);

        // Route capability for Modelflare with grok-4.6:
        // Model supports tool calling and reasoning, but route HostedWebSearch FAILED
        var routeCaps = CodexSwitcher.Core.Providers.Models.RouteCapabilities.ForModelflareGrok46("https://api.modelflare.test/v1", "codex-cli 0.155.0-alpha.9.2");
        Assert.True(routeCaps.Responses.IsSupported);
        Assert.True(routeCaps.ToolCallingPassthrough.IsSupported);

        // Explicit check: route HostedWebSearch failed even though model is grok-4.6
        Assert.Equal(CodexSwitcher.Core.Providers.Models.CapabilityEvidenceState.ProbeFailed, routeCaps.HostedWebSearch.State);
        Assert.False(routeCaps.HostedWebSearch.IsSupported);
    }
}

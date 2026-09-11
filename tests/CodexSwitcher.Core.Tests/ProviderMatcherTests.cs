using CodexSwitcher.Core.Catalog;
using Xunit;

namespace CodexSwitcher.Core.Tests;

public sealed class ProviderMatcherTests
{
    private static ProviderDescriptor CreateRouterCheapDescriptor()
    {
        return new ProviderDescriptor
        {
            Id = "router-cheap",
            DisplayName = "Router.Cheap",
            Match = new ProviderMatchCriteria
            {
                ExactHosts = new List<string> { "router.cheap", "direct.router-cheap.com" },
                ExactBaseUrls = new List<string> { "https://router.cheap/v1", "https://direct.router-cheap.com/v1" }
            },
            TrustedHosts = new List<string> { "router.cheap", "direct.router-cheap.com" },
            Routes = new List<ProviderRoute>
            {
                new() { Id = "primary", BaseUrl = "https://router.cheap/v1", DisplayName = "Primary" },
                new() { Id = "reserve", BaseUrl = "https://direct.router-cheap.com/v1", DisplayName = "Reserve" }
            }
        };
    }

    [Theory]
    [InlineData("https://router.cheap/v1")]
    [InlineData("https://router.cheap/v1/")]
    [InlineData("https://router.cheap")]
    [InlineData("router.cheap")]
    [InlineData("ROUTER.CHEAP")]
    [InlineData("https://ROUTER.CHEAP/v1")]
    [InlineData("https://direct.router-cheap.com/v1")]
    [InlineData("direct.router-cheap.com")]
    public void Matches_ValidExactHostsAndRoutes_ReturnsTrue(string input)
    {
        var descriptor = CreateRouterCheapDescriptor();
        Assert.True(ProviderMatcher.Matches(descriptor, input), $"Should match: {input}");
    }

    [Theory]
    [InlineData("https://router.cheap.attacker.example/v1")]
    [InlineData("https://attacker-router.cheap/v1")]
    [InlineData("https://notrouter.cheap/v1")]
    [InlineData("https://router.cheap.com/v1")]
    [InlineData("https://direct-router-cheap.com/v1")]
    [InlineData("https://fake.direct.router-cheap.com/v1")]
    [InlineData("https://google.com")]
    public void Matches_SpoofedOrSuffixHosts_ReturnsFalse(string input)
    {
        var descriptor = CreateRouterCheapDescriptor();
        Assert.False(ProviderMatcher.Matches(descriptor, input), $"Should NOT match spoofed/suffix: {input}");
    }

    [Fact]
    public void IsHostTrusted_ExactMatch_ReturnsTrue()
    {
        var descriptor = CreateRouterCheapDescriptor();
        var primaryUri = new Uri("https://router.cheap/v1/models");
        var reserveUri = new Uri("https://direct.router-cheap.com/v1/models");

        Assert.True(ProviderMatcher.IsHostTrusted(descriptor, primaryUri));
        Assert.True(ProviderMatcher.IsHostTrusted(descriptor, reserveUri));
    }

    [Fact]
    public void IsHostTrusted_SubdomainOrAttackerDomain_ReturnsFalse()
    {
        var descriptor = CreateRouterCheapDescriptor();
        var attackerUri1 = new Uri("https://router.cheap.evil.com/v1/models");
        var attackerUri2 = new Uri("https://sub.router.cheap/v1/models");
        var foreignUri = new Uri("https://api.openai.com/v1/models");

        Assert.False(ProviderMatcher.IsHostTrusted(descriptor, attackerUri1));
        Assert.False(ProviderMatcher.IsHostTrusted(descriptor, attackerUri2));
        Assert.False(ProviderMatcher.IsHostTrusted(descriptor, foreignUri));
    }

    [Fact]
    public void NormalizeHost_PunycodeAndIdn_HandledSafely()
    {
        var host = ProviderMatcher.NormalizeHost("https://xn--bcher-kva.example/v1");
        Assert.NotNull(host);
        Assert.Contains("xn--bcher-kva.example", host);
    }
}

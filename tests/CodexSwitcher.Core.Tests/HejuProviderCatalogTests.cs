using System.Net;
using System.Text;
using System.Text.Json;
using CodexSwitcher.Core.Tests.TestSupport;
using CodexSwitcher.Infra.Providers.Inspection;
using Xunit;

namespace CodexSwitcher.Core.Tests;

public sealed class HejuProviderCatalogTests
{
    private readonly PhysicalFileSystem _fileSystem = new();

    private ProviderDescriptor LoadDescriptor()
    {
        using var temp = new TempDir();
        var catalogPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "catalog", "providers.catalog.json");
        var result = new ProviderCatalogLoader(
            _fileSystem,
            catalogPath,
            Path.ChangeExtension(catalogPath, ".sig"),
            temp.Combine("none.previous.json"),
            temp.Combine("none.local.json"),
            "0.2.0").LoadCatalog();

        Assert.NotNull(result.Catalog);
        return Assert.Single(result.Catalog.Providers, provider => provider.Id == "hejuapi");
    }

    [Fact]
    public void SignedCatalog_RegistersQualifiedHejuDescriptorAndRoutes()
    {
        var descriptor = LoadDescriptor();

        Assert.Equal("HeJu API", descriptor.DisplayName);
        Assert.Equal("responses", descriptor.Codex.WireApi);
        Assert.Equal("bearer", descriptor.Codex.AuthStrategy);
        Assert.Null(descriptor.Codex.DefaultModel);
        Assert.Equal(CapabilityStatus.Supported, descriptor.Capabilities.Models.Status);
        Assert.Equal(CapabilityStatus.Unknown, descriptor.Capabilities.Balance.Status);
        Assert.Equal(CapabilityStatus.Supported, descriptor.Capabilities.Usage.Status);

        var global = Assert.Single(descriptor.Routes, route => route.Id == "global");
        Assert.Equal("Global", global.DisplayName);
        Assert.Equal("Global", global.Region);
        Assert.Equal("https://www.hejuapi.com/v1", global.BaseUrl);
        Assert.True(global.IsDefault);
        Assert.False(global.IsReserve);

        var hongKong = Assert.Single(descriptor.Routes, route => route.Id == "hong-kong");
        Assert.Equal("Hong Kong", hongKong.DisplayName);
        Assert.Equal("Hong Kong", hongKong.Region);
        Assert.Equal("https://hk.hejuapi.com/v1", hongKong.BaseUrl);
        Assert.False(hongKong.IsDefault);
        Assert.False(hongKong.IsReserve);
    }

    [Theory]
    [InlineData("https://www.hejuapi.com/v1")]
    [InlineData("www.hejuapi.com")]
    [InlineData("https://hk.hejuapi.com/v1")]
    [InlineData("hk.hejuapi.com")]
    [InlineData("https://hejuapi.com/")]
    public void ProviderMatcher_ResolvesOnlyProvenExactHejuHosts(string input)
    {
        Assert.True(ProviderMatcher.Matches(LoadDescriptor(), input));
    }

    [Theory]
    [InlineData("https://www.hejuapi.com.attacker.example/v1")]
    [InlineData("https://hk.hejuapi.com.attacker.example/v1")]
    [InlineData("https://hejuapi.com.attacker.example/v1")]
    [InlineData("https://subdomain.hejuapi.com/v1")]
    public void ProviderMatcher_RejectsHejuSuffixAndUnregisteredSubdomains(string input)
    {
        Assert.False(ProviderMatcher.Matches(LoadDescriptor(), input));
    }

    [Fact]
    public void ProbePlanner_PreservesRoutesAndNeverDuplicatesV1()
    {
        var descriptor = LoadDescriptor();
        var planner = new ProviderProbePlanner();

        var global = planner.PlanProbe(descriptor, descriptor.Capabilities.Models, "https://www.hejuapi.com/v1", "models");
        var hongKong = planner.PlanProbe(descriptor, descriptor.Capabilities.Models, "https://hk.hejuapi.com/v1", "models");

        Assert.True(global.Valid);
        Assert.True(hongKong.Valid);
        Assert.Equal("https://www.hejuapi.com/v1/models", global.Plan!.TargetUri.ToString());
        Assert.Equal("https://hk.hejuapi.com/v1/models", hongKong.Plan!.TargetUri.ToString());
        Assert.DoesNotContain("/v1/v1/", global.Plan.TargetUri.AbsolutePath, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/v1/v1/", hongKong.Plan.TargetUri.AbsolutePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UsageRecipe_MapsUpstreamHundredthUnitsWithoutClaimingCurrencyOrBalance()
    {
        var descriptor = LoadDescriptor();
        using var document = JsonDocument.Parse("{ \"object\": \"list\", \"total_usage\": 12345 }");
        var response = new ProbeHttpResponse(true, document.RootElement.Clone(), 200, null);
        decimal? balance = null;
        decimal? used = null;
        decimal? limit = null;
        decimal? remaining = null;
        string? currency = null;

        new ProviderProbeResponseMapper().MapBalanceAndUsageResponse(
            response,
            descriptor.Capabilities.Usage.Response,
            ref balance,
            ref used,
            ref limit,
            ref remaining,
            ref currency);

        Assert.Equal(123.45m, used);
        Assert.Null(balance);
        Assert.Null(limit);
        Assert.Null(remaining);
        Assert.Null(currency);
    }

    [Theory]
    [InlineData("https://www.hejuapi.com.attacker.example/capture")]
    [InlineData("https://hk.hejuapi.com.attacker.example/capture")]
    public async Task SafeTransport_BlocksHejuCredentialRedirectsToAttackerSuffixes(string redirectUrl)
    {
        var handler = new RedirectHandler(redirectUrl);
        using var transport = new SafeProviderHttpTransport(new HttpClient(handler));
        var descriptor = LoadDescriptor();
        var plan = new ProviderProbePlanner().PlanProbe(
            descriptor,
            descriptor.Capabilities.Models,
            "https://www.hejuapi.com/v1",
            "models");

        var response = await transport.SendProbeAsync(plan.Plan!, descriptor, "synthetic-heju-secret");

        Assert.False(response.Success);
        Assert.Equal(HttpStatusCode.Redirect, (HttpStatusCode)response.StatusCode);
        Assert.Contains("untrusted host", response.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal("www.hejuapi.com", handler.OnlyRequestedHost);
        Assert.DoesNotContain("synthetic-heju-secret", response.Error, StringComparison.Ordinal);
    }

    private sealed class RedirectHandler(string redirectUrl) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public string? OnlyRequestedHost { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            OnlyRequestedHost = request.RequestUri?.Host;
            var response = new HttpResponseMessage(HttpStatusCode.Redirect)
            {
                Content = new StringContent(string.Empty, Encoding.UTF8, "application/json")
            };
            response.Headers.Location = new Uri(redirectUrl);
            return Task.FromResult(response);
        }
    }
}

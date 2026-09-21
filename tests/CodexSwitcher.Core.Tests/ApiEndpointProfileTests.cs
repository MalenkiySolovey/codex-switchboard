using System;
using System.Collections.Generic;
using CodexSwitcher.Core.Providers.Models;
using Xunit;

namespace CodexSwitcher.Core.Tests;

public sealed class ApiEndpointProfileTests
{
    [Fact]
    public void FromProviderProfiles_GroupsProfilesByEndpointId()
    {
        var endpointId = Guid.NewGuid();
        var profile1Id = Guid.NewGuid();
        var profile2Id = Guid.NewGuid();

        var profiles = new List<ApiProviderProfile>
        {
            new()
            {
                Id = profile1Id,
                EndpointId = endpointId,
                ProviderPresetId = "deepseek",
                Nickname = "DeepSeek Endpoint",
                BaseUrl = "https://api.deepseek.com/v1",
                RoutePoolLabel = "HK-VIP",
                SelectedRouteId = "primary",
                SelectedModel = "deepseek-chat",
                DiscoveredModels = new List<string> { "deepseek-chat", "deepseek-reasoner" },
                CompatibilityLevel = CodexCompatibilityLevel.CodexCompatible,
                SortOrder = 1
            },
            new()
            {
                Id = profile2Id,
                EndpointId = endpointId,
                ProviderPresetId = "deepseek",
                Nickname = "DeepSeek Endpoint",
                BaseUrl = "https://api.deepseek.com/v1",
                RoutePoolLabel = "HK-VIP",
                SelectedRouteId = "primary",
                SelectedModel = "deepseek-reasoner",
                DiscoveredModels = new List<string> { "deepseek-chat", "deepseek-reasoner" },
                CompatibilityLevel = CodexCompatibilityLevel.CodexCompatible,
                SortOrder = 2
            }
        };

        var endpoints = ApiEndpointProfile.FromProviderProfiles(profiles);

        Assert.Single(endpoints);
        var ep = endpoints[0];
        Assert.Equal(endpointId, ep.EndpointId);
        Assert.Equal("deepseek", ep.ProviderPresetId);
        Assert.Equal("DeepSeek Endpoint", ep.DisplayName);
        Assert.Equal("https://api.deepseek.com/v1", ep.BaseUrl);
        Assert.Equal("HK-VIP", ep.RoutePoolLabel);
        Assert.Equal(CodexCompatibilityLevel.CodexCompatible, ep.CompatibilityLevel);
        Assert.Equal(2, ep.DiscoveredModels.Count);

        Assert.Equal(2, ep.Models.Count);
        Assert.Equal("deepseek-chat", ep.Models[0].ModelId);
        Assert.Equal(profile1Id, ep.Models[0].ProfileId);
        Assert.Equal("deepseek-reasoner", ep.Models[1].ModelId);
        Assert.Equal(profile2Id, ep.Models[1].ProfileId);
    }

    [Fact]
    public void ToProviderProfile_MergesEndpointAndModelConfig()
    {
        var endpointId = Guid.NewGuid();
        var profileId = Guid.NewGuid();

        var endpoint = new ApiEndpointProfile
        {
            EndpointId = endpointId,
            ProviderPresetId = "xai",
            DisplayName = "xAI Official",
            BaseUrl = "https://api.x.ai/v1",
            RoutePoolLabel = "direct-us",
            DiscoveredModels = new List<string> { "grok-4.6", "grok-beta" },
            CompatibilityLevel = CodexCompatibilityLevel.CodexCompatible
        };

        var modelConfig = new ApiEndpointModelConfig
        {
            ProfileId = profileId,
            ModelId = "grok-4.6",
            DisplayName = "Grok 4.6 Fast",
            StableCodexProviderId = "xai-grok",
            SortOrder = 5,
            CompatibilityLevel = CodexCompatibilityLevel.CodexCompatible
        };

        var profile = endpoint.ToProviderProfile(modelConfig);

        Assert.Equal(profileId, profile.Id);
        Assert.Equal(endpointId, profile.EndpointId);
        Assert.Equal("xai", profile.ProviderPresetId);
        Assert.Equal("Grok 4.6 Fast", profile.Nickname);
        Assert.Equal("https://api.x.ai/v1", profile.BaseUrl);
        Assert.Equal("direct-us", profile.RoutePoolLabel);
        Assert.Equal("grok-4.6", profile.SelectedModel);
        Assert.Equal("xai-grok", profile.StableCodexProviderId);
        Assert.Equal(5, profile.SortOrder);
        Assert.Equal(CodexCompatibilityLevel.CodexCompatible, profile.CompatibilityLevel);
        Assert.NotNull(profile.DiscoveredModels);
        Assert.Equal(2, profile.DiscoveredModels.Count);
    }


    [Fact]
    public void FromProviderProfiles_WhenEndpointIdNull_UsesProfileIdAsGroupKey()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();

        var profiles = new List<ApiProviderProfile>
        {
            new() { Id = id1, EndpointId = null, Nickname = "P1", BaseUrl = "https://p1.com" },
            new() { Id = id2, EndpointId = null, Nickname = "P2", BaseUrl = "https://p2.com" }
        };

        var endpoints = ApiEndpointProfile.FromProviderProfiles(profiles);

        Assert.Equal(2, endpoints.Count);
        Assert.Contains(endpoints, e => e.EndpointId == id1 && e.DisplayName == "P1");
        Assert.Contains(endpoints, e => e.EndpointId == id2 && e.DisplayName == "P2");
    }
}

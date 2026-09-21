using System;
using System.Collections.Generic;
using System.Linq;

namespace CodexSwitcher.Core.Providers.Models;

/// <summary>
/// Domain model separating the API account/endpoint tier from individual model configurations.
/// Enables discovering an endpoint once and attaching multiple Codex model configurations without
/// re-entering the Base URL, API key, or transport settings.
/// </summary>
public sealed class ApiEndpointProfile
{
    public Guid EndpointId { get; init; } = Guid.NewGuid();

    /// <summary>Optional provider preset ID (e.g. "deepseek", "xai", "openrouter", "modelflare", "hejuapi", "router-cheap").</summary>
    public string? ProviderPresetId { get; set; }

    /// <summary>User-visible endpoint nickname or service name.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Canonical base URL of the service endpoint (e.g. "https://api.deepseek.com/v1").</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Optional route or upstream pool label (e.g. "grok-award 0.01x", "HK-direct").</summary>
    public string? RoutePoolLabel { get; set; }

    /// <summary>Catalog route ID if derived from a signed catalog provider.</summary>
    public string? CatalogRouteId { get; set; }

    /// <summary>Catalog provider ID if derived from a signed catalog provider.</summary>
    public string? CatalogProviderId { get; set; }

    /// <summary>Network and transport overrides shared by all models under this endpoint.</summary>
    public ApiProviderTransportOverrides? TransportOverrides { get; set; }

    /// <summary>Cached list of model IDs discovered via the provider's /models endpoint.</summary>
    public List<string> DiscoveredModels { get; set; } = [];

    /// <summary>Individual Codex model configurations mapped to this endpoint.</summary>
    public List<ApiEndpointModelConfig> Models { get; set; } = [];

    /// <summary>Latest overall Codex compatibility qualification level for this endpoint.</summary>
    public CodexCompatibilityLevel CompatibilityLevel { get; set; } = CodexCompatibilityLevel.Unknown;

    /// <summary>Timestamp of last qualification probe.</summary>
    public DateTimeOffset? LastProbedAt { get; set; }

    /// <summary>Creates a standalone ApiProviderProfile from this endpoint and a given model config.</summary>
    public ApiProviderProfile ToProviderProfile(ApiEndpointModelConfig modelConfig)
    {
        ArgumentNullException.ThrowIfNull(modelConfig);

        return new ApiProviderProfile
        {
            Id = modelConfig.ProfileId,
            EndpointId = EndpointId,
            ProviderPresetId = ProviderPresetId,
            CatalogProviderId = CatalogProviderId,
            StableCodexProviderId = modelConfig.StableCodexProviderId,
            Nickname = !string.IsNullOrWhiteSpace(modelConfig.DisplayName) ? modelConfig.DisplayName : DisplayName,
            BaseUrl = BaseUrl,
            SelectedRouteId = CatalogRouteId,
            RoutePoolLabel = RoutePoolLabel,
            SelectedModel = modelConfig.ModelId,
            WireApi = "responses",
            KeyPreview = modelConfig.KeyPreview,
            Status = modelConfig.Status,
            CreatedAt = modelConfig.CreatedAt,
            LastSwitchedAt = modelConfig.LastSwitchedAt,
            SortOrder = modelConfig.SortOrder,
            ModelOverrides = modelConfig.ModelOverrides,
            TransportOverrides = TransportOverrides,
            DiscoveredModels = new List<string>(DiscoveredModels),
            CompatibilityLevel = modelConfig.CompatibilityLevel != CodexCompatibilityLevel.Unknown ? modelConfig.CompatibilityLevel : CompatibilityLevel,
        };
    }

    /// <summary>Groups a list of flat ApiProviderProfiles into cohesive ApiEndpointProfiles.</summary>
    public static List<ApiEndpointProfile> FromProviderProfiles(IEnumerable<ApiProviderProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);

        var groups = profiles.GroupBy(p => p.EndpointId ?? p.Id);
        var endpoints = new List<ApiEndpointProfile>();

        foreach (var group in groups)
        {
            var first = group.First();
            var endpoint = new ApiEndpointProfile
            {
                EndpointId = group.Key,
                ProviderPresetId = first.ProviderPresetId,
                DisplayName = first.Nickname,
                BaseUrl = first.BaseUrl,
                RoutePoolLabel = first.RoutePoolLabel,
                CatalogRouteId = first.SelectedRouteId,
                CatalogProviderId = first.CatalogProviderId,
                TransportOverrides = first.TransportOverrides,
                DiscoveredModels = first.DiscoveredModels != null ? new List<string>(first.DiscoveredModels) : [],
                CompatibilityLevel = first.CompatibilityLevel,
                Models = group.Select(p => new ApiEndpointModelConfig
                {
                    ProfileId = p.Id,
                    StableCodexProviderId = p.StableCodexProviderId,
                    ModelId = p.SelectedModel ?? string.Empty,
                    DisplayName = p.Nickname,
                    KeyPreview = p.KeyPreview,
                    Status = p.Status,
                    CreatedAt = p.CreatedAt,
                    LastSwitchedAt = p.LastSwitchedAt,
                    SortOrder = p.SortOrder,
                    ModelOverrides = p.ModelOverrides,
                    CompatibilityLevel = p.CompatibilityLevel,
                }).ToList()
            };
            endpoints.Add(endpoint);
        }

        return endpoints;
    }
}

/// <summary>
/// Model configuration attached to an ApiEndpointProfile.
/// </summary>
public sealed class ApiEndpointModelConfig
{
    public Guid ProfileId { get; init; } = Guid.NewGuid();
    public string StableCodexProviderId { get; init; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string KeyPreview { get; set; } = string.Empty;
    public ApiProviderProfileStatus Status { get; set; } = ApiProviderProfileStatus.Active;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastSwitchedAt { get; set; }
    public int SortOrder { get; set; }
    public CodexModelOverrides? ModelOverrides { get; set; }
    public CodexCompatibilityLevel CompatibilityLevel { get; set; } = CodexCompatibilityLevel.Unknown;
}

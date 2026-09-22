using CodexSwitcher.Core.Accounts.Models;
using CodexSwitcher.Core.Common.Enums;
using CodexSwitcher.Core.Common.Environment;
using CodexSwitcher.Core.Common.Errors;
using CodexSwitcher.Core.Providers.Contracts;
using CodexSwitcher.Core.Providers.Models;
using CodexSwitcher.Core.Routing.Contracts;
using CodexSwitcher.Core.Routing.Models;
using CodexSwitcher.Core.Security.Secrets;

namespace CodexSwitcher.Core.Routing.Services;

/// <summary>
/// Deterministic builder for validated <see cref="CodexSwitchPlan"/> instances.
/// Validates prerequisite profile metadata, encrypted credential presence, KeyBroker binary readiness,
/// and routing state. Never places raw credentials into the resulting plan.
/// </summary>
public sealed class SwitchPlanBuilder : ISwitchPlanBuilder
{
    private readonly IApiProviderStore _apiProviderStore;
    private readonly IApiKeySecretStore _secretStore;
    private readonly IKeyBrokerInstaller _brokerInstaller;
    private readonly ICodexRoutingConfigStore _routingConfig;
    private readonly CodexPaths _paths;

    public SwitchPlanBuilder(
        IApiProviderStore apiProviderStore,
        IApiKeySecretStore secretStore,
        IKeyBrokerInstaller brokerInstaller,
        ICodexRoutingConfigStore routingConfig,
        CodexPaths paths)
    {
        _apiProviderStore = apiProviderStore ?? throw new ArgumentNullException(nameof(apiProviderStore));
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        _brokerInstaller = brokerInstaller ?? throw new ArgumentNullException(nameof(brokerInstaller));
        _routingConfig = routingConfig ?? throw new ArgumentNullException(nameof(routingConfig));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    public CodexSwitchPlan BuildApiProviderPlan(Guid apiProfileId, SwitchExecutionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var targetProfile = _apiProviderStore.GetById(apiProfileId);
        if (targetProfile is null)
        {
            return new InvalidSwitchPlan(new ActiveTarget.Unknown(), ErrorCategory.Unknown, $"API provider profile {apiProfileId} not found.");
        }

        var secretOwnerId = targetProfile.EndpointId ?? targetProfile.Id;
        if (!_secretStore.HasApiKey(secretOwnerId))
        {
            targetProfile.Status = ApiProviderProfileStatus.CredentialMissing;
            _apiProviderStore.Save(targetProfile);
            return new InvalidSwitchPlan(
                new ActiveTarget.Api(targetProfile),
                ErrorCategory.DecryptionFailed,
                $"Encrypted API key for '{targetProfile.DisplayName}' is missing. Re-enter the API key.");
        }

        string brokerPath;
        try
        {
            brokerPath = _brokerInstaller.EnsureInstalled();
        }
        catch (Exception ex)
        {
            return new InvalidSwitchPlan(
                new ActiveTarget.Api(targetProfile),
                ErrorCategory.Unknown,
                $"Failed to install or verify KeyBroker executable: {ex.Message}");
        }

        var transport = targetProfile.TransportOverrides;
        var responsesPolicy = transport?.ResponsesPolicy ?? ResponsesCompatibilityPolicy.Auto;
        var routeCaps = DeriveRouteCapabilities(targetProfile);
        var toolPolicy = EffectiveToolPolicy.Resolve(responsesPolicy, routeCaps);

        return new ApiProviderSwitchPlan(targetProfile, brokerPath, options, toolPolicy);
    }

    private static RouteCapabilities DeriveRouteCapabilities(ApiProviderProfile targetProfile)
    {
        if (targetProfile.LastProbeReport is { } report)
        {
            return new RouteCapabilities(
                routeId: targetProfile.SelectedRouteId ?? "active",
                baseUrl: targetProfile.BaseUrl,
                responses: report.ResponsesEndpoint,
                streaming: report.StreamingSupport,
                webSockets: CapabilityEvidence.Unknown("WebSocket route not probed"),
                hostedWebSearch: report.HostedSearchSupport,
                standardFunctionTools: report.BuiltInFunctionTools,
                visionPassthrough: report.Vision,
                customFreeformTools: report.CustomApplyPatch,
                applyPatchFreeform: report.CustomApplyPatch,
                toolSearch: report.ToolSearch,
                standaloneWebSearch: report.StandaloneSearch,
                namespaceTools: report.McpNamespaceTools,
                promptCaching: CapabilityEvidence.Unknown("Prompt caching unverified"),
                mcp: CapabilityEvidence.Unknown("MCP unverified"),
                appsPlugins: report.Plugins);
        }

        if (targetProfile.BaseUrl.Contains("modelflare", StringComparison.OrdinalIgnoreCase))
        {
            return RouteCapabilities.ForModelflareGrok46(targetProfile.BaseUrl);
        }

        return RouteCapabilities.ForGenericResponses(targetProfile.BaseUrl);
    }

    public CodexSwitchPlan BuildChatGptPlan(Guid? targetProfileId, List<ProfileMetadata> allChatGptProfiles, SwitchExecutionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(allChatGptProfiles);

        var currentActive = allChatGptProfiles.FirstOrDefault(p => p.IsActive);
        var targetProfile = targetProfileId.HasValue
            ? allChatGptProfiles.FirstOrDefault(p => p.Id == targetProfileId.Value)
            : currentActive;

        if (targetProfileId.HasValue && targetProfile is null)
        {
            return new InvalidSwitchPlan(new ActiveTarget.Unknown(), ErrorCategory.Unknown, $"ChatGPT profile {targetProfileId.Value} not found.");
        }

        var routing = _routingConfig.ReadRoutingState(_paths.ConfigTomlPath);
        var isApiRoutingActive = !string.IsNullOrWhiteSpace(routing.ModelProvider) &&
                                 !string.Equals(routing.ModelProvider, "openai", StringComparison.OrdinalIgnoreCase);

        var isAccountSwitch = targetProfile is not null && (currentActive is null || currentActive.Id != targetProfile.Id);

        if (isAccountSwitch)
        {
            return new ChatGptAccountSwitchPlan(targetProfile!, allChatGptProfiles, isApiRoutingActive, options);
        }

        if (isApiRoutingActive)
        {
            return new ChatGptReturnRoutingSwitchPlan(targetProfile, options);
        }

        return new ChatGptNoOpSwitchPlan(targetProfile);
    }

    public CodexSwitchPlan BuildApiRoutePlan(Guid apiProfileId, string routeId, string newBaseUrl, SwitchExecutionOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(routeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(newBaseUrl);
        ArgumentNullException.ThrowIfNull(options);

        var profile = _apiProviderStore.GetById(apiProfileId);
        if (profile is null)
        {
            return new InvalidSwitchPlan(new ActiveTarget.Unknown(), ErrorCategory.Unknown, $"API provider profile {apiProfileId} not found.");
        }

        var routing = _routingConfig.ReadRoutingState(_paths.ConfigTomlPath);
        var isCurrentlyActive = routing.SwitchboardProviders.ContainsKey(profile.StableCodexProviderId);

        return new ApiRouteSwitchPlan(profile, routeId, newBaseUrl, isCurrentlyActive, options);
    }
}

using CodexSwitcher.Core.Abstractions;
using CodexSwitcher.Core.Models;

namespace CodexSwitcher.Core.Services;

/// <summary>
/// Resolves active inference target from config.toml.
/// Invariant: Does not alter ProfileMetadata.IsActive, maintaining strict separation
/// between the ChatGPT credential slot and the active inference route.
/// </summary>
public sealed class CodexActiveTargetResolver : ICodexActiveTargetResolver
{
    private readonly ICodexRoutingConfigStore _routingConfig;
    private readonly IApiProviderStore _apiProviderStore;

    public CodexActiveTargetResolver(
        ICodexRoutingConfigStore routingConfig,
        IApiProviderStore apiProviderStore)
    {
        _routingConfig = routingConfig ?? throw new ArgumentNullException(nameof(routingConfig));
        _apiProviderStore = apiProviderStore ?? throw new ArgumentNullException(nameof(apiProviderStore));
    }

    public ActiveTarget ResolveActiveTarget(
        string configTomlPath,
        Guid? activeChatGptProfileId = null,
        string? activeChatGptEmail = null)
    {
        var routing = _routingConfig.ReadRoutingState(configTomlPath);
        var provider = routing.ModelProvider?.Trim();

        // If unset or empty or "openai", the active inference target is ChatGPT
        if (string.IsNullOrWhiteSpace(provider) || string.Equals(provider, "openai", StringComparison.OrdinalIgnoreCase))
        {
            return new ActiveTarget.ChatGpt(activeChatGptProfileId, activeChatGptEmail);
        }

        // Check if it is a Switchboard-owned API provider
        if (provider.StartsWith("switchboard_", StringComparison.OrdinalIgnoreCase))
        {
            var profile = _apiProviderStore.GetByStableCodexProviderId(provider);
            if (profile is not null)
            {
                return new ActiveTarget.Api(profile);
            }

            // Unknown or orphaned switchboard provider block
            var syntheticProfile = new ApiProviderProfile
            {
                StableCodexProviderId = provider,
                Nickname = provider,
                Status = ApiProviderProfileStatus.CredentialMissing
            };
            return new ActiveTarget.Api(syntheticProfile);
        }

        // User-owned external provider (e.g. "company_internal", "openrouter", etc.)
        return new ActiveTarget.External(provider);
    }
}

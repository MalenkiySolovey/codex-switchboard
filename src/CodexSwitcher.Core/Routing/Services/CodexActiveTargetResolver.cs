using CodexSwitcher.Core.Accounts.Contracts;
using CodexSwitcher.Core.Accounts.Formatting;
using CodexSwitcher.Core.Accounts.Services;
using CodexSwitcher.Core.Common.Dispatcher;
using CodexSwitcher.Core.Common.Enums;
using CodexSwitcher.Core.Common.Environment;
using CodexSwitcher.Core.Common.Errors;
using CodexSwitcher.Core.Common.Lifecycle;
using CodexSwitcher.Core.Common.Logging;
using CodexSwitcher.Core.Common.Storage;
using CodexSwitcher.Core.Common.Time;
using CodexSwitcher.Core.Providers.Catalog;
using CodexSwitcher.Core.Providers.Services;
using CodexSwitcher.Core.Routing.Services;
using CodexSwitcher.Core.Security.Secrets;
using CodexSwitcher.Core.Security.Totp;
using CodexSwitcher.Core.Security.Verification;
using CodexSwitcher.Core.Settings.Contracts;
using CodexSwitcher.Core.Settings.Models;
using CodexSwitcher.Core.Threads.Contracts;
using CodexSwitcher.Core.Threads.Models;
using CodexSwitcher.Core.Transfer.Contracts;
using CodexSwitcher.Core.Transfer.Models;
using CodexSwitcher.Core.Transfer.Services;
using CodexSwitcher.Core.Usage.Contracts;
using CodexSwitcher.Core.Usage.Formatting;
using CodexSwitcher.Core.Usage.Models;
using CodexSwitcher.Core.Usage.Services;
using CodexSwitcher.Core.Routing.Contracts;
using CodexSwitcher.Core.Routing.Models;
using CodexSwitcher.Core.Providers.Contracts;
using CodexSwitcher.Core.Providers.Models;
using CodexSwitcher.Core.Accounts.Models;

namespace CodexSwitcher.Core.Routing.Services;

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

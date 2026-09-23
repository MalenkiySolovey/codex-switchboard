using CodexSwitcher.Core.Accounts.Contracts;
using CodexSwitcher.Core.Accounts.Formatting;
using CodexSwitcher.Core.Accounts.Models;
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
using CodexSwitcher.Core.Routing.Contracts;
using CodexSwitcher.Core.Routing.Models;
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
using CodexSwitcher.Core.Providers.Contracts;
using CodexSwitcher.Core.Providers.Models;

namespace CodexSwitcher.Core.Providers.Services;

/// <summary>
/// Secure domain service for declarative AI provider inspection.
/// Enforces that stored plaintext credentials never escape into presentation view models.
/// </summary>
public sealed class ProviderInspectionService : IProviderInspectionService
{
    private readonly IApiProviderStore _apiProviderStore;
    private readonly IApiKeySecretStore _secretStore;
    private readonly IProviderCatalogService _catalogService;
    private readonly IDeclarativeProviderInspector _inspector;
    private readonly IProviderModelCache? _modelCache;

    public ProviderInspectionService(
        IApiProviderStore apiProviderStore,
        IApiKeySecretStore secretStore,
        IProviderCatalogService catalogService,
        IDeclarativeProviderInspector inspector,
        IProviderModelCache? modelCache = null)
    {
        _apiProviderStore = apiProviderStore ?? throw new ArgumentNullException(nameof(apiProviderStore));
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        _catalogService = catalogService ?? throw new ArgumentNullException(nameof(catalogService));
        _inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));
        _modelCache = modelCache;
    }

    /// <inheritdoc />
    public async Task<ApiProviderSnapshot> InspectAsync(Guid providerProfileId, CancellationToken cancellationToken = default)
    {
        var profile = _apiProviderStore.GetById(providerProfileId);
        if (profile == null)
        {
            return new ApiProviderSnapshot
            {
                ConnectionStatus = HealthStatus.Error,
                Error = "Provider profile not found.",
                LastCheckedAt = DateTimeOffset.UtcNow,
            };
        }

        // Endpoint-bound credentials are intentionally shared by model
        // profiles. Reading by profile ID caused card discovery to emit a
        // false 401 while the edit dialog used the correct endpoint ID.
        var secretOwnerId = profile.EndpointId ?? profile.Id;
        string? apiKey = _secretStore.GetApiKey(secretOwnerId);
        var descriptor = !string.IsNullOrWhiteSpace(profile.CatalogProviderId)
            ? _catalogService.GetDescriptor(profile.CatalogProviderId)
            : null;

        var routeKey = !string.IsNullOrWhiteSpace(profile.SelectedRouteId)
            ? profile.SelectedRouteId
            : profile.BaseUrl;

        ApiProviderSnapshot snapshot;
        try
        {
            if (descriptor != null)
            {
                snapshot = await _inspector.InspectAsync(descriptor, profile.BaseUrl, apiKey, cancellationToken);
            }
            else
            {
                snapshot = await _inspector.InspectGenericUnknownAsync(profile.BaseUrl, apiKey, cancellationToken);
            }

            if (snapshot.Models.Count > 0 && _modelCache != null)
            {
                _modelCache.SetModels(profile.Id, routeKey, 1, snapshot.Models);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            snapshot = new ApiProviderSnapshot
            {
                ConnectionStatus = HealthStatus.Error,
                Error = SanitizeMessage(ex.Message),
                LastCheckedAt = DateTimeOffset.UtcNow,
            };
        }

        return snapshot;
    }

    /// <inheritdoc />
    public async Task<ApiProviderSnapshot> DiscoverModelsAsync(Guid providerProfileId, CancellationToken cancellationToken = default)
    {
        var profile = _apiProviderStore.GetById(providerProfileId);
        if (profile is null)
        {
            return new ApiProviderSnapshot
            {
                ConnectionStatus = HealthStatus.Error,
                Error = "Provider profile not found.",
                LastCheckedAt = DateTimeOffset.UtcNow,
            };
        }

        var secretOwnerId = profile.EndpointId ?? profile.Id;
        var apiKey = _secretStore.GetApiKey(secretOwnerId);
        var descriptor = !string.IsNullOrWhiteSpace(profile.CatalogProviderId)
            ? _catalogService.GetDescriptor(profile.CatalogProviderId)
            : null;
        var routeKey = !string.IsNullOrWhiteSpace(profile.SelectedRouteId)
            ? profile.SelectedRouteId
            : profile.BaseUrl;

        try
        {
            ApiProviderSnapshot snapshot;
            if (descriptor is not null)
            {
                snapshot = await _inspector.DiscoverModelsAsync(
                    descriptor,
                    profile.BaseUrl,
                    apiKey,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // The generic inspector is itself restricted to GET /models
                // and never guesses balance, usage, or inference endpoints.
                snapshot = await _inspector.InspectGenericUnknownAsync(
                    profile.BaseUrl,
                    apiKey,
                    cancellationToken).ConfigureAwait(false);
            }

            if (snapshot.Models.Count > 0 && _modelCache is not null)
            {
                _modelCache.SetModels(profile.Id, routeKey, 1, snapshot.Models);
            }

            return snapshot;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ApiProviderSnapshot
            {
                ConnectionStatus = HealthStatus.Error,
                Error = SanitizeMessage(ex.Message),
                LastCheckedAt = DateTimeOffset.UtcNow,
            };
        }
    }

    private static string SanitizeMessage(string message)
    {
        if (string.IsNullOrEmpty(message)) return "Inspection failed.";
        if (message.Contains("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return "Inspection failed (credentials redacted).";
        }
        return message;
    }
}

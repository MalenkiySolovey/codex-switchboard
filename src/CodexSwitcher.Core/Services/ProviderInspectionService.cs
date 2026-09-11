using CodexSwitcher.Core.Abstractions;
using CodexSwitcher.Core.Catalog;
using CodexSwitcher.Core.Models;

namespace CodexSwitcher.Core.Services;

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

        string? apiKey = _secretStore.GetApiKey(providerProfileId);
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

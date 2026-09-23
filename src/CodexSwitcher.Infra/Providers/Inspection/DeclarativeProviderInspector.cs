using CodexSwitcher.Core.Common.Enums;
using CodexSwitcher.Core.Providers.Catalog;
using CodexSwitcher.Core.Providers.Contracts;
using CodexSwitcher.Core.Providers.Models;
using CodexSwitcher.Core.Routing.Models;

namespace CodexSwitcher.Infra.Providers.Inspection;

/// <summary>
/// Safe, non-Turing-complete declarative inspection facade for AI model providers.
/// Coordinates request planning (<see cref="IProviderProbePlanner"/>), hardened HTTP transport
/// (<see cref="ISafeProviderHttpTransport"/>), and fact extraction (<see cref="IProviderProbeResponseMapper"/>).
/// Strictly enforces GET/HEAD-only, exact trusted-host validation, redirect credential stripping,
/// response size capping, and normalized snapshot extraction.
/// </summary>
public sealed class DeclarativeProviderInspector : IDeclarativeProviderInspector, IDisposable
{
    private readonly IProviderProbePlanner _planner;
    private readonly ISafeProviderHttpTransport _transport;
    private readonly IProviderProbeResponseMapper _mapper;

    /// <summary>
    /// Decomposed constructor injecting dedicated planner, transport, and mapper.
    /// </summary>
    public DeclarativeProviderInspector(
        IProviderProbePlanner planner,
        ISafeProviderHttpTransport transport,
        IProviderProbeResponseMapper mapper)
    {
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
    }

    /// <summary>
    /// Backward-compatible constructor for existing tests and composition roots.
    /// </summary>
    public DeclarativeProviderInspector(HttpClient? httpClient = null)
        : this(
            new ProviderProbePlanner(),
            new SafeProviderHttpTransport(httpClient),
            new ProviderProbeResponseMapper())
    {
    }

    /// <summary>
    /// Inspects a provider using its catalog descriptor and returns a normalized ApiProviderSnapshot.
    /// </summary>
    public async Task<ApiProviderSnapshot> InspectAsync(
        ProviderDescriptor descriptor,
        string activeBaseUrl,
        string? apiKey,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (string.IsNullOrWhiteSpace(activeBaseUrl))
        {
            throw new ArgumentException("Active base URL cannot be empty.", nameof(activeBaseUrl));
        }

        var capabilities = new Dictionary<string, CapabilityStatus>(StringComparer.OrdinalIgnoreCase)
        {
            ["models"] = descriptor.Capabilities?.Models?.Status ?? CapabilityStatus.Unknown,
            ["balance"] = descriptor.Capabilities?.Balance?.Status ?? CapabilityStatus.Unknown,
            ["usage"] = descriptor.Capabilities?.Usage?.Status ?? CapabilityStatus.Unknown,
        };

        var modelsList = new List<string>();
        decimal? balance = null;
        string? currency = null;
        decimal? usedCredits = null;
        decimal? creditLimit = null;
        decimal? remainingCredits = null;
        string? overallError = null;
        var connectionStatus = HealthStatus.Unknown;

        // 1. Inspect Models if supported
        var modelsRecipe = descriptor.Capabilities?.Models;
        if (modelsRecipe != null && modelsRecipe.Status == CapabilityStatus.Supported)
        {
            var (valid, plan, planError) = _planner.PlanProbe(descriptor, modelsRecipe, activeBaseUrl, "models");
            if (!valid || plan == null)
            {
                connectionStatus = HealthStatus.Error;
                overallError = planError;
            }
            else
            {
                var response = await _transport.SendProbeAsync(plan, descriptor, apiKey, ct).ConfigureAwait(false);
                var (success, models, mapError) = _mapper.MapModelsResponse(response, plan.ModelsPointer);
                if (success)
                {
                    connectionStatus = HealthStatus.Valid;
                    if (models != null)
                    {
                        modelsList.AddRange(models);
                    }
                }
                else
                {
                    connectionStatus = HealthStatus.Error;
                    overallError = mapError ?? response.Error;
                }
            }
        }

        // 2. Inspect Balance if supported
        var balanceRecipe = descriptor.Capabilities?.Balance;
        if (balanceRecipe != null && balanceRecipe.Status == CapabilityStatus.Supported)
        {
            var (valid, plan, planError) = _planner.PlanProbe(descriptor, balanceRecipe, activeBaseUrl, "balance");
            if (valid && plan != null)
            {
                var response = await _transport.SendProbeAsync(plan, descriptor, apiKey, ct).ConfigureAwait(false);
                if (response.Success && response.Json != null)
                {
                    if (connectionStatus == HealthStatus.Unknown)
                    {
                        connectionStatus = HealthStatus.Valid;
                    }

                    _mapper.MapBalanceAndUsageResponse(
                        response,
                        balanceRecipe.Response,
                        ref balance,
                        ref usedCredits,
                        ref creditLimit,
                        ref remainingCredits,
                        ref currency);
                }
                else if (response.Error != null)
                {
                    overallError ??= response.Error;
                }
            }
            else if (planError != null)
            {
                overallError ??= planError;
            }
        }

        // 3. Inspect Usage if supported and separate from balance
        var usageRecipe = descriptor.Capabilities?.Usage;
        if (usageRecipe != null && usageRecipe.Status == CapabilityStatus.Supported && usageRecipe != balanceRecipe)
        {
            var (valid, plan, _) = _planner.PlanProbe(descriptor, usageRecipe, activeBaseUrl, "usage");
            if (valid && plan != null)
            {
                var response = await _transport.SendProbeAsync(plan, descriptor, apiKey, ct).ConfigureAwait(false);
                if (response.Success && response.Json != null)
                {
                    _mapper.MapBalanceAndUsageResponse(
                        response,
                        usageRecipe.Response,
                        ref balance,
                        ref usedCredits,
                        ref creditLimit,
                        ref remainingCredits,
                        ref currency);
                }
            }
        }

        if (connectionStatus == HealthStatus.Unknown)
        {
            connectionStatus = overallError == null ? HealthStatus.Valid : HealthStatus.Unknown;
        }

        return new ApiProviderSnapshot
        {
            ConnectionStatus = connectionStatus,
            Models = modelsList,
            Balance = balance,
            Currency = currency,
            UsedCredits = usedCredits,
            RemainingCredits = remainingCredits,
            CreditLimit = creditLimit,
            Usage = usedCredits,
            Capabilities = capabilities,
            LastCheckedAt = DateTimeOffset.UtcNow,
            Error = overallError,
        };
    }

    /// <inheritdoc />
    public async Task<ApiProviderSnapshot> DiscoverModelsAsync(
        ProviderDescriptor descriptor,
        string activeBaseUrl,
        string? apiKey,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (string.IsNullOrWhiteSpace(activeBaseUrl))
        {
            throw new ArgumentException("Active base URL cannot be empty.", nameof(activeBaseUrl));
        }

        var modelsRecipe = descriptor.Capabilities?.Models;
        if (modelsRecipe is null || modelsRecipe.Status != CapabilityStatus.Supported)
        {
            return new ApiProviderSnapshot
            {
                ConnectionStatus = HealthStatus.Unknown,
                Capabilities = new Dictionary<string, CapabilityStatus>(StringComparer.OrdinalIgnoreCase)
                {
                    ["models"] = modelsRecipe?.Status ?? CapabilityStatus.Unknown,
                },
                Error = "This provider does not declare a supported GET /models strategy.",
                LastCheckedAt = DateTimeOffset.UtcNow,
            };
        }

        var (valid, plan, planError) = _planner.PlanProbe(descriptor, modelsRecipe, activeBaseUrl, "models");
        if (!valid || plan is null)
        {
            return new ApiProviderSnapshot
            {
                ConnectionStatus = HealthStatus.Error,
                Capabilities = new Dictionary<string, CapabilityStatus>(StringComparer.OrdinalIgnoreCase)
                {
                    ["models"] = modelsRecipe.Status,
                },
                Error = planError,
                LastCheckedAt = DateTimeOffset.UtcNow,
            };
        }

        var response = await _transport.SendProbeAsync(plan, descriptor, apiKey, ct).ConfigureAwait(false);
        var (success, models, mapError) = _mapper.MapModelsResponse(response, plan.ModelsPointer);
        return new ApiProviderSnapshot
        {
            ConnectionStatus = success ? HealthStatus.Valid : HealthStatus.Error,
            Models = models ?? [],
            Capabilities = new Dictionary<string, CapabilityStatus>(StringComparer.OrdinalIgnoreCase)
            {
                ["models"] = success ? CapabilityStatus.Supported : CapabilityStatus.Unknown,
            },
            Error = mapError ?? response.Error,
            LastCheckedAt = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>
    /// Inspects an unknown generic OpenAI-compatible provider using safe default endpoints.
    /// strictly never guesses balance/credits paths.
    /// </summary>
    public async Task<ApiProviderSnapshot> InspectGenericUnknownAsync(
        string rawBaseUrl,
        string? apiKey,
        CancellationToken ct = default)
    {
        var (valid, plan, syntheticDescriptor, planError) = _planner.PlanGenericUnknownModelsProbe(rawBaseUrl);
        if (!valid || plan == null || syntheticDescriptor == null)
        {
            return new ApiProviderSnapshot
            {
                ConnectionStatus = HealthStatus.Unknown,
                Error = planError ?? "Invalid base URL format.",
                LastCheckedAt = DateTimeOffset.UtcNow,
            };
        }

        var response = await _transport.SendProbeAsync(plan, syntheticDescriptor, apiKey, ct).ConfigureAwait(false);
        var (success, models, mapError) = _mapper.MapModelsResponse(response, plan.ModelsPointer);

        var capabilities = new Dictionary<string, CapabilityStatus>(StringComparer.OrdinalIgnoreCase)
        {
            ["models"] = success ? CapabilityStatus.Supported : CapabilityStatus.Unknown,
            ["balance"] = CapabilityStatus.Unknown,
            ["usage"] = CapabilityStatus.Unknown,
        };

        return new ApiProviderSnapshot
        {
            ConnectionStatus = success ? HealthStatus.Valid : HealthStatus.Unknown,
            Models = models ?? [],
            Balance = null,
            Currency = null,
            Capabilities = capabilities,
            LastCheckedAt = DateTimeOffset.UtcNow,
            Error = mapError ?? response.Error,
        };
    }

    /// <summary>
    /// Safely joins a route base URL and a request path without duplication (/v1 + /models != /v1/v1/models).
    /// Enforces OpenAI-compatible path normalization.
    /// </summary>
    public static string JoinBaseUrlAndPath(string baseUrl, string? path, string? strategy = null)
    {
        return new ProviderProbePlanner().JoinBaseUrlAndPath(baseUrl, path, strategy);
    }

    public void Dispose()
    {
        if (_transport is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}

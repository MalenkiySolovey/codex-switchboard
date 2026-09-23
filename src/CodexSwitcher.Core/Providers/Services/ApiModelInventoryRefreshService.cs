using System.Collections.Concurrent;
using CodexSwitcher.Core.Accounts.Models;
using CodexSwitcher.Core.Common.Enums;
using CodexSwitcher.Core.Common.Environment;
using CodexSwitcher.Core.Common.Logging;
using CodexSwitcher.Core.Common.Time;
using CodexSwitcher.Core.Providers.Contracts;
using CodexSwitcher.Core.Providers.Models;
using CodexSwitcher.Core.Routing.Contracts;
using CodexSwitcher.Core.Routing.Models;
using CodexSwitcher.Core.Routing.Services;

namespace CodexSwitcher.Core.Providers.Services;

/// <summary>
/// Canonical model-inventory refresh pipeline. It intentionally never runs a
/// compatibility/inference probe: discovery is limited to the provider's
/// read-only GET /models strategy.
/// </summary>
public sealed class ApiModelInventoryRefreshService : IApiModelInventoryRefreshService
{
    private sealed class CallbackProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    private sealed class ProfileRefreshGate
    {
        public SemaphoreSlim Serial { get; } = new(1, 1);
        public object GenerationLock { get; } = new();
        public long LatestStarted;
        public long LatestPublished;
    }

    private readonly IApiProviderStore _profiles;
    private readonly IProviderInspectionService _inspection;
    private readonly IProviderModelCache _modelCache;
    private readonly ICodexRoutingConfigStore _routing;
    private readonly ICodexModelCatalogService _catalogs;
    private readonly ICodexTargetSwitchService _switches;
    private readonly CodexPaths _codexPaths;
    private readonly IClock _clock;
    private readonly IAuditLog? _audit;
    private readonly ConcurrentDictionary<Guid, ProfileRefreshGate> _gates = new();
    private readonly ConcurrentDictionary<Guid, ModelInventoryRefreshResult> _latest = new();

    public ApiModelInventoryRefreshService(
        IApiProviderStore profiles,
        IProviderInspectionService inspection,
        IProviderModelCache modelCache,
        ICodexRoutingConfigStore routing,
        ICodexModelCatalogService catalogs,
        ICodexTargetSwitchService switches,
        CodexPaths codexPaths,
        IClock clock,
        IAuditLog? audit = null)
    {
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _inspection = inspection ?? throw new ArgumentNullException(nameof(inspection));
        _modelCache = modelCache ?? throw new ArgumentNullException(nameof(modelCache));
        _routing = routing ?? throw new ArgumentNullException(nameof(routing));
        _catalogs = catalogs ?? throw new ArgumentNullException(nameof(catalogs));
        _switches = switches ?? throw new ArgumentNullException(nameof(switches));
        _codexPaths = codexPaths ?? throw new ArgumentNullException(nameof(codexPaths));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _audit = audit;
    }

    public event EventHandler<ModelInventoryRefreshResult>? RefreshStateChanged;

    public ModelInventoryRefreshResult? GetLatestResult(Guid profileId) =>
        _latest.TryGetValue(profileId, out var result) ? result : null;

    public async Task<ModelInventoryRefreshResult> RefreshAsync(
        Guid profileId,
        RefreshModelsOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var gate = _gates.GetOrAdd(profileId, static _ => new ProfileRefreshGate());
        long generation;
        ModelInventoryRefreshResult started;
        lock (gate.GenerationLock)
        {
            generation = Interlocked.Increment(ref gate.LatestStarted);
            started = new ModelInventoryRefreshResult
            {
                ProfileId = profileId,
                RequestGeneration = generation,
                Outcome = ModelInventoryRefreshOutcome.Refreshing,
                VerificationStatus = ModelCatalogVerificationStatus.Pending,
                ProgressMessage = "Refreshing models...",
                ObservedAt = _clock.UtcNow,
            };
        }
        PublishIfLatest(profileId, generation, gate, started);
        if (generation == Volatile.Read(ref gate.LatestStarted))
        {
            options?.Progress?.Report(started.ProgressMessage!);
        }

        await gate.Serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RefreshSerializedAsync(profileId, generation, gate, options, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Serial.Release();
        }
    }

    private async Task<ModelInventoryRefreshResult> RefreshSerializedAsync(
        Guid profileId,
        long generation,
        ProfileRefreshGate gate,
        RefreshModelsOptions? options,
        CancellationToken cancellationToken)
    {
        var observedAt = _clock.UtcNow;
        var profile = _profiles.GetById(profileId);
        if (profile is null)
        {
            return PublishTerminal(profileId, generation, gate, new ModelInventoryRefreshResult
            {
                ProfileId = profileId,
                RequestGeneration = generation,
                Outcome = ModelInventoryRefreshOutcome.Failed,
                ErrorKind = "ProfileNotFound",
                ErrorMessageSanitized = "API provider profile was not found.",
                VerificationStatus = ModelCatalogVerificationStatus.NotRequired,
                ObservedAt = observedAt,
            });
        }

        ApiProviderSnapshot discovery;
        try
        {
            discovery = await _inspection.DiscoverModelsAsync(profileId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return PublishTerminal(profileId, generation, gate, Failed(profileId, generation, observedAt, "DiscoveryFailed", Sanitize(ex.Message)));
        }

        // An older in-flight request is never allowed to persist or publish over
        // a newer request. The newer request will acquire this serial gate next.
        if (generation != Volatile.Read(ref gate.LatestStarted))
        {
            return Superseded(profileId, generation);
        }

        if (discovery.Models.Count == 0)
        {
            var message = string.IsNullOrWhiteSpace(discovery.Error)
                ? "Provider returned no models from GET /models."
                : Sanitize(discovery.Error);
            return PublishTerminal(profileId, generation, gate, new ModelInventoryRefreshResult
            {
                ProfileId = profileId,
                RequestGeneration = generation,
                Outcome = ModelInventoryRefreshOutcome.NoModelsReported,
                ErrorKind = "NoModelsReported",
                ErrorMessageSanitized = message,
                VerificationStatus = ModelCatalogVerificationStatus.NotRequired,
                ObservedAt = observedAt,
            });
        }

        ReportProgress(profileId, generation, gate, options, "Merging discovered models...");

        var original = CloneProfile(profile);
        var originalRouting = _routing.ReadRoutingState(_codexPaths.ConfigTomlPath);
        var activeAtStart = string.Equals(
            originalRouting.ModelProvider,
            profile.StableCodexProviderId,
            StringComparison.OrdinalIgnoreCase);

        profile.ModelInventory ??= new ApiProviderModelInventory();
        // A legacy SelectedModel represents an explicit user selection even
        // when preview.18 had not yet materialized an inventory. Migrate it
        // before merging discovery so a temporarily unreported selected model
        // remains a manual NotReported entry instead of disappearing.
        profile.ModelInventory.EnsureSelectedModelMigrated(
            profile.SelectedModel,
            displayName: null,
            contextWindow: profile.ModelOverrides?.ContextWindowTokens,
            overrides: profile.ModelOverrides);
        var beforeInventory = SnapshotInventory(profile.ModelInventory);
        var merge = profile.ModelInventory.MergeDiscoveredModels(discovery.Models, observedAt);
        NormalizeProfileDerivedDisplayNames(profile);
        profile.DiscoveredModels = discovery.Models.Distinct(StringComparer.Ordinal).ToList();
        profile.ModelInventory.SelectedModel ??= profile.SelectedModel;

        var routeKey = !string.IsNullOrWhiteSpace(profile.SelectedRouteId)
            ? profile.SelectedRouteId
            : profile.BaseUrl;
        try
        {
            lock (gate.GenerationLock)
            {
                if (generation != Volatile.Read(ref gate.LatestStarted))
                {
                    return Superseded(profileId, generation);
                }

                ReportProgress(profileId, generation, gate, options, "Persisting model inventory...");
                _profiles.Save(profile);
                _modelCache.SetModels(profile.Id, routeKey, 1, profile.DiscoveredModels);
            }

            string? catalogPath;
            bool catalogChanged = false;
            bool activeReconciled = false;
            bool runtimeRestarted = false;
            var verification = ModelCatalogVerificationStatus.NotRequired;

            if (activeAtStart)
            {
                if (!TryValidateSelectedModel(profile, out var selectionError))
                {
                    _profiles.Save(original);
                    _modelCache.SetModels(original.Id, routeKey, 1, original.DiscoveredModels ?? []);
                    return PublishTerminal(profileId, generation, gate, Failed(profileId, generation, observedAt, "SelectedModelInvalid", selectionError));
                }

                // Switch execution creates an immutable profile catalog, applies
                // one coherent provider+model+catalog projection, restarts when
                // the catalog changes, and verifies model/list before success.
                var switchOptions = options?.ActiveTargetSwitchOptions
                    ?? new SwitchExecutionOptions(
                        CloseReopenMode.Automatic,
                        TimeSpan.FromSeconds(10),
                        BackupsToKeep: 10);
                var progress = CreateProgressReporter(profileId, generation, gate, options);
                switchOptions = switchOptions with { Progress = progress };
                ReportProgress(profileId, generation, gate, options, "Updating catalog...");
                if (generation != Volatile.Read(ref gate.LatestStarted))
                {
                    return Superseded(profileId, generation);
                }
                var switchResult = await _switches.SwitchToApiProviderAsync(profileId, switchOptions, cancellationToken).ConfigureAwait(false);
                if (switchResult.Outcome is not (TargetSwitchOutcome.Success or TargetSwitchOutcome.SuccessWithReopenWarning or TargetSwitchOutcome.NoOp))
                {
                    _profiles.Save(original);
                    _modelCache.SetModels(original.Id, routeKey, 1, original.DiscoveredModels ?? []);
                    return PublishTerminal(profileId, generation, gate, Failed(
                        profileId,
                        generation,
                        observedAt,
                        "ActiveTargetReconciliationFailed",
                        Sanitize(switchResult.Message)));
                }

                var postRouting = _routing.ReadRoutingState(_codexPaths.ConfigTomlPath);
                var trace = switchResult.DiagnosticTrace;
                if (trace?.ActiveModelListVerified != true ||
                    !string.Equals(postRouting.ModelProvider, profile.StableCodexProviderId, StringComparison.Ordinal) ||
                    !string.Equals(postRouting.Model, profile.SelectedModel, StringComparison.Ordinal) ||
                    string.IsNullOrWhiteSpace(postRouting.ModelCatalogJson))
                {
                    _profiles.Save(original);
                    _modelCache.SetModels(original.Id, routeKey, 1, original.DiscoveredModels ?? []);
                    return PublishTerminal(profileId, generation, gate, Failed(
                        profileId,
                        generation,
                        observedAt,
                        "ActiveModelListUnverified",
                        "The active API provider, selected model, catalog, and production model/list result did not match."));
                }

                catalogPath = postRouting.ModelCatalogJson;
                catalogChanged = !SamePath(originalRouting.ModelCatalogJson, catalogPath);
                activeReconciled = true;
                runtimeRestarted = trace.RuntimeRestartRequired && trace.RuntimeRestartCompleted;
                verification = ModelCatalogVerificationStatus.Verified;
            }
            else
            {
                // Every usable API profile owns a profile-scoped immutable
                // catalog. Inactive refreshes never touch config.toml or the
                // current Codex process.
                ReportProgress(profileId, generation, gate, options, "Updating profile catalog...");
                catalogPath = _catalogs.EnsureProfileModelCatalog(profile);
                if (string.IsNullOrWhiteSpace(catalogPath))
                {
                    throw new InvalidOperationException("No profile catalog could be generated from the enabled model inventory.");
                }
                catalogChanged = true;
            }

            var inventoryChanged = !InventoryEquivalent(beforeInventory, profile.ModelInventory);
            var result = new ModelInventoryRefreshResult
            {
                ProfileId = profileId,
                RequestGeneration = generation,
                Outcome = ModelInventoryRefreshOutcome.Succeeded,
                ModelsReportedCount = merge.ReportedCount,
                ModelsAdded = merge.ModelsAdded,
                ModelsUpdated = merge.ModelsUpdated,
                ManualModelsPreserved = merge.ManualModelsPreserved,
                InventoryChanged = inventoryChanged,
                InventoryPersisted = true,
                CatalogChanged = catalogChanged,
                CatalogPath = catalogPath,
                ActiveTargetReconciled = activeReconciled,
                RuntimeRestarted = runtimeRestarted,
                VerificationStatus = verification,
                ObservedAt = observedAt,
            };

            _audit?.Record(
                "api-model-refresh",
                "success",
                $"PROFILE_ID={profileId:D} REPORTED={result.ModelsReportedCount} ADDED={result.ModelsAdded} UPDATED={result.ModelsUpdated} ACTIVE_RECONCILED={result.ActiveTargetReconciled} CATALOG_PATH={result.CatalogPath ?? "none"} RUNTIME_RESTARTED={result.RuntimeRestarted} VERIFICATION={result.VerificationStatus}");
            return PublishTerminal(profileId, generation, gate, result);
        }
        catch (Exception ex)
        {
            // The only config mutation for an active refresh is delegated to
            // the switch transaction, which compensates its own config/process
            // changes. Restore the persisted inventory snapshot here too.
            _profiles.Save(original);
            _modelCache.SetModels(original.Id, routeKey, 1, original.DiscoveredModels ?? []);
            return PublishTerminal(profileId, generation, gate, Failed(profileId, generation, observedAt, "CatalogOrReconciliationFailed", Sanitize(ex.Message)));
        }
    }

    private ModelInventoryRefreshResult PublishTerminal(
        Guid profileId,
        long generation,
        ProfileRefreshGate gate,
        ModelInventoryRefreshResult result)
    {
        PublishIfLatest(profileId, generation, gate, result);
        return result;
    }

    private ModelInventoryRefreshResult Superseded(Guid profileId, long generation) => new()
    {
        ProfileId = profileId,
        RequestGeneration = generation,
        Outcome = ModelInventoryRefreshOutcome.Superseded,
        VerificationStatus = ModelCatalogVerificationStatus.NotRequired,
        ObservedAt = _clock.UtcNow,
    };

    private void ReportProgress(
        Guid profileId,
        long generation,
        ProfileRefreshGate gate,
        RefreshModelsOptions? options,
        string message)
    {
        if (generation == Volatile.Read(ref gate.LatestStarted))
        {
            options?.Progress?.Report(message);
        }

        PublishIfLatest(profileId, generation, gate, new ModelInventoryRefreshResult
        {
            ProfileId = profileId,
            RequestGeneration = generation,
            Outcome = ModelInventoryRefreshOutcome.Refreshing,
            ProgressMessage = message,
            VerificationStatus = ModelCatalogVerificationStatus.Pending,
            ObservedAt = _clock.UtcNow,
        });
    }

    private CallbackProgress CreateProgressReporter(
        Guid profileId,
        long generation,
        ProfileRefreshGate gate,
        RefreshModelsOptions? options) =>
        new CallbackProgress(message => ReportProgress(profileId, generation, gate, options, message));

    private void PublishIfLatest(Guid profileId, long generation, ProfileRefreshGate gate, ModelInventoryRefreshResult result)
    {
        lock (gate.GenerationLock)
        {
            if (generation != gate.LatestStarted || generation < gate.LatestPublished)
            {
                return;
            }

            gate.LatestPublished = generation;
            _latest[profileId] = result;
        }

        // Notify outside the generation lock: subscribers may trigger UI work
        // or a follow-up refresh. View-model projections also reject any older
        // event that was queued before this newer publication.
        RefreshStateChanged?.Invoke(this, result);
    }

    private static ModelInventoryRefreshResult Failed(
        Guid profileId,
        long generation,
        DateTimeOffset observedAt,
        string kind,
        string message) =>
        new()
        {
            ProfileId = profileId,
            RequestGeneration = generation,
            Outcome = ModelInventoryRefreshOutcome.Failed,
            ErrorKind = kind,
            ErrorMessageSanitized = message,
            VerificationStatus = ModelCatalogVerificationStatus.Failed,
            ObservedAt = observedAt,
        };

    private static bool TryValidateSelectedModel(ApiProviderProfile profile, out string error)
    {
        if (string.IsNullOrWhiteSpace(profile.SelectedModel))
        {
            error = "Select an enabled model before activating this API profile.";
            return false;
        }

        var selected = profile.ModelInventory?.Models.FirstOrDefault(m =>
            string.Equals(m.Slug, profile.SelectedModel, StringComparison.Ordinal));
        if (selected is null || !selected.Enabled)
        {
            error = "Select an enabled model before activating this API profile.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static void NormalizeProfileDerivedDisplayNames(ApiProviderProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Nickname) || profile.ModelInventory is null)
        {
            return;
        }

        foreach (var item in profile.ModelInventory.Models)
        {
            if (string.IsNullOrWhiteSpace(item.Slug) || string.IsNullOrWhiteSpace(item.DisplayName))
            {
                continue;
            }

            // preview.18 accidentally used values such as "Modelflare Grok"
            // as every model's display label. Preserve deliberate arbitrary
            // manual labels, but repair the recognizable endpoint-prefix form
            // whenever it appears in a refreshed inventory.
            var profilePrefix = profile.Nickname + " ";
            if (string.Equals(item.DisplayName, profile.Nickname, StringComparison.OrdinalIgnoreCase) ||
                item.DisplayName.StartsWith(profilePrefix, StringComparison.OrdinalIgnoreCase))
            {
                item.DisplayName = ModelDisplayName.FromSlug(item.Slug);
            }
        }
    }

    private static bool SamePath(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second))
        {
            return string.IsNullOrWhiteSpace(first) && string.IsNullOrWhiteSpace(second);
        }

        return string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);
    }

    private static List<ApiProviderModelItem> SnapshotInventory(ApiProviderModelInventory inventory) =>
        inventory.Models.Select(CloneModel).ToList();

    private static bool InventoryEquivalent(List<ApiProviderModelItem> before, ApiProviderModelInventory after)
    {
        if (before.Count != after.Models.Count)
        {
            return false;
        }

        for (var index = 0; index < before.Count; index++)
        {
            var left = before[index];
            var right = after.Models[index];
            if (!string.Equals(left.Slug, right.Slug, StringComparison.Ordinal) ||
                !string.Equals(left.DisplayName, right.DisplayName, StringComparison.Ordinal) ||
                left.Enabled != right.Enabled ||
                left.DiscoverySource != right.DiscoverySource ||
                left.Availability != right.Availability)
            {
                return false;
            }
        }

        return true;
    }

    private static ApiProviderProfile CloneProfile(ApiProviderProfile source)
    {
        return new ApiProviderProfile
        {
            Id = source.Id,
            CatalogProviderId = source.CatalogProviderId,
            StableCodexProviderId = source.StableCodexProviderId,
            Nickname = source.Nickname,
            BaseUrl = source.BaseUrl,
            SelectedRouteId = source.SelectedRouteId,
            SelectedModel = source.SelectedModel,
            WireApi = source.WireApi,
            KeyPreview = source.KeyPreview,
            Status = source.Status,
            CreatedAt = source.CreatedAt,
            LastSwitchedAt = source.LastSwitchedAt,
            SortOrder = source.SortOrder,
            ModelOverrides = CloneOverrides(source.ModelOverrides),
            TransportOverrides = source.TransportOverrides,
            EndpointId = source.EndpointId,
            ProviderPresetId = source.ProviderPresetId,
            RoutePoolLabel = source.RoutePoolLabel,
            DiscoveredModels = source.DiscoveredModels is null ? null : new List<string>(source.DiscoveredModels),
            ModelInventory = source.ModelInventory is null ? null : new ApiProviderModelInventory
            {
                Models = source.ModelInventory.Models.Select(CloneModel).ToList(),
                SelectedModel = source.ModelInventory.SelectedModel,
                DiscoveryStatus = source.ModelInventory.DiscoveryStatus,
                LastDiscoveryAt = source.ModelInventory.LastDiscoveryAt,
            },
            CompatibilityLevel = source.CompatibilityLevel,
            LastProbeReport = source.LastProbeReport,
        };
    }

    private static ApiProviderModelItem CloneModel(ApiProviderModelItem source) =>
        new()
        {
            Slug = source.Slug,
            DisplayName = source.DisplayName,
            Enabled = source.Enabled,
            DiscoverySource = source.DiscoverySource,
            Availability = source.Availability,
            LastSeenAt = source.LastSeenAt,
            ContextWindow = source.ContextWindow,
            ContextEvidence = source.ContextEvidence,
            Capabilities = source.Capabilities,
            UserOverrides = CloneOverrides(source.UserOverrides),
        };

    private static CodexModelOverrides? CloneOverrides(CodexModelOverrides? source) =>
        source is null
            ? null
            : new CodexModelOverrides
            {
                ContextWindowTokens = source.ContextWindowTokens,
                AutoCompactTokenLimit = source.AutoCompactTokenLimit,
                AutoCompactTokenLimitScope = source.AutoCompactTokenLimitScope,
                ReasoningEffort = source.ReasoningEffort,
                ReasoningSummary = source.ReasoningSummary,
                Verbosity = source.Verbosity,
                ToolOutputTokenLimit = source.ToolOutputTokenLimit,
            };

    private static string Sanitize(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "Model inventory refresh failed.";
        }

        if (message.Contains("Bearer ", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("api key", StringComparison.OrdinalIgnoreCase))
        {
            return "Model inventory refresh failed (credentials redacted).";
        }

        return message;
    }
}

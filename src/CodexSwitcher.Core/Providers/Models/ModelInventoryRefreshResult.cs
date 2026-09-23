using CodexSwitcher.Core.Routing.Models;
using CodexSwitcher.Core.Routing.Services;

namespace CodexSwitcher.Core.Providers.Models;

/// <summary>
/// Authoritative outcome of one API-profile GET /models refresh. This is the
/// one fact consumed by card and dialog projections; neither presentation
/// surface infers its own discovery status.
/// </summary>
public sealed record ModelInventoryRefreshResult
{
    public Guid ProfileId { get; init; }
    public long RequestGeneration { get; init; }
    public ModelInventoryRefreshOutcome Outcome { get; init; }
    public int ModelsReportedCount { get; init; }
    public int ModelsAdded { get; init; }
    public int ModelsUpdated { get; init; }
    public int ManualModelsPreserved { get; init; }
    public bool InventoryChanged { get; init; }
    public bool InventoryPersisted { get; init; }
    public bool CatalogChanged { get; init; }
    public string? CatalogPath { get; init; }
    public bool ActiveTargetReconciled { get; init; }
    public bool RuntimeRestarted { get; init; }
    public ModelCatalogVerificationStatus VerificationStatus { get; init; }
    public string? ErrorKind { get; init; }
    public string? ErrorMessageSanitized { get; init; }
    public string? ProgressMessage { get; init; }
    public DateTimeOffset ObservedAt { get; init; }

    public bool Succeeded => Outcome == ModelInventoryRefreshOutcome.Succeeded;
    public bool IsTerminalFailure => Outcome is ModelInventoryRefreshOutcome.Failed or ModelInventoryRefreshOutcome.NoModelsReported;
}

public enum ModelInventoryRefreshOutcome
{
    Unknown = 0,
    Refreshing = 1,
    Succeeded = 2,
    NoModelsReported = 3,
    Failed = 4,
    Superseded = 5,
}

public enum ModelCatalogVerificationStatus
{
    NotRequired = 0,
    Pending = 1,
    Verified = 2,
    Failed = 3,
}

/// <summary>
/// Execution choices for the shared refresh use case. Refresh remains a
/// read-only provider GET /models operation; only an already active profile
/// receives the explicitly requested target-reconciliation transaction.
/// </summary>
public sealed record RefreshModelsOptions(
    SwitchExecutionOptions? ActiveTargetSwitchOptions = null,
    IProgress<string>? Progress = null);

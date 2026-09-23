using CodexSwitcher.Core.Common.Errors;

namespace CodexSwitcher.Core.Usage.Models;

/// <summary>
/// Granular classification of account activity error.
/// </summary>
public enum ActivityErrorReason
{
    None = 0,
    Timeout = 1,
    RpcError = 2,
    Unsupported = 3,
    NoData = 4,
}

/// <summary>
/// Normalized snapshot of server-reported account activity and daily token buckets.
/// Decoupled from transport JSON and strictly non-secret.
/// </summary>
public sealed record AccountActivitySnapshot(
    Guid ProfileId,
    DateTimeOffset ObservedAt,
    AccountTokenUsageSummary? Summary,
    IReadOnlyList<DailyTokenUsage> DailyBuckets,
    ErrorInfo? LastError = null,
    ActivityErrorReason? ErrorReason = null);

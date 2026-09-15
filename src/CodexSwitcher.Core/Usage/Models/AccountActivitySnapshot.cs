using CodexSwitcher.Core.Common.Errors;

namespace CodexSwitcher.Core.Usage.Models;

/// <summary>
/// Normalized snapshot of server-reported account activity and daily token buckets.
/// Decoupled from transport JSON and strictly non-secret.
/// </summary>
public sealed record AccountActivitySnapshot(
    Guid ProfileId,
    DateTimeOffset ObservedAt,
    AccountTokenUsageSummary? Summary,
    IReadOnlyList<DailyTokenUsage> DailyBuckets,
    ErrorInfo? LastError = null);

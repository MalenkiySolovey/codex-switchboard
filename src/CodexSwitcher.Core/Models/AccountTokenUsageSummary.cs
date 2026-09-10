namespace CodexSwitcher.Core.Models;

/// <summary>
/// Server-reported summary metrics returned by <c>account/usage/read</c>.
/// All metrics are authoritative server outputs and must not be recomputed by the client.
/// </summary>
public sealed record AccountTokenUsageSummary(
    long? LifetimeTokens,
    long? PeakDailyTokens,
    long? LongestRunningTurnSeconds,
    long? CurrentStreakDays,
    long? LongestStreakDays);

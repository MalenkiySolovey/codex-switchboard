namespace CodexSwitcher.Core.Models;

/// <summary>
/// Status classification for an individual rate-limit reset credit.
/// </summary>
public enum ResetCreditStatus
{
    Available = 0,
    Redeeming = 1,
    Redeemed = 2,
    Unknown = 3,
}

/// <summary>
/// Urgency classification for reset-credit expiration display.
/// </summary>
public enum CreditExpiryUrgency
{
    Normal = 0,    // > 7 days remaining
    Warning = 1,   // <= 7 days remaining
    Critical = 2,  // <= 24 hours remaining
    Expired = 3,   // expiry timestamp has passed
}

/// <summary>
/// A normalized, non-sensitive detail row for an individual earned reset credit.
/// The opaque server-side credit ID is internal-only, never displayed or persisted.
/// </summary>
public sealed record RateLimitResetCredit(
    string? Id,
    string ResetType,
    string Status,
    DateTimeOffset? GrantedAt,
    DateTimeOffset? ExpiresAt,
    string? Title,
    string? Description)
{
    public ResetCreditStatus NormalizedStatus => Status?.ToLowerInvariant() switch
    {
        "available" => ResetCreditStatus.Available,
        "redeeming" => ResetCreditStatus.Redeeming,
        "redeemed" => ResetCreditStatus.Redeemed,
        _ => ResetCreditStatus.Unknown
    };
}

/// <summary>
/// Normalized summary of rate-limit reset credits.
/// AvailableCount is authoritative; Credits may be null (when unsupported by runtime)
/// or capped by the backend.
/// </summary>
public sealed record RateLimitResetCredits(
    int AvailableCount,
    IReadOnlyList<RateLimitResetCredit>? Credits = null)
{
    /// <summary>
    /// Indicates whether detailed credit rows were reported by the server.
    /// </summary>
    public bool HasDetailedCredits => Credits is not null;

    /// <summary>
    /// Returns available credits sorted by nearest expiry first, with non-expiring or unknown expiry last.
    /// </summary>
    public IReadOnlyList<RateLimitResetCredit> SortedCredits =>
        Credits is null
            ? []
            : Credits
                .OrderBy(c => c.ExpiresAt.HasValue ? 0 : 1)
                .ThenBy(c => c.ExpiresAt ?? DateTimeOffset.MaxValue)
                .ToList();

    /// <summary>
    /// Number of credits reported in the detailed array that have an available status.
    /// </summary>
    public int ReportedAvailableCount =>
        Credits?.Count(c => c.NormalizedStatus == ResetCreditStatus.Available) ?? 0;

    /// <summary>
    /// Number of available credits whose detailed expiry rows are omitted/capped by the backend.
    /// </summary>
    public int UnreportedCount =>
        Credits is null
            ? AvailableCount
            : Math.Max(0, AvailableCount - Credits.Count);
}

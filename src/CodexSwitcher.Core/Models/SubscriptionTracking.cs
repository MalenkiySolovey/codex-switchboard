namespace CodexSwitcher.Core.Models;

/// <summary>
/// Display mode for user-provided subscription tracking date.
/// </summary>
public enum SubscriptionTrackingMode
{
    Renewal = 0,
    Expiration = 1,
}

/// <summary>
/// Provenance of subscription tracking date.
/// Currently UserProvided; never inferred from tokens or Codex protocol.
/// </summary>
public enum SubscriptionDateSource
{
    UserProvided = 0,
}

/// <summary>
/// Optional, non-secret local profile metadata for tracking subscription renewal or expiration.
/// Stored in profiles.json outside the vault. Never derived automatically from JWT iat/exp or profile dates.
/// </summary>
public sealed record SubscriptionTracking(
    DateOnly? StartedOn,
    DateOnly? NextRenewalOrExpiryOn,
    SubscriptionTrackingMode Mode = SubscriptionTrackingMode.Renewal,
    SubscriptionDateSource Source = SubscriptionDateSource.UserProvided,
    bool IsEstimated = false)
{
    /// <summary>
    /// Returns true if an authoritative target date (renewal or expiration) is configured.
    /// </summary>
    public bool HasTargetDate => NextRenewalOrExpiryOn.HasValue;

    /// <summary>
    /// Calculates an estimated next monthly renewal date from StartedOn.
    /// Returns null if StartedOn is missing or if the date cannot be determined.
    /// </summary>
    public static DateOnly? EstimateNextMonthlyRenewal(DateOnly startedOn, DateOnly today)
    {
        var targetDay = startedOn.Day;
        // Start from current month
        var candidateYear = today.Year;
        var candidateMonth = today.Month;

        int daysInMonth = DateTime.DaysInMonth(candidateYear, candidateMonth);
        int day = Math.Min(targetDay, daysInMonth);
        var candidate = new DateOnly(candidateYear, candidateMonth, day);

        if (candidate <= today)
        {
            // Move to next month
            if (candidateMonth == 12)
            {
                candidateYear++;
                candidateMonth = 1;
            }
            else
            {
                candidateMonth++;
            }
            daysInMonth = DateTime.DaysInMonth(candidateYear, candidateMonth);
            day = Math.Min(targetDay, daysInMonth);
            candidate = new DateOnly(candidateYear, candidateMonth, day);
        }

        return candidate;
    }
}

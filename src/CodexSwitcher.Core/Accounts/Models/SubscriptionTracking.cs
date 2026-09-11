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
using CodexSwitcher.Core.Providers.Contracts;
using CodexSwitcher.Core.Providers.Models;
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
namespace CodexSwitcher.Core.Accounts.Models;

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

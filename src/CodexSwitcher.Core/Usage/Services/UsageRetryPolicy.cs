using CodexSwitcher.Core.Accounts.Contracts;
using CodexSwitcher.Core.Accounts.Formatting;
using CodexSwitcher.Core.Accounts.Models;
using CodexSwitcher.Core.Accounts.Services;
using CodexSwitcher.Core.Common.Dispatcher;
using CodexSwitcher.Core.Common.Enums;
using CodexSwitcher.Core.Common.Environment;
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
using CodexSwitcher.Core.Usage.Services;
using CodexSwitcher.Core.Common.Errors;
using CodexSwitcher.Core.Usage.Models;

namespace CodexSwitcher.Core.Usage.Services;

/// <summary>
/// Evaluates error retryability and computes exponential backoff delays for usage polling.
/// Pure, deterministic, and testable without thread sleeping.
/// </summary>
public static class UsageRetryPolicy
{
    public static readonly TimeSpan DefaultBaseDelay = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan DefaultMaxDelay = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Determines whether a failed usage query is retryable via automatic backoff.
    /// Non-retryable conditions (authentication failure, unsupported account, decryption failure)
    /// require user intervention and must not loop in automatic backoff.
    /// </summary>
    public static bool IsRetryable(UsageStatus status, ErrorInfo? error)
    {
        if (status is UsageStatus.Healthy)
            return false;

        if (status is UsageStatus.AuthRequired or UsageStatus.UnsupportedAccountType)
            return false;

        // Rate-limited accounts are governed by window reset timestamps, not error backoff.
        if (status is UsageStatus.RateLimited)
            return false;

        if (error is null)
            return status is UsageStatus.ProcessDown or UsageStatus.BackingOff or UsageStatus.Error;

        if (error.Category is ErrorCategory.DecryptionFailed or
            ErrorCategory.InvalidAuthFile or
            ErrorCategory.RefreshTokenExpired)
        {
            return false;
        }

        // Server overloaded error (-32001) or timeout or process transient down
        if (error.Message.Contains("-32001", StringComparison.OrdinalIgnoreCase) ||
            error.Message.Contains("overload", StringComparison.OrdinalIgnoreCase) ||
            error.Category is ErrorCategory.Timeout or ErrorCategory.CodexNotFound or ErrorCategory.ProcessRemnant)
        {
            return true;
        }

        return true;
    }

    /// <summary>
    /// Computes exponential backoff delay based on failure count:
    /// delay = min(maxDelay, baseDelay * 2^(failureCount - 1)).
    /// </summary>
    public static TimeSpan ComputeBackoff(
        int failureCount,
        TimeSpan? baseDelay = null,
        TimeSpan? maxDelay = null)
    {
        if (failureCount <= 0)
            return TimeSpan.Zero;

        var b = baseDelay ?? DefaultBaseDelay;
        var m = maxDelay ?? DefaultMaxDelay;

        // Cap exponent to prevent overflow
        var exponent = Math.Min(failureCount - 1, 30);
        var multiplier = Math.Pow(2, exponent);
        var calculatedSeconds = b.TotalSeconds * multiplier;

        if (calculatedSeconds >= m.TotalSeconds || double.IsInfinity(calculatedSeconds))
            return m;

        return TimeSpan.FromSeconds(calculatedSeconds);
    }

    /// <summary>
    /// Computes the next retry timestamp for a failed query, or null if non-retryable.
    /// </summary>
    public static DateTimeOffset? GetNextRetryTime(
        int failureCount,
        UsageStatus status,
        ErrorInfo? error,
        DateTimeOffset now,
        TimeSpan? baseDelay = null,
        TimeSpan? maxDelay = null)
    {
        if (!IsRetryable(status, error))
            return null;

        var backoff = ComputeBackoff(failureCount, baseDelay, maxDelay);
        return now.Add(backoff);
    }
}

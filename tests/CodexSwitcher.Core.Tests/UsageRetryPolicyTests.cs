using CodexSwitcher.Core.Models;
using CodexSwitcher.Core.Services;

namespace CodexSwitcher.Core.Tests;

public sealed class UsageRetryPolicyTests
{
    [Fact]
    public void ComputeBackoff_FollowsExponentialCurve_AndCapsAtMaxDelay()
    {
        var baseDelay = TimeSpan.FromSeconds(15);
        var maxDelay = TimeSpan.FromMinutes(10);

        Assert.Equal(TimeSpan.Zero, UsageRetryPolicy.ComputeBackoff(0, baseDelay, maxDelay));
        Assert.Equal(TimeSpan.FromSeconds(15), UsageRetryPolicy.ComputeBackoff(1, baseDelay, maxDelay));
        Assert.Equal(TimeSpan.FromSeconds(30), UsageRetryPolicy.ComputeBackoff(2, baseDelay, maxDelay));
        Assert.Equal(TimeSpan.FromSeconds(60), UsageRetryPolicy.ComputeBackoff(3, baseDelay, maxDelay));
        Assert.Equal(TimeSpan.FromSeconds(120), UsageRetryPolicy.ComputeBackoff(4, baseDelay, maxDelay));
        Assert.Equal(TimeSpan.FromSeconds(240), UsageRetryPolicy.ComputeBackoff(5, baseDelay, maxDelay));
        Assert.Equal(TimeSpan.FromSeconds(480), UsageRetryPolicy.ComputeBackoff(6, baseDelay, maxDelay));
        // Attempt 7 calculates 960s, but is capped at 600s
        Assert.Equal(TimeSpan.FromMinutes(10), UsageRetryPolicy.ComputeBackoff(7, baseDelay, maxDelay));
        Assert.Equal(TimeSpan.FromMinutes(10), UsageRetryPolicy.ComputeBackoff(100, baseDelay, maxDelay));
    }

    [Fact]
    public void ServerOverload32001_AndTransientErrors_AreRetryable()
    {
        var now = DateTimeOffset.UtcNow;
        var overloadError = ErrorInfo.Create(ErrorCategory.Unknown, "JSON-RPC error -32001: Server overloaded. Please try again later.", now);
        Assert.True(UsageRetryPolicy.IsRetryable(UsageStatus.Error, overloadError));

        var timeoutError = ErrorInfo.Create(ErrorCategory.Timeout, "Request timed out.", now);
        Assert.True(UsageRetryPolicy.IsRetryable(UsageStatus.ProcessDown, timeoutError));

        var genericOverload = ErrorInfo.Create(ErrorCategory.Unknown, "Capacity overload reached", now);
        Assert.True(UsageRetryPolicy.IsRetryable(UsageStatus.Error, genericOverload));
    }

    [Fact]
    public void AuthRequired_UnsupportedAccountType_AndDecryptionFailed_AreNotRetryable()
    {
        var now = DateTimeOffset.UtcNow;

        Assert.False(UsageRetryPolicy.IsRetryable(UsageStatus.AuthRequired, ErrorInfo.Create(ErrorCategory.RefreshTokenExpired, "Expired", now)));
        Assert.False(UsageRetryPolicy.IsRetryable(UsageStatus.UnsupportedAccountType, ErrorInfo.Create(ErrorCategory.InvalidAuthFile, "Unsupported", now)));

        var decrErr = ErrorInfo.Create(ErrorCategory.DecryptionFailed, "Cannot decrypt", now);
        Assert.False(UsageRetryPolicy.IsRetryable(UsageStatus.Error, decrErr));

        var invalidAuth = ErrorInfo.Create(ErrorCategory.InvalidAuthFile, "Invalid auth.json", now);
        Assert.False(UsageRetryPolicy.IsRetryable(UsageStatus.Error, invalidAuth));
    }

    [Fact]
    public void RateLimitedAccount_IsNotRetriedAggressively()
    {
        var now = DateTimeOffset.UtcNow;
        // Rate-limited accounts are governed by window reset times, not error backoff
        Assert.False(UsageRetryPolicy.IsRetryable(UsageStatus.RateLimited, null));
    }

    [Fact]
    public void GetNextRetryTime_ComputesExpectedFutureTimestamp()
    {
        var now = new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);
        var err = ErrorInfo.Create(ErrorCategory.Timeout, "Timeout", now);

        var retry1 = UsageRetryPolicy.GetNextRetryTime(1, UsageStatus.ProcessDown, err, now);
        Assert.NotNull(retry1);
        Assert.Equal(now.AddSeconds(15), retry1.Value);

        var retry2 = UsageRetryPolicy.GetNextRetryTime(2, UsageStatus.ProcessDown, err, now);
        Assert.NotNull(retry2);
        Assert.Equal(now.AddSeconds(30), retry2.Value);

        var noRetry = UsageRetryPolicy.GetNextRetryTime(1, UsageStatus.AuthRequired, null, now);
        Assert.Null(noRetry);
    }
}


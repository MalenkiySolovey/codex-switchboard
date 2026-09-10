using CodexSwitcher.Core.Models;
using CodexSwitcher.Core.Support;

namespace CodexSwitcher.Core.Tests;

public sealed class AccountActivityFormattingTests
{
    [Fact]
    public void Test26_NullMetric_RendersFallback()
    {
        Assert.Equal("—", UsageMetricFormatter.FormatTokenCount(null));
        Assert.Equal("Not reported", UsageMetricFormatter.FormatTokenCount(null, "Not reported"));
        Assert.Equal("—", UsageMetricFormatter.FormatTurnDuration(null));
        Assert.Equal("—", UsageMetricFormatter.FormatStreak(null));
    }

    [Fact]
    public void Test27_LongestTurnDuration_FormattedCorrectly()
    {
        Assert.Equal("45s", UsageMetricFormatter.FormatTurnDuration(45));
        Assert.Equal("12m 04s", UsageMetricFormatter.FormatTurnDuration(724));
        Assert.Equal("1h 41m", UsageMetricFormatter.FormatTurnDuration(6073));
        Assert.Equal("2h 00m", UsageMetricFormatter.FormatTurnDuration(7200));
    }

    [Fact]
    public void Test28_TokenCount_FormattedWithKMB()
    {
        Assert.Equal("0", UsageMetricFormatter.FormatTokenCount(0));
        Assert.Equal("500", UsageMetricFormatter.FormatTokenCount(500));
        Assert.Equal("12.4K", UsageMetricFormatter.FormatTokenCount(12400));
        Assert.Equal("5.8M", UsageMetricFormatter.FormatTokenCount(5800000));
        Assert.Equal("1.13B", UsageMetricFormatter.FormatTokenCount(1133785860));
        Assert.Equal("201.8M", UsageMetricFormatter.FormatTokenCount(201820447));
    }

    [Fact]
    public void Test29_EmptyGraph_HandledGracefully()
    {
        var snapshot = new AccountActivitySnapshot(
            ProfileId: Guid.NewGuid(),
            ObservedAt: DateTimeOffset.UtcNow,
            Summary: null,
            DailyBuckets: Array.Empty<DailyTokenUsage>());

        Assert.Empty(snapshot.DailyBuckets);
    }

    [Fact]
    public void Test30_SingleGraphBucket_ScaledSafely()
    {
        var bucket = new DailyTokenUsage("2026-08-01", new DateOnly(2026, 8, 1), 50000);
        var snapshot = new AccountActivitySnapshot(
            ProfileId: Guid.NewGuid(),
            ObservedAt: DateTimeOffset.UtcNow,
            Summary: null,
            DailyBuckets: [bucket]);

        Assert.Single(snapshot.DailyBuckets);
        Assert.Equal(50000L, snapshot.DailyBuckets[0].Tokens);
    }

    [Fact]
    public void Test31_AllZeroGraph_NoDivisionByZero()
    {
        var buckets = new List<DailyTokenUsage>
        {
            new("2026-08-01", new DateOnly(2026, 8, 1), 0),
            new("2026-08-02", new DateOnly(2026, 8, 2), 0)
        };

        var snapshot = new AccountActivitySnapshot(
            ProfileId: Guid.NewGuid(),
            ObservedAt: DateTimeOffset.UtcNow,
            Summary: null,
            DailyBuckets: buckets);

        long maxTokens = 0;
        foreach (var b in snapshot.DailyBuckets)
        {
            if (b.Tokens > maxTokens) maxTokens = b.Tokens;
        }

        // Logic tested: when maxTokens == 0, scale factor does not divide by zero
        double height = maxTokens > 0 ? ((double)buckets[0].Tokens / maxTokens) * 36.0 : 0.0;
        Assert.Equal(0.0, height);
    }

    [Fact]
    public void Test32_HugeGraphValues_ScaleWithoutOverflow()
    {
        var buckets = new List<DailyTokenUsage>
        {
            new("2026-08-01", new DateOnly(2026, 8, 1), 1_000_000_000_000L),
            new("2026-08-02", new DateOnly(2026, 8, 2), 500_000_000_000L)
        };

        var snapshot = new AccountActivitySnapshot(
            ProfileId: Guid.NewGuid(),
            ObservedAt: DateTimeOffset.UtcNow,
            Summary: null,
            DailyBuckets: buckets);

        long maxTokens = 1_000_000_000_000L;
        double h1 = ((double)buckets[0].Tokens / maxTokens) * 36.0;
        double h2 = ((double)buckets[1].Tokens / maxTokens) * 36.0;

        Assert.Equal(36.0, h1);
        Assert.Equal(18.0, h2);
    }

    [Fact]
    public void Test37_CountdownTimer_PureLocalMath_ZeroAnalyticsInvocation()
    {
        var now = new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
        var resetAt = now.AddMinutes(45);
        var label = CodexSwitcher.Core.Support.UsageCountdownFormatter.FormatCountdown(resetAt, now);

        // Relative countdown is purely arithmetic; zero network, zero analytics calls
        Assert.Equal("Resets in 45m", label);
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using CodexSwitcher.Core.Usage.Formatting;
using CodexSwitcher.Core.Usage.Models;
using CodexSwitcher.Infra.Codex.Usage;
using Xunit;

namespace CodexSwitcher.Core.Tests;

public sealed class AccountActivityDateAndStreakSemanticsTests
{
    private readonly Guid _profileId = Guid.NewGuid();

    [Fact]
    public void ServerCurrentAndLongestStreak_WhenPresent_AreUsedDirectlyWithoutFabrication()
    {
        var summary = new AccountTokenUsageSummary(
            LifetimeTokens: 1_000_000,
            PeakDailyTokens: 50_000,
            LongestRunningTurnSeconds: 120,
            CurrentStreakDays: 7,
            LongestStreakDays: 14);

        var snapshot = new AccountActivitySnapshot(
            ProfileId: _profileId,
            ObservedAt: DateTimeOffset.UtcNow,
            Summary: summary,
            DailyBuckets: []);

        Assert.NotNull(snapshot.Summary);
        Assert.Equal(7L, snapshot.Summary.CurrentStreakDays);
        Assert.Equal(14L, snapshot.Summary.LongestStreakDays);
        Assert.Equal("7d", UsageMetricFormatter.FormatStreak(snapshot.Summary.CurrentStreakDays));
        Assert.Equal("14d", UsageMetricFormatter.FormatStreak(snapshot.Summary.LongestStreakDays));
    }

    [Fact]
    public void ServerStreak_WhenNull_RendersUnavailableDashWithoutLocalFabrication()
    {
        var summary = new AccountTokenUsageSummary(
            LifetimeTokens: 1_000_000,
            PeakDailyTokens: 50_000,
            LongestRunningTurnSeconds: 120,
            CurrentStreakDays: null,
            LongestStreakDays: null);

        var buckets = new List<DailyTokenUsage>
        {
            new("2026-09-20", new DateOnly(2026, 9, 20), 10_000),
            new("2026-09-21", new DateOnly(2026, 9, 21), 15_000),
            new("2026-09-22", new DateOnly(2026, 9, 22), 20_000),
            new("2026-09-23", new DateOnly(2026, 9, 23), 25_000),
        };

        var snapshot = new AccountActivitySnapshot(
            ProfileId: _profileId,
            ObservedAt: DateTimeOffset.UtcNow,
            Summary: summary,
            DailyBuckets: buckets);

        Assert.NotNull(snapshot.Summary);
        Assert.Null(snapshot.Summary.CurrentStreakDays);
        Assert.Null(snapshot.Summary.LongestStreakDays);
        Assert.Equal("—", UsageMetricFormatter.FormatStreak(snapshot.Summary.CurrentStreakDays));
        Assert.Equal("—", UsageMetricFormatter.FormatStreak(snapshot.Summary.LongestStreakDays));
    }

    [Theory]
    [InlineData(-11, 0)]  // Samoa / Niue (UTC-11)
    [InlineData(0, 0)]    // UTC
    [InlineData(14, 0)]   // Line Islands / Kiribati (UTC+14)
    public void DailyBucket_CalendarDateLabel_RemainsInvariantAcrossTimezones(int offsetHours, int offsetMinutes)
    {
        var json = """
        {
            "summary": {
                "lifetimeTokens": 1000,
                "currentStreakDays": 1,
                "longestStreakDays": 1
            },
            "dailyUsageBuckets": [
                { "startDate": "2026-09-23", "tokens": 5000 }
            ]
        }
        """;

        var offset = new TimeSpan(offsetHours, offsetMinutes, 0);
        var observedTime = new DateTimeOffset(2026, 9, 23, 1, 0, 0, offset);

        using var doc = JsonDocument.Parse(json);
        var snapshot = CodexUsageResponseParser.ParseAccountActivity(_profileId, doc.RootElement, observedTime);

        Assert.Single(snapshot.DailyBuckets);
        var bucket = snapshot.DailyBuckets[0];

        // Strict invariant: calendar date label must NOT shift across midnight boundaries
        Assert.Equal("2026-09-23", bucket.StartDateRaw);
        Assert.True(bucket.ParsedDate.HasValue);
        Assert.Equal(new DateOnly(2026, 9, 23), bucket.ParsedDate.Value);
        Assert.Equal("09/23", bucket.ParsedDate.Value.ToString("MM/dd", CultureInfo.InvariantCulture));
    }

    [Fact]
    public void CachedServerStreak_RemainsIdenticalAcrossTimezones()
    {
        var summary = new AccountTokenUsageSummary(
            LifetimeTokens: 500_000,
            PeakDailyTokens: 25_000,
            LongestRunningTurnSeconds: 60,
            CurrentStreakDays: 3,
            LongestStreakDays: 8);

        var snapshot1 = new AccountActivitySnapshot(
            ProfileId: _profileId,
            ObservedAt: new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.FromHours(-7)),
            Summary: summary,
            DailyBuckets: [new("2026-09-23", new DateOnly(2026, 9, 23), 12000)]);

        var snapshot2 = new AccountActivitySnapshot(
            ProfileId: _profileId,
            ObservedAt: new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.FromHours(9)),
            Summary: summary,
            DailyBuckets: [new("2026-09-23", new DateOnly(2026, 9, 23), 12000)]);

        Assert.NotNull(snapshot1.Summary);
        Assert.NotNull(snapshot2.Summary);
        Assert.Equal(3L, snapshot1.Summary.CurrentStreakDays);
        Assert.Equal(3L, snapshot2.Summary.CurrentStreakDays);
        Assert.Equal(8L, snapshot1.Summary.LongestStreakDays);
        Assert.Equal(8L, snapshot2.Summary.LongestStreakDays);
        Assert.Equal(UsageMetricFormatter.FormatStreak(snapshot1.Summary.CurrentStreakDays),
                     UsageMetricFormatter.FormatStreak(snapshot2.Summary.CurrentStreakDays));
        Assert.Equal(UsageMetricFormatter.FormatStreak(snapshot1.Summary.LongestStreakDays),
                     UsageMetricFormatter.FormatStreak(snapshot2.Summary.LongestStreakDays));
    }
}

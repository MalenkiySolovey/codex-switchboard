using System.Text.Json;
using CodexSwitcher.Core.Models;
using CodexSwitcher.Infra.Codex;

namespace CodexSwitcher.Core.Tests;

public sealed class AccountActivityParserTests
{
    private readonly Guid _profileId = Guid.NewGuid();
    private readonly DateTimeOffset _now = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Test1_CompleteResponse_ParsesAllSummaryMetricsAndBuckets()
    {
        var json = """
        {
            "summary": {
                "lifetimeTokens": 1133785860,
                "peakDailyTokens": 201820447,
                "longestRunningTurnSec": 6073,
                "currentStreakDays": 2,
                "longestStreakDays": 3
            },
            "dailyUsageBuckets": [
                { "startDate": "2026-07-11", "tokens": 57118364 },
                { "startDate": "2026-07-16", "tokens": 22379878 }
            ]
        }
        """;

        using var doc = JsonDocument.Parse(json);
        var snapshot = CodexUsageResponseParser.ParseAccountActivity(_profileId, doc.RootElement, _now);

        Assert.Equal(_profileId, snapshot.ProfileId);
        Assert.Equal(_now, snapshot.ObservedAt);
        Assert.NotNull(snapshot.Summary);
        Assert.Equal(1133785860L, snapshot.Summary.LifetimeTokens);
        Assert.Equal(201820447L, snapshot.Summary.PeakDailyTokens);
        Assert.Equal(6073L, snapshot.Summary.LongestRunningTurnSeconds);
        Assert.Equal(2L, snapshot.Summary.CurrentStreakDays);
        Assert.Equal(3L, snapshot.Summary.LongestStreakDays);

        Assert.Equal(2, snapshot.DailyBuckets.Count);
        Assert.Equal("2026-07-11", snapshot.DailyBuckets[0].StartDateRaw);
        Assert.Equal(new DateOnly(2026, 7, 11), snapshot.DailyBuckets[0].ParsedDate);
        Assert.Equal(57118364L, snapshot.DailyBuckets[0].Tokens);
    }

    [Fact]
    public void Test2_EveryNullableSummaryMetric_NullWhenMissingOrExplicitNull()
    {
        var json = """
        {
            "summary": {
                "lifetimeTokens": null,
                "peakDailyTokens": null,
                "longestRunningTurnSec": null,
                "currentStreakDays": null,
                "longestStreakDays": null
            },
            "dailyUsageBuckets": []
        }
        """;

        using var doc = JsonDocument.Parse(json);
        var snapshot = CodexUsageResponseParser.ParseAccountActivity(_profileId, doc.RootElement, _now);

        Assert.NotNull(snapshot.Summary);
        Assert.Null(snapshot.Summary.LifetimeTokens);
        Assert.Null(snapshot.Summary.PeakDailyTokens);
        Assert.Null(snapshot.Summary.LongestRunningTurnSeconds);
        Assert.Null(snapshot.Summary.CurrentStreakDays);
        Assert.Null(snapshot.Summary.LongestStreakDays);
    }

    [Fact]
    public void Test3_NullDailyUsageBuckets_ToleratedAsEmptyList()
    {
        var json = """
        {
            "summary": { "lifetimeTokens": 500 },
            "dailyUsageBuckets": null
        }
        """;

        using var doc = JsonDocument.Parse(json);
        var snapshot = CodexUsageResponseParser.ParseAccountActivity(_profileId, doc.RootElement, _now);

        Assert.NotNull(snapshot.Summary);
        Assert.Equal(500L, snapshot.Summary.LifetimeTokens);
        Assert.Empty(snapshot.DailyBuckets);
    }

    [Fact]
    public void Test4_EmptyDailyUsageBuckets_ReturnsEmptyList()
    {
        var json = """
        {
            "summary": { "lifetimeTokens": 100 },
            "dailyUsageBuckets": []
        }
        """;

        using var doc = JsonDocument.Parse(json);
        var snapshot = CodexUsageResponseParser.ParseAccountActivity(_profileId, doc.RootElement, _now);

        Assert.Empty(snapshot.DailyBuckets);
    }

    [Fact]
    public void Test5_UnknownAdditionalJsonFields_IgnoredSafely()
    {
        var json = """
        {
            "futureServerField": "ignored",
            "summary": {
                "lifetimeTokens": 1000,
                "futureMetric": 42
            },
            "dailyUsageBuckets": [
                { "startDate": "2026-08-01", "tokens": 500, "extra": "data" }
            ],
            "anotherObject": { "a": 1 }
        }
        """;

        using var doc = JsonDocument.Parse(json);
        var snapshot = CodexUsageResponseParser.ParseAccountActivity(_profileId, doc.RootElement, _now);

        Assert.NotNull(snapshot.Summary);
        Assert.Equal(1000L, snapshot.Summary.LifetimeTokens);
        Assert.Single(snapshot.DailyBuckets);
        Assert.Equal("2026-08-01", snapshot.DailyBuckets[0].StartDateRaw);
    }

    [Fact]
    public void Test6_MalformedOptionalSummaryValue_TreatedAsNull()
    {
        var json = """
        {
            "summary": {
                "lifetimeTokens": "not-a-number",
                "peakDailyTokens": [1, 2, 3],
                "longestRunningTurnSec": { "nested": true }
            }
        }
        """;

        using var doc = JsonDocument.Parse(json);
        var snapshot = CodexUsageResponseParser.ParseAccountActivity(_profileId, doc.RootElement, _now);

        Assert.NotNull(snapshot.Summary);
        Assert.Null(snapshot.Summary.LifetimeTokens);
        Assert.Null(snapshot.Summary.PeakDailyTokens);
        Assert.Null(snapshot.Summary.LongestRunningTurnSeconds);
    }

    [Fact]
    public void Test7_MalformedDate_PreservesRawStringAndLeavesParsedDateNull()
    {
        var json = """
        {
            "dailyUsageBuckets": [
                { "startDate": "invalid-date-format", "tokens": 1234 }
            ]
        }
        """;

        using var doc = JsonDocument.Parse(json);
        var snapshot = CodexUsageResponseParser.ParseAccountActivity(_profileId, doc.RootElement, _now);

        Assert.Single(snapshot.DailyBuckets);
        Assert.Equal("invalid-date-format", snapshot.DailyBuckets[0].StartDateRaw);
        Assert.Null(snapshot.DailyBuckets[0].ParsedDate);
        Assert.Equal(1234L, snapshot.DailyBuckets[0].Tokens);
    }

    [Fact]
    public void Test8_Int64ScaleTokenCount_ParsedWithoutOverflow()
    {
        var json = """
        {
            "summary": {
                "lifetimeTokens": 9000000000000000000
            },
            "dailyUsageBuckets": [
                { "startDate": "2026-08-10", "tokens": 8000000000000000000 }
            ]
        }
        """;

        using var doc = JsonDocument.Parse(json);
        var snapshot = CodexUsageResponseParser.ParseAccountActivity(_profileId, doc.RootElement, _now);

        Assert.NotNull(snapshot.Summary);
        Assert.Equal(9_000_000_000_000_000_000L, snapshot.Summary.LifetimeTokens);
        Assert.Equal(8_000_000_000_000_000_000L, snapshot.DailyBuckets[0].Tokens);
    }

    [Fact]
    public void Test9_ZeroTokenBucket_PreservedAccurately()
    {
        var json = """
        {
            "dailyUsageBuckets": [
                { "startDate": "2026-08-01", "tokens": 0 }
            ]
        }
        """;

        using var doc = JsonDocument.Parse(json);
        var snapshot = CodexUsageResponseParser.ParseAccountActivity(_profileId, doc.RootElement, _now);

        Assert.Single(snapshot.DailyBuckets);
        Assert.Equal(0L, snapshot.DailyBuckets[0].Tokens);
    }

    [Fact]
    public void Test10_OutOfOrderDates_SortedChronologicallyAscending()
    {
        var json = """
        {
            "dailyUsageBuckets": [
                { "startDate": "2026-08-20", "tokens": 300 },
                { "startDate": "2026-08-05", "tokens": 100 },
                { "startDate": "2026-08-12", "tokens": 200 }
            ]
        }
        """;

        using var doc = JsonDocument.Parse(json);
        var snapshot = CodexUsageResponseParser.ParseAccountActivity(_profileId, doc.RootElement, _now);

        Assert.Equal(3, snapshot.DailyBuckets.Count);
        Assert.Equal("2026-08-05", snapshot.DailyBuckets[0].StartDateRaw);
        Assert.Equal("2026-08-12", snapshot.DailyBuckets[1].StartDateRaw);
        Assert.Equal("2026-08-20", snapshot.DailyBuckets[2].StartDateRaw);
    }

    [Fact]
    public void Test11_DuplicateRawDates_PreservedInDeterministicOrder()
    {
        var json = """
        {
            "dailyUsageBuckets": [
                { "startDate": "2026-08-10", "tokens": 100 },
                { "startDate": "2026-08-10", "tokens": 200 }
            ]
        }
        """;

        using var doc = JsonDocument.Parse(json);
        var snapshot = CodexUsageResponseParser.ParseAccountActivity(_profileId, doc.RootElement, _now);

        Assert.Equal(2, snapshot.DailyBuckets.Count);
        Assert.Equal("2026-08-10", snapshot.DailyBuckets[0].StartDateRaw);
        Assert.Equal("2026-08-10", snapshot.DailyBuckets[1].StartDateRaw);
    }

    [Fact]
    public void Test12_SummaryMetrics_AreNotRecomputedFromBuckets()
    {
        // Server says lifetimeTokens = 10,000, even though buckets sum to 300.
        // Server summary must remain authoritative!
        var json = """
        {
            "summary": {
                "lifetimeTokens": 10000,
                "currentStreakDays": 5
            },
            "dailyUsageBuckets": [
                { "startDate": "2026-08-01", "tokens": 100 },
                { "startDate": "2026-08-02", "tokens": 200 }
            ]
        }
        """;

        using var doc = JsonDocument.Parse(json);
        var snapshot = CodexUsageResponseParser.ParseAccountActivity(_profileId, doc.RootElement, _now);

        Assert.NotNull(snapshot.Summary);
        Assert.Equal(10000L, snapshot.Summary.LifetimeTokens);
        Assert.Equal(5L, snapshot.Summary.CurrentStreakDays);
    }

    [Fact]
    public void Test13_MissingCurrentDayBucket_DoesNotBecomeZero()
    {
        // Buckets end on 2026-08-01. Parser must NOT synthesize 2026-09-10 with 0 tokens.
        var json = """
        {
            "dailyUsageBuckets": [
                { "startDate": "2026-08-01", "tokens": 150 }
            ]
        }
        """;

        using var doc = JsonDocument.Parse(json);
        var snapshot = CodexUsageResponseParser.ParseAccountActivity(_profileId, doc.RootElement, _now);

        Assert.Single(snapshot.DailyBuckets);
        Assert.Equal("2026-08-01", snapshot.DailyBuckets[0].StartDateRaw);
        Assert.DoesNotContain(snapshot.DailyBuckets, b => b.StartDateRaw == "2026-09-10");
    }

    [Fact]
    public void Test33_MissingDates_AreNotSynthesized()
    {
        var json = """
        {
            "dailyUsageBuckets": [
                { "startDate": "2026-07-01", "tokens": 100 },
                { "startDate": "2026-07-05", "tokens": 200 }
            ]
        }
        """;

        using var doc = JsonDocument.Parse(json);
        var snapshot = CodexUsageResponseParser.ParseAccountActivity(_profileId, doc.RootElement, _now);

        // Gap between 07-01 and 07-05 is NOT filled with 07-02, 07-03, 07-04
        Assert.Equal(2, snapshot.DailyBuckets.Count);
        Assert.Equal("2026-07-01", snapshot.DailyBuckets[0].StartDateRaw);
        Assert.Equal("2026-07-05", snapshot.DailyBuckets[1].StartDateRaw);
    }

    [Fact]
    public void Test34_ExactRawDatePreserved_WithoutTimeZoneShifting()
    {
        var json = """
        {
            "dailyUsageBuckets": [
                { "startDate": "2026-07-11", "tokens": 57118364 }
            ]
        }
        """;

        using var doc = JsonDocument.Parse(json);
        var snapshot = CodexUsageResponseParser.ParseAccountActivity(_profileId, doc.RootElement, _now);

        Assert.Equal("2026-07-11", snapshot.DailyBuckets[0].StartDateRaw);
    }
}

using CodexSwitcher.Core.Models;
using CodexSwitcher.Core.Services;

namespace CodexSwitcher.Core.Tests;

public sealed class UsagePresentationTests
{
    private readonly Guid _profileId = Guid.NewGuid();
    private readonly DateTimeOffset _now = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void MapFromCache_NullEntry_ReturnsNeverLoadedState()
    {
        var state = UsagePresentationMapper.MapFromCache(null, _profileId, _now);

        Assert.Equal(_profileId, state.ProfileId);
        Assert.Equal(UsageVisualState.NeverLoaded, state.VisualState);
        Assert.Equal(UsageStatus.Unknown, state.Status);
        Assert.False(state.IsStale);
        Assert.False(state.IsRefreshing);
        Assert.Empty(state.Windows);
        Assert.False(state.HasNotice);
    }

    [Fact]
    public void MapFromCache_EmptyUnknownEntry_ReturnsNeverLoadedState()
    {
        var entry = new UsageCacheEntry(
            ProfileId: _profileId,
            ObservedAt: _now,
            Snapshot: null,
            Status: UsageStatus.Unknown,
            LastError: null,
            IsStale: false);

        var state = UsagePresentationMapper.MapFromCache(entry, _profileId, _now);
        Assert.Equal(UsageVisualState.NeverLoaded, state.VisualState);
    }

    [Fact]
    public void MapFromCache_StaleEntry_ReturnsStaleCacheState()
    {
        var snapshot = new RateLimitsSnapshot(
            ProfileId: _profileId,
            ObservedAt: _now.AddHours(-1),
            PrimaryLimitId: "codex",
            Limits: [
                new LimitBucket("codex", "Codex", [
                    new UsageWindow("primary", 300, "5h", 25.0, 75.0, _now.AddHours(3))
                ], null, "plus")
            ],
            ResetCreditsAvailable: 0,
            PlanType: "plus",
            AccountEmail: "user@example.com",
            Status: UsageStatus.Healthy);

        var entry = new UsageCacheEntry(
            ProfileId: _profileId,
            ObservedAt: _now.AddHours(-1),
            Snapshot: snapshot,
            Status: UsageStatus.Healthy,
            LastError: null,
            IsStale: true);

        var state = UsagePresentationMapper.MapFromCache(entry, _profileId, _now);

        Assert.Equal(UsageVisualState.StaleCache, state.VisualState);
        Assert.True(state.IsStale);
        Assert.Single(state.Windows);
        Assert.Equal(75.0, state.Windows[0].RemainingPercent);
        Assert.Equal(25.0, state.Windows[0].UsedPercent);
    }

    [Fact]
    public void MapFromFetchResult_Healthy_ReturnsFreshState()
    {
        var snapshot = new RateLimitsSnapshot(
            ProfileId: _profileId,
            ObservedAt: _now,
            PrimaryLimitId: "codex",
            Limits: [
                new LimitBucket("codex", "Codex", [
                    new UsageWindow("primary", 300, "5h", 10.0, 90.0, _now.AddHours(4))
                ], null, "pro")
            ],
            ResetCreditsAvailable: 5,
            PlanType: "pro",
            AccountEmail: "pro@example.com",
            Status: UsageStatus.Healthy);

        var result = UsageFetchResult.Ok(snapshot);
        var state = UsagePresentationMapper.MapFromFetchResult(result, _profileId, _now);

        Assert.Equal(UsageVisualState.Fresh, state.VisualState);
        Assert.False(state.IsStale);
        Assert.Equal("pro", state.PlanType);
        Assert.Equal(5, state.ResetCreditsAvailable);
        Assert.Single(state.Windows);
    }

    [Fact]
    public void MapWindows_SortsByDurationAscending_ShortestFirst()
    {
        var raw = new List<UsageWindow>
        {
            new("weekly", 10080, "7d", 50.0, 50.0, _now.AddDays(5)),
            new("hourly", 60, "1h", 20.0, 80.0, _now.AddMinutes(30)),
            new("five_hour", 300, "5h", 10.0, 90.0, _now.AddHours(2)),
            new("sub_hour", 45, "45m", 5.0, 95.0, _now.AddMinutes(15)),
        };

        var mapped = UsagePresentationMapper.MapWindows(raw);

        Assert.Equal(4, mapped.Count);
        Assert.Equal(45, mapped[0].DurationMinutes);
        Assert.Equal(60, mapped[1].DurationMinutes);
        Assert.Equal(300, mapped[2].DurationMinutes);
        Assert.Equal(10080, mapped[3].DurationMinutes);
    }

    [Fact]
    public void MapWindows_PlacesNullDurationAtEnd()
    {
        var raw = new List<UsageWindow>
        {
            new("unspecified", null, "Custom Window", 10.0, 90.0, null),
            new("five_hour", 300, "5h", 20.0, 80.0, _now.AddHours(1)),
        };

        var mapped = UsagePresentationMapper.MapWindows(raw);

        Assert.Equal(2, mapped.Count);
        Assert.Equal(300, mapped[0].DurationMinutes);
        Assert.Null(mapped[1].DurationMinutes);
        Assert.Equal("Custom Window", mapped[1].DisplayLabel);
    }

    [Fact]
    public void MapWindows_ClampsPercentagesBetweenZeroAnd100()
    {
        var raw = new List<UsageWindow>
        {
            new("underflow", 300, "5h", -15.0, 120.0, null),
            new("overflow", 300, "5h", 150.0, -10.0, null),
        };

        var mapped = UsagePresentationMapper.MapWindows(raw);
        var underflow = mapped.Single(w => w.Slot == "underflow");
        var overflow = mapped.Single(w => w.Slot == "overflow");

        Assert.Equal(0.0, underflow.UsedPercent);
        Assert.Equal(100.0, underflow.RemainingPercent);

        Assert.Equal(100.0, overflow.UsedPercent);
        Assert.Equal(0.0, overflow.RemainingPercent);
        Assert.True(overflow.IsExhausted);
    }

    [Fact]
    public void MapWindows_EmptyList_ReturnsEmpty()
    {
        var mapped = UsagePresentationMapper.MapWindows(Array.Empty<UsageWindow>());
        Assert.Empty(mapped);
    }

    [Fact]
    public void MapWindows_IdentifiesLowQuotaWindow()
    {
        var raw = new List<UsageWindow>
        {
            new("low", 300, "5h", 85.0, 15.0, null),
            new("normal", 300, "5h", 50.0, 50.0, null),
            new("zero", 300, "5h", 100.0, 0.0, null),
        };

        var mapped = UsagePresentationMapper.MapWindows(raw);

        Assert.True(mapped[0].IsLowQuota);
        Assert.False(mapped[0].IsExhausted);

        Assert.False(mapped[1].IsLowQuota);
        Assert.False(mapped[1].IsExhausted);

        Assert.False(mapped[2].IsLowQuota); // 0 remaining is exhausted, not just low
        Assert.True(mapped[2].IsExhausted);
    }

    [Fact]
    public void ResolveVisualState_RateLimited_WhenUsageStatusIsRateLimited()
    {
        var snapshot = new RateLimitsSnapshot(
            ProfileId: _profileId,
            ObservedAt: _now,
            PrimaryLimitId: "codex",
            Limits: [new LimitBucket("codex", "Codex", [], null, "free")],
            ResetCreditsAvailable: 0,
            PlanType: "free",
            AccountEmail: "free@example.com",
            Status: UsageStatus.RateLimited);

        var state = UsagePresentationMapper.MapFromFetchResult(UsageFetchResult.Ok(snapshot), _profileId, _now);
        Assert.Equal(UsageVisualState.RateLimited, state.VisualState);
    }

    [Fact]
    public void ResolveVisualState_RateLimited_WhenBucketHasRateLimitReachedType()
    {
        var snapshot = new RateLimitsSnapshot(
            ProfileId: _profileId,
            ObservedAt: _now,
            PrimaryLimitId: "codex",
            Limits: [new LimitBucket("codex", "Codex", [], "exhausted", "plus")],
            ResetCreditsAvailable: 0,
            PlanType: "plus",
            AccountEmail: "plus@example.com",
            Status: UsageStatus.Healthy);

        var state = UsagePresentationMapper.MapFromFetchResult(UsageFetchResult.Ok(snapshot), _profileId, _now);
        Assert.Equal(UsageVisualState.RateLimited, state.VisualState);
    }

    [Fact]
    public void ResolveVisualState_WindowExhausted_WhenHealthyAndNoRateLimitType_DoesNotBecomeAccountRateLimited()
    {
        var snapshot = new RateLimitsSnapshot(
            ProfileId: _profileId,
            ObservedAt: _now,
            PrimaryLimitId: "codex",
            Limits: [
                new LimitBucket("codex", "Codex", [
                    new UsageWindow("primary", 300, "5h", 100.0, 0.0, _now.AddHours(1))
                ], null, "plus")
            ],
            ResetCreditsAvailable: 0,
            PlanType: "plus",
            AccountEmail: "plus@example.com",
            Status: UsageStatus.Healthy);

        var state = UsagePresentationMapper.MapFromFetchResult(UsageFetchResult.Ok(snapshot), _profileId, _now);

        // Window itself is visually exhausted
        Assert.Single(state.Windows);
        Assert.True(state.Windows[0].IsExhausted);
        Assert.Equal(0.0, state.Windows[0].RemainingPercent);
        Assert.Equal(100.0, state.Windows[0].UsedPercent);

        // But account-level status remains Fresh because backend did not confirm RateLimited
        Assert.Equal(UsageVisualState.Fresh, state.VisualState);
    }

    [Fact]
    public void ResolveVisualState_AuthRequired_ReturnsNoticeAndState()
    {
        var result = UsageFetchResult.Fail(UsageStatus.AuthRequired, ErrorInfo.Create(ErrorCategory.RefreshTokenExpired, "Auth expired", _now));
        var state = UsagePresentationMapper.MapFromFetchResult(result, _profileId, _now);

        Assert.Equal(UsageVisualState.AuthRequired, state.VisualState);
        Assert.True(state.HasNotice);
        Assert.Contains("Authentication required", state.NoticeMessage);
    }

    [Fact]
    public void ResolveVisualState_UnsupportedAccountType_ReturnsNoticeAndState()
    {
        var result = UsageFetchResult.Fail(UsageStatus.UnsupportedAccountType, ErrorInfo.Create(ErrorCategory.InvalidAuthFile, "API key", _now));
        var state = UsagePresentationMapper.MapFromFetchResult(result, _profileId, _now);

        Assert.Equal(UsageVisualState.UnsupportedAccountType, state.VisualState);
        Assert.True(state.HasNotice);
        Assert.Contains("not supported", state.NoticeMessage);
    }

    [Fact]
    public void ResolveVisualState_ProcessDownAndBackingOff_ReturnsProcessDownState()
    {
        var downResult = UsageFetchResult.Fail(UsageStatus.ProcessDown, ErrorInfo.Create(ErrorCategory.CodexNotFound, "Unavailable", _now));
        var downState = UsagePresentationMapper.MapFromFetchResult(downResult, _profileId, _now);

        Assert.Equal(UsageVisualState.ProcessDown, downState.VisualState);
        Assert.True(downState.HasNotice);

        var backoffResult = UsageFetchResult.Fail(UsageStatus.BackingOff, ErrorInfo.Create(ErrorCategory.Timeout, "Backing off", _now));
        var backoffState = UsagePresentationMapper.MapFromFetchResult(backoffResult, _profileId, _now);

        Assert.Equal(UsageVisualState.ProcessDown, backoffState.VisualState);
    }

    [Fact]
    public void ResolveVisualState_Error_ReturnsErrorMessageInNotice()
    {
        var result = UsageFetchResult.Fail(UsageStatus.Error, ErrorInfo.Create(ErrorCategory.Network, "Connection timeout", _now));
        var state = UsagePresentationMapper.MapFromFetchResult(result, _profileId, _now);

        Assert.Equal(UsageVisualState.ProcessDown, state.VisualState);
        Assert.True(state.HasNotice);
        Assert.Contains("Connection timeout", state.NoticeMessage);
    }

    [Fact]
    public void ResolveVisualState_CredentialConflict_TakesPriority()
    {
        var entry = new UsageCacheEntry(
            ProfileId: _profileId,
            ObservedAt: _now,
            Snapshot: null,
            Status: UsageStatus.Healthy,
            LastError: null,
            IsStale: false,
            CredentialConflict: true);

        var state = UsagePresentationMapper.MapFromCache(entry, _profileId, _now);

        Assert.Equal(UsageVisualState.CredentialConflict, state.VisualState);
        Assert.True(state.HasNotice);
        Assert.Contains("Credential conflict", state.NoticeMessage);
    }

    [Fact]
    public void AccountUsageState_AsRefreshing_PreservesWindowsAndSetsRefreshingFlag()
    {
        var baseState = new AccountUsageState(
            ProfileId: _profileId,
            Status: UsageStatus.Healthy,
            VisualState: UsageVisualState.Fresh,
            IsStale: false,
            IsRefreshing: false,
            PlanType: "team",
            ResetCreditsAvailable: 2,
            ObservedAt: _now,
            Windows: [new UsageWindowModel("primary", 300, "5h", 30.0, 70.0, null, false, false)],
            NoticeMessage: null);

        var refreshing = baseState.AsRefreshing();

        Assert.True(refreshing.IsRefreshing);
        Assert.Equal(UsageVisualState.Refreshing, refreshing.VisualState);
        Assert.Single(refreshing.Windows);
        Assert.Equal(70.0, refreshing.Windows[0].RemainingPercent);
    }

    [Fact]
    public void ZeroSecrets_VerifiedAcrossAllPresentationModels()
    {
        var snapshot = new RateLimitsSnapshot(
            ProfileId: _profileId,
            ObservedAt: _now,
            PrimaryLimitId: "codex",
            Limits: [new LimitBucket("codex", "Codex", [], null, "pro")],
            ResetCreditsAvailable: 0,
            PlanType: "pro",
            AccountEmail: "user@test.org",
            Status: UsageStatus.Healthy);

        var state = UsagePresentationMapper.MapFromFetchResult(UsageFetchResult.Ok(snapshot), _profileId, _now);

        // Verify string representation contains no token fields
        var serialized = state.ToString();
        Assert.DoesNotContain("access_token", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("refresh_token", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", serialized, StringComparison.OrdinalIgnoreCase);
    }
}

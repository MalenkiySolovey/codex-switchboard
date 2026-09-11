using System.Globalization;
using System.Text;
using System.Text.Json;
using CodexSwitcher.Core.Models;
using CodexSwitcher.Core.Security;
using CodexSwitcher.Core.Services;
using CodexSwitcher.Core.Support;

namespace CodexSwitcher.Core.Tests;

public sealed class SubscriptionAutoDetectionTests
{
    private static readonly CultureInfo EnCulture = CultureInfo.InvariantCulture;
    private static readonly CultureInfo PtCulture = new("pt-BR");

    private static string CreateDummyJwt(Dictionary<string, object> payloadClaims)
    {
        var header = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"alg\":\"none\",\"typ\":\"JWT\"}"))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var payloadJson = JsonSerializer.Serialize(payloadClaims);
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(payloadJson))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{header}.{payload}.";
    }

    [Fact]
    public void Test01_Extracts_ActiveUntil_And_ActiveStart_From_IdToken()
    {
        var jwt = CreateDummyJwt(new()
        {
            ["https://api.openai.com/auth"] = new Dictionary<string, object>
            {
                ["chatgpt_subscription_active_start"] = "2026-08-11T12:00:00Z",
                ["chatgpt_subscription_active_until"] = "2026-09-11T12:00:00Z",
                ["chatgpt_plan_type"] = "plus"
            }
        });

        var result = SubscriptionJwtClaimExtractor.ExtractFromJwt(
            jwtToken: jwt,
            now: new DateTimeOffset(2026, 9, 11, 0, 0, 0, TimeSpan.Zero));

        Assert.NotNull(result);
        Assert.Equal(new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero), result.ActiveStartUtc);
        Assert.Equal(new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero), result.ActiveUntilUtc);
        Assert.Equal(DetectedSubscriptionSource.OAuthTokenClaim, result.Source);
        Assert.Equal("plus", result.PlanType);
        Assert.False(result.IsStale);
    }

    [Fact]
    public void Test02_Strictly_Ignores_Exp_And_Iat_Invariants()
    {
        // A JWT with ONLY exp and iat and NO subscription claims must return null.
        // Invariant: exp is NEVER subscription expiry; iat is NEVER subscription start.
        var jwt = CreateDummyJwt(new()
        {
            ["exp"] = 1777777777,
            ["iat"] = 1777770000,
            ["sub"] = "user-12345"
        });

        var result = SubscriptionJwtClaimExtractor.ExtractFromJwt(
            jwtToken: jwt,
            now: DateTimeOffset.UtcNow);

        Assert.Null(result);
    }

    [Fact]
    public void Test03_ExtractFromAuthJson_Falls_Back_To_AccessToken_If_IdToken_Missing_Claims()
    {
        var idToken = CreateDummyJwt(new()
        {
            ["sub"] = "user-123"
        });

        var accessToken = CreateDummyJwt(new()
        {
            ["https://api.openai.com/auth"] = new Dictionary<string, object>
            {
                ["chatgpt_subscription_active_until"] = "2026-10-15T00:00:00Z",
                ["chatgpt_plan_type"] = "pro"
            }
        });

        var authJsonObj = new
        {
            tokens = new
            {
                id_token = idToken,
                access_token = accessToken
            }
        };
        var authJsonBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(authJsonObj));

        var result = SubscriptionJwtClaimExtractor.Extract(
            authJsonBytes: authJsonBytes,
            now: new DateTimeOffset(2026, 9, 11, 0, 0, 0, TimeSpan.Zero));

        Assert.NotNull(result);
        Assert.Equal(new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero), result.ActiveUntilUtc);
        Assert.Equal("pro", result.PlanType);
    }

    [Fact]
    public void Test04_Handles_Unix_Seconds_Timestamps()
    {
        // 1789084800 = 2026-09-12 00:00:00 UTC
        var jwt = CreateDummyJwt(new()
        {
            ["https://api.openai.com/auth"] = new Dictionary<string, object>
            {
                ["chatgpt_subscription_active_until"] = 1789084800L,
                ["chatgpt_plan_type"] = "team"
            }
        });

        var result = SubscriptionJwtClaimExtractor.ExtractFromJwt(
            jwtToken: jwt,
            now: new DateTimeOffset(2026, 9, 11, 0, 0, 0, TimeSpan.Zero));

        Assert.NotNull(result);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1789084800L), result.ActiveUntilUtc);
    }

    [Fact]
    public void Test05_Estimates_Monthly_When_Only_ActiveStart_On_Known_Monthly_Plan()
    {
        // Started 2026-08-15; today is 2026-09-11; next boundary should be estimated to 2026-09-15
        var jwt = CreateDummyJwt(new()
        {
            ["https://api.openai.com/auth"] = new Dictionary<string, object>
            {
                ["chatgpt_subscription_active_start"] = "2026-08-15T00:00:00Z",
                ["chatgpt_plan_type"] = "plus"
            }
        });

        var now = new DateTimeOffset(2026, 9, 11, 0, 0, 0, TimeSpan.Zero);
        var result = SubscriptionJwtClaimExtractor.ExtractFromJwt(
            jwtToken: jwt,
            now: now);

        Assert.NotNull(result);
        Assert.Equal(new DateTimeOffset(2026, 8, 15, 0, 0, 0, TimeSpan.Zero), result.ActiveStartUtc);
        Assert.Equal(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero), result.ActiveUntilUtc);
        Assert.Equal(DetectedSubscriptionSource.EstimatedFromStart, result.Source);
    }

    [Fact]
    public void Test06_Does_Not_Estimate_When_Free_Plan()
    {
        var jwt = CreateDummyJwt(new()
        {
            ["https://api.openai.com/auth"] = new Dictionary<string, object>
            {
                ["chatgpt_subscription_active_start"] = "2026-08-15T00:00:00Z",
                ["chatgpt_plan_type"] = "free"
            }
        });

        var now = new DateTimeOffset(2026, 9, 11, 0, 0, 0, TimeSpan.Zero);
        var result = SubscriptionJwtClaimExtractor.ExtractFromJwt(
            jwtToken: jwt,
            now: now);

        // Free plans without until date do not produce an estimated subscription
        Assert.Null(result);
    }

    [Fact]
    public void Test07_PresentationResolver_ManualTracking_Dominates()
    {
        var manual = new SubscriptionTracking(
            StartedOn: new DateOnly(2026, 8, 1),
            NextRenewalOrExpiryOn: new DateOnly(2026, 10, 1),
            Mode: SubscriptionTrackingMode.Renewal,
            Source: SubscriptionDateSource.UserProvided);

        var detected = new DetectedSubscriptionInfo(
            ActiveStartUtc: new DateTimeOffset(2026, 8, 11, 0, 0, 0, TimeSpan.Zero),
            ActiveUntilUtc: new DateTimeOffset(2026, 9, 11, 0, 0, 0, TimeSpan.Zero),
            ObservedAtUtc: DateTimeOffset.UtcNow,
            PlanType: "plus",
            Source: DetectedSubscriptionSource.OAuthTokenClaim,
            IsStale: false);

        var today = new DateOnly(2026, 9, 11);

        var resultPt = SubscriptionPresentationResolver.Resolve(manual, detected, "plus", today, PtCulture, pt: true);
        var resultEn = SubscriptionPresentationResolver.Resolve(manual, detected, "plus", today, EnCulture, pt: false);

        // Manual tracking takes precedence
        Assert.Contains("renova em", resultPt.DisplayText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("renews", resultEn.DisplayText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1", resultPt.DisplayText);
        Assert.Contains("Rastreamento local", resultPt.TooltipText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("User-provided", resultEn.TooltipText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Test08_PresentationResolver_DetectedSubscription_Neutral_Phrasing()
    {
        var detected = new DetectedSubscriptionInfo(
            ActiveStartUtc: null,
            ActiveUntilUtc: new DateTimeOffset(2026, 9, 25, 0, 0, 0, TimeSpan.Zero),
            ObservedAtUtc: DateTimeOffset.UtcNow,
            PlanType: "plus",
            Source: DetectedSubscriptionSource.OAuthTokenClaim,
            IsStale: false);

        var today = new DateOnly(2026, 9, 11);

        var resultPt = SubscriptionPresentationResolver.Resolve(null, detected, "plus", today, PtCulture, pt: true);
        var resultEn = SubscriptionPresentationResolver.Resolve(null, detected, "plus", today, EnCulture, pt: false);

        // Neutral phrasing: "período até..." / "period until..."
        Assert.Contains("período até", resultPt.DisplayText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("period until", resultEn.DisplayText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("25", resultPt.DisplayText);
        Assert.Contains("Codex OAuth", resultPt.TooltipText);
        Assert.Contains("Codex OAuth", resultEn.TooltipText);
    }

    [Fact]
    public void Test09_PresentationResolver_Stale_Paid_Plan_Graceful_Tooltip()
    {
        // Detected until 2026-09-01 (in the past relative to today 2026-09-11)
        var detected = new DetectedSubscriptionInfo(
            ActiveStartUtc: null,
            ActiveUntilUtc: new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
            ObservedAtUtc: new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
            PlanType: "plus",
            Source: DetectedSubscriptionSource.OAuthTokenClaim,
            IsStale: true);

        var today = new DateOnly(2026, 9, 11);

        var resultPt = SubscriptionPresentationResolver.Resolve(null, detected, "plus", today, PtCulture, pt: true);

        // Must NOT state definitive expired
        Assert.DoesNotContain("Expirada", resultPt.DisplayText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("período até", resultPt.DisplayText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("desatualizada", resultPt.TooltipText?.ToLowerInvariant() ?? "");
    }

    [Fact]
    public void Test10_InitialCache_Provides_Immediate_Compact_Reset_Countdown_Without_Toggle()
    {
        var profileId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 9, 11, 10, 0, 0, TimeSpan.Zero);
        var resetAt = now.AddHours(4).AddMinutes(30);

        var window = new UsageWindow("primary", 300, "5h", 18.0, 82.0, resetAt);
        var bucket = new LimitBucket("codex", "Codex", [window], null, "plus");

        var snapshot = new RateLimitsSnapshot(
            ProfileId: profileId,
            ObservedAt: now,
            PrimaryLimitId: "codex",
            Limits: [bucket],
            ResetCreditsAvailable: null,
            PlanType: "plus",
            AccountEmail: "test@example.com",
            Status: UsageStatus.Healthy);

        var entry = new UsageCacheEntry(
            ProfileId: profileId,
            ObservedAt: now,
            Snapshot: snapshot,
            Status: UsageStatus.Healthy,
            LastError: null,
            IsStale: true);

        // Mapping from cache on initial startup
        var presentation = UsagePresentationMapper.MapFromCache(entry, profileId, now);

        Assert.NotEmpty(presentation.Windows);
        var primaryWindow = presentation.Windows[0];
        Assert.Equal("5h", primaryWindow.DisplayLabel);
        Assert.Equal(resetAt, primaryWindow.ResetsAt);

        // Formatting compact reset string immediately available
        var formattedCompact = UsageCountdownFormatter.FormatCompactCountdown(primaryWindow.ResetsAt, now, pt: false);
        Assert.Equal("4h 30m", formattedCompact);
    }
}

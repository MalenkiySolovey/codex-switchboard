using System.Globalization;
using CodexSwitcher.Core.Abstractions;
using CodexSwitcher.Core.Models;
using CodexSwitcher.Core.Services;
using CodexSwitcher.Core.Support;
using CodexSwitcher.Core.Tests.TestSupport;
using CodexSwitcher.Infra;
using CodexSwitcher.Infra.Io;
using CodexSwitcher.Infra.Security;

namespace CodexSwitcher.Core.Tests;

public sealed class SubscriptionTrackingTests : IDisposable
{
    private static readonly CultureInfo EnCulture = CultureInfo.InvariantCulture;
    private static readonly CultureInfo PtCulture = new("pt-BR");
    private readonly TempDir _dir = new();
    private readonly PhysicalFileSystem _fs = new();
    private readonly AppPaths _paths;

    public SubscriptionTrackingTests()
    {
        _paths = new AppPaths(_dir.Combine(".codex-switcher"), _dir.Combine(".codex"));
    }

    public void Dispose()
    {
        _dir.Dispose();
    }

    [Fact]
    public void Test01_ModelDefaults_InitializedProperly()
    {
        var tracking = new SubscriptionTracking(
            StartedOn: new DateOnly(2026, 8, 27),
            NextRenewalOrExpiryOn: new DateOnly(2026, 9, 27));

        Assert.Equal(SubscriptionTrackingMode.Renewal, tracking.Mode);
        Assert.Equal(SubscriptionDateSource.UserProvided, tracking.Source);
        Assert.False(tracking.IsEstimated);
        Assert.True(tracking.HasTargetDate);
    }

    [Fact]
    public void Test02_EstimateNextMonthlyRenewal_EarlierThisMonth()
    {
        // Started on the 5th; today is Sept 10 (already passed 5th of this month) -> next renewal is Oct 5
        var started = new DateOnly(2026, 8, 5);
        var today = new DateOnly(2026, 9, 10);
        var estimated = SubscriptionTracking.EstimateNextMonthlyRenewal(started, today);

        Assert.Equal(new DateOnly(2026, 10, 5), estimated);
    }

    [Fact]
    public void Test03_EstimateNextMonthlyRenewal_LaterThisMonth()
    {
        // Started on the 27th; today is Sept 10 (not yet reached 27th) -> next renewal is Sept 27
        var started = new DateOnly(2026, 8, 27);
        var today = new DateOnly(2026, 9, 10);
        var estimated = SubscriptionTracking.EstimateNextMonthlyRenewal(started, today);

        Assert.Equal(new DateOnly(2026, 9, 27), estimated);
    }

    [Fact]
    public void Test04_EstimateNextMonthlyRenewal_EndOfMonthClamping()
    {
        // Started on Jan 31; today is Feb 1, 2026 -> clamped to Feb 28, 2026
        var started = new DateOnly(2026, 1, 31);
        var today = new DateOnly(2026, 2, 1);
        var estimated = SubscriptionTracking.EstimateNextMonthlyRenewal(started, today);

        Assert.Equal(new DateOnly(2026, 2, 28), estimated);
    }

    [Fact]
    public void Test05_EstimateNextMonthlyRenewal_YearRollover()
    {
        // Started on Dec 15; today is Dec 20, 2026 -> rolls over to Jan 15, 2027
        var started = new DateOnly(2026, 12, 15);
        var today = new DateOnly(2026, 12, 20);
        var estimated = SubscriptionTracking.EstimateNextMonthlyRenewal(started, today);

        Assert.Equal(new DateOnly(2027, 1, 15), estimated);
    }

    [Fact]
    public void Test06_FormatDisplayText_RenewalFuture()
    {
        var target = new DateOnly(2026, 9, 27);
        var today = new DateOnly(2026, 9, 10);
        var tracking = new SubscriptionTracking(null, target, SubscriptionTrackingMode.Renewal);

        var display = SubscriptionFormatter.FormatDisplayText(tracking, today, EnCulture, pt: false);
        Assert.Equal("• renews Sep 27", display);
    }

    [Fact]
    public void Test07_FormatDisplayText_RenewalPast()
    {
        var target = new DateOnly(2026, 9, 5);
        var today = new DateOnly(2026, 9, 10);
        var tracking = new SubscriptionTracking(null, target, SubscriptionTrackingMode.Renewal);

        var display = SubscriptionFormatter.FormatDisplayText(tracking, today, EnCulture, pt: false);
        Assert.Equal("• renewal was Sep 5", display);
    }

    [Fact]
    public void Test08_FormatDisplayText_ExpirationFuture()
    {
        var target = new DateOnly(2026, 9, 27);
        var today = new DateOnly(2026, 9, 10);
        var tracking = new SubscriptionTracking(null, target, SubscriptionTrackingMode.Expiration);

        var display = SubscriptionFormatter.FormatDisplayText(tracking, today, EnCulture, pt: false);
        Assert.Equal("• expires Sep 27", display);
    }

    [Fact]
    public void Test09_FormatDisplayText_ExpirationPast()
    {
        var target = new DateOnly(2026, 9, 5);
        var today = new DateOnly(2026, 9, 10);
        var tracking = new SubscriptionTracking(null, target, SubscriptionTrackingMode.Expiration);

        var display = SubscriptionFormatter.FormatDisplayText(tracking, today, EnCulture, pt: false);
        Assert.Equal("• expired Sep 5", display);
    }

    [Fact]
    public void Test10_FormatDisplayText_EstimatedNotice()
    {
        var target = new DateOnly(2026, 9, 27);
        var today = new DateOnly(2026, 9, 10);
        var tracking = new SubscriptionTracking(null, target, SubscriptionTrackingMode.Renewal, IsEstimated: true);

        var display = SubscriptionFormatter.FormatDisplayText(tracking, today, EnCulture, pt: false);
        Assert.Equal("• renews Sep 27 (est.)", display);
    }

    [Fact]
    public void Test11_FormatTooltipText_ContainsUserProvidedDisclaimer()
    {
        var target = new DateOnly(2026, 9, 27);
        var today = new DateOnly(2026, 9, 10);
        var tracking = new SubscriptionTracking(new DateOnly(2026, 8, 27), target, SubscriptionTrackingMode.Renewal);

        var tooltip = SubscriptionFormatter.FormatTooltipText(tracking, today, EnCulture, pt: false);
        Assert.Contains("User-provided local tracking", tooltip);
        Assert.Contains("September 27, 2026", tooltip);
        Assert.Contains("Started: August 27, 2026", tooltip);
        Assert.Contains("chatgpt.com", tooltip);
    }

    [Fact]
    public void Test12_ForbiddenInferences_NeverDerivedFromJwtOrProfileDates()
    {
        // Ensure default ProfileMetadata has null SubscriptionTracking regardless of CreatedAt or claims
        var profile = new ProfileMetadata
        {
            Nickname = "Test Account",
            CreatedAt = DateTimeOffset.UtcNow.AddMonths(-3),
            LastRefreshedAt = DateTimeOffset.UtcNow.AddDays(-1),
            PlanType = "plus"
        };

        Assert.Null(profile.SubscriptionTracking);
    }

    [Fact]
    public void Test13_Persistence_ProfileStoreRoundTrip()
    {
        var store = new ProfileStore(_fs, _dir.Combine("profiles.json"));
        var id = Guid.NewGuid();
        var profile = new ProfileMetadata
        {
            Id = id,
            Nickname = "Subscribed Profile",
            PlanType = "plus",
            SubscriptionTracking = new SubscriptionTracking(
                new DateOnly(2026, 8, 27),
                new DateOnly(2026, 9, 27),
                SubscriptionTrackingMode.Renewal,
                SubscriptionDateSource.UserProvided,
                IsEstimated: true)
        };

        store.SaveAll([profile]);

        var loaded = store.LoadAll();
        Assert.Single(loaded);
        Assert.NotNull(loaded[0].SubscriptionTracking);
        var loadedTracking = loaded[0].SubscriptionTracking!;
        Assert.Equal(new DateOnly(2026, 8, 27), loadedTracking.StartedOn);
        Assert.Equal(new DateOnly(2026, 9, 27), loadedTracking.NextRenewalOrExpiryOn);
        Assert.Equal(SubscriptionTrackingMode.Renewal, loadedTracking.Mode);
        Assert.True(loadedTracking.IsEstimated);
    }

    [Fact]
    public void Test14_ProfileService_UpdateAndClearSubscriptionTracking()
    {
        var store = new ProfileStore(_fs, _dir.Combine("profiles.json"));
        var vault = new VaultService(new DpapiSecretProtector(), _fs, _dir.Combine("vault"));
        var paths = CodexPaths.ForHome(_dir.Combine(".codex"));
        var recon = new ReconciliationService(_fs, paths);
        var service = new ProfileService(vault, store, recon, _fs, paths, new SystemClock(), new FakeAudit());

        var id = Guid.NewGuid();
        var profile = new ProfileMetadata { Id = id, Nickname = "Work" };
        service.Profiles.Add(profile);
        store.SaveAll(service.Profiles);

        var tracking = new SubscriptionTracking(null, new DateOnly(2026, 10, 1), SubscriptionTrackingMode.Renewal);
        service.UpdateSubscriptionTracking(id, tracking);

        Assert.NotNull(profile.SubscriptionTracking);
        Assert.Equal(new DateOnly(2026, 10, 1), profile.SubscriptionTracking.NextRenewalOrExpiryOn);

        // Clear tracking
        service.UpdateSubscriptionTracking(id, null);
        Assert.Null(profile.SubscriptionTracking);
    }

    [Fact]
    public void Test15_Localization_PortugueseFormatting()
    {
        var target = new DateOnly(2026, 9, 27);
        var today = new DateOnly(2026, 9, 10);
        var tracking = new SubscriptionTracking(null, target, SubscriptionTrackingMode.Renewal, IsEstimated: true);

        var displayPt = SubscriptionFormatter.FormatDisplayText(tracking, today, PtCulture, pt: true);
        Assert.StartsWith("• renova em", displayPt);
        Assert.EndsWith("(est.)", displayPt);

        var tooltipPt = SubscriptionFormatter.FormatTooltipText(tracking, today, PtCulture, pt: true);
        Assert.Contains("Rastreamento local fornecido pelo usuário", tooltipPt);
        Assert.Contains("renovação", tooltipPt);
        Assert.Contains("chatgpt.com", tooltipPt);
    }
}

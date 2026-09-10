using System.Globalization;
using CodexSwitcher.Core.Models;
using CodexSwitcher.Core.Support;

namespace CodexSwitcher.Core.Tests;

public sealed class QuotaResetTimestampTests
{
    private static readonly CultureInfo EnCulture = CultureInfo.InvariantCulture;
    private static readonly CultureInfo PtCulture = new("pt-BR");

    [Fact]
    public void Test01_ResetsAt_Null_ProducesDash()
    {
        var now = DateTimeOffset.UtcNow;
        var formatted = UsageCountdownFormatter.FormatCountdownWithAbsolute(null, now, EnCulture, pt: false);
        Assert.Equal("-", formatted);

        var tooltip = UsageCountdownFormatter.FormatFullResetTooltip(null, EnCulture, pt: false);
        Assert.Equal("Reset time: unknown", tooltip);

        var tooltipPt = UsageCountdownFormatter.FormatFullResetTooltip(null, PtCulture, pt: true);
        Assert.Equal("Horário de reinício: desconhecido", tooltipPt);
    }

    [Fact]
    public void Test02_ResetsAt_Due_ProducesResetDue_WithConciseTimestamp()
    {
        var now = new DateTimeOffset(2026, 9, 10, 14, 0, 0, TimeSpan.Zero);
        var past = now.AddMinutes(-10);

        var en = UsageCountdownFormatter.FormatCountdownWithAbsolute(past, now, EnCulture, pt: false);
        var localPast = past.ToLocalTime();
        var expectedConcise = localPast.ToString("MMM d, HH:mm", EnCulture);
        Assert.Equal($"Reset due — refresh to verify ({expectedConcise})", en);

        var pt = UsageCountdownFormatter.FormatCountdownWithAbsolute(past, now, PtCulture, pt: true);
        var expectedConcisePt = localPast.ToString("MMM d, HH:mm", PtCulture);
        Assert.Equal($"Reset pendente — renove para atualizar ({expectedConcisePt})", pt);
    }

    [Fact]
    public void Test03_ResetsAt_Future_ProducesRelativeCountdown_AndConciseTimestamp()
    {
        var now = new DateTimeOffset(2026, 9, 10, 11, 8, 0, TimeSpan.Zero);
        var future = now.AddHours(3).AddMinutes(18);

        var en = UsageCountdownFormatter.FormatCountdownWithAbsolute(future, now, EnCulture, pt: false);
        var localFuture = future.ToLocalTime();
        var expectedConcise = localFuture.ToString("MMM d, HH:mm", EnCulture);
        Assert.Equal($"Resets in 3h 18m ({expectedConcise})", en);
    }

    [Fact]
    public void Test04_ResetsAt_FullTooltip_IncludesSecondsAndLocalTime()
    {
        var reset = new DateTimeOffset(2026, 9, 10, 14, 26, 30, TimeSpan.Zero);
        var local = reset.ToLocalTime();

        var tooltipEn = UsageCountdownFormatter.FormatFullResetTooltip(reset, EnCulture, pt: false);
        Assert.Contains(local.ToString("HH:mm:ss"), tooltipEn);
        Assert.Contains("2026", tooltipEn);
        Assert.EndsWith("(local time)", tooltipEn);
    }

    [Fact]
    public void Test05_ResetsAt_PortugueseLocalization()
    {
        var now = new DateTimeOffset(2026, 9, 10, 10, 0, 0, TimeSpan.Zero);
        var future = now.AddHours(5).AddMinutes(30);
        var local = future.ToLocalTime();

        var pt = UsageCountdownFormatter.FormatCountdownWithAbsolute(future, now, PtCulture, pt: true);
        Assert.StartsWith("Reinicia em 5h 30m (", pt);

        var tooltipPt = UsageCountdownFormatter.FormatFullResetTooltip(future, PtCulture, pt: true);
        Assert.Contains(local.ToString("HH:mm:ss"), tooltipPt);
        Assert.EndsWith("(horário local)", tooltipPt);
    }

    [Fact]
    public void Test06_ResetsAt_PreservesOriginalDateTimeOffset()
    {
        var reset = new DateTimeOffset(2026, 9, 10, 17, 45, 0, TimeSpan.FromHours(-3));
        var window = new UsageWindow("primary", 300, "5h", 25.0, 75.0, reset);
        var model = new UsageWindowModel("primary", 300, "5h", 25.0, 75.0, reset, false, false);

        Assert.Equal(reset, window.ResetsAt);
        Assert.Equal(reset, model.ResetsAt);
        Assert.True(window.ResetsAt.HasValue);
        Assert.Equal(TimeSpan.FromHours(-3), window.ResetsAt!.Value.Offset);
    }

    [Fact]
    public void Test07_ResetsAt_InvalidOrPastTimestamp_DoesNotThrow()
    {
        var now = DateTimeOffset.UtcNow;
        var ancient = DateTimeOffset.UnixEpoch;

        var result = UsageCountdownFormatter.FormatCountdownWithAbsolute(ancient, now, EnCulture, pt: false);
        Assert.Contains("Reset due", result);

        var tooltip = UsageCountdownFormatter.FormatFullResetTooltip(ancient, EnCulture, pt: false);
        Assert.Contains("1970", tooltip);
    }
}

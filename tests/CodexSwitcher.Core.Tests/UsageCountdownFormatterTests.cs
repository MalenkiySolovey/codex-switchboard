using CodexSwitcher.Core.Support;

namespace CodexSwitcher.Core.Tests;

public sealed class UsageCountdownFormatterTests
{
    [Fact]
    public void FormatCountdown_ReturnsDash_WhenResetsAtIsNull()
    {
        var now = DateTimeOffset.UtcNow;
        var formatted = UsageCountdownFormatter.FormatCountdown(null, now);
        Assert.Equal("-", formatted);
    }

    [Fact]
    public void FormatCountdown_ReturnsResetDue_WhenResetsAtIsPast()
    {
        var now = DateTimeOffset.UtcNow;
        var past = now.AddMinutes(-5);

        var en = UsageCountdownFormatter.FormatCountdown(past, now, pt: false);
        var pt = UsageCountdownFormatter.FormatCountdown(past, now, pt: true);

        Assert.Equal("Reset due — refresh to verify", en);
        Assert.Equal("Reset pendente — renove para atualizar", pt);
    }

    [Fact]
    public void FormatCountdown_ReturnsResetDue_WhenResetsAtIsExactlyNow()
    {
        var now = DateTimeOffset.UtcNow;
        var en = UsageCountdownFormatter.FormatCountdown(now, now, pt: false);
        Assert.Equal("Reset due — refresh to verify", en);
    }

    [Fact]
    public void FormatCountdown_FormatsMinutes_WhenUnderOneHour()
    {
        var now = DateTimeOffset.UtcNow;
        var target = now.AddMinutes(45).AddSeconds(10);

        var en = UsageCountdownFormatter.FormatCountdown(target, now, pt: false);
        var pt = UsageCountdownFormatter.FormatCountdown(target, now, pt: true);

        Assert.Equal("Resets in 45m", en);
        Assert.Equal("Reinicia em 45m", pt);
    }

    [Fact]
    public void FormatCountdown_FormatsHoursAndMinutes_WhenUnderOneDay()
    {
        var now = DateTimeOffset.UtcNow;
        var target = now.AddHours(4).AddMinutes(32).AddSeconds(15);

        var en = UsageCountdownFormatter.FormatCountdown(target, now, pt: false);
        var pt = UsageCountdownFormatter.FormatCountdown(target, now, pt: true);

        Assert.Equal("Resets in 4h 32m", en);
        Assert.Equal("Reinicia em 4h 32m", pt);
    }

    [Fact]
    public void FormatCountdown_FormatsDaysAndHours_WhenOverOneDay()
    {
        var now = DateTimeOffset.UtcNow;
        var target = now.AddDays(2).AddHours(5).AddMinutes(12);

        var en = UsageCountdownFormatter.FormatCountdown(target, now, pt: false);
        var pt = UsageCountdownFormatter.FormatCountdown(target, now, pt: true);

        Assert.Equal("Resets in 2d 5h", en);
        Assert.Equal("Reinicia em 2d 5h", pt);
    }

    [Fact]
    public void FormatCountdown_FormatsSeconds_WhenUnderOneMinute()
    {
        var now = DateTimeOffset.UtcNow;
        var target = now.AddSeconds(30);

        var en = UsageCountdownFormatter.FormatCountdown(target, now, pt: false);
        var pt = UsageCountdownFormatter.FormatCountdown(target, now, pt: true);

        Assert.Equal("Resets in 30s", en);
        Assert.Equal("Reinicia em 30s", pt);
    }

    [Fact]
    public void FormatAbsoluteReset_ReturnsLocalizedNotice()
    {
        var date = new DateTimeOffset(2026, 9, 10, 15, 30, 0, TimeSpan.Zero);

        var en = UsageCountdownFormatter.FormatAbsoluteReset(date, pt: false);
        var pt = UsageCountdownFormatter.FormatAbsoluteReset(date, pt: true);

        Assert.StartsWith("Scheduled reset:", en);
        Assert.StartsWith("Reinício programado:", pt);

        var nullEn = UsageCountdownFormatter.FormatAbsoluteReset(null, pt: false);
        Assert.Equal("Reset time: unknown", nullEn);
    }
}

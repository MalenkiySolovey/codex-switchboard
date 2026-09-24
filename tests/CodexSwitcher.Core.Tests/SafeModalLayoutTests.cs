using CodexSwitcher.Core.Common.Layout;

namespace CodexSwitcher.Core.Tests;

public sealed class SafeModalLayoutTests
{
    [Theory]
    [InlineData(1280d, 720d, 880d, 672d, 480d)]
    [InlineData(1920d, 1080d, 880d, 1000d, 760d)]
    [InlineData(2560d, 1440d, 880d, 1000d, 760d)]
    public void Calculate_UsesWideEditorWithinTargetViewport(
        double hostWidth,
        double hostHeight,
        double expectedWidth,
        double expectedMaxHeight,
        double expectedContentMaxHeight)
    {
        var layout = SafeModalLayoutCalculator.Calculate(hostWidth, hostHeight);

        Assert.Equal(expectedWidth, layout.Width);
        Assert.Equal(320, layout.MinWidth);
        Assert.Equal(expectedMaxHeight, layout.MaxHeight);
        Assert.Equal(expectedContentMaxHeight, layout.ContentMaxHeight);
        Assert.True(layout.Width <= hostWidth - 48);
        Assert.True(layout.MaxHeight <= hostHeight - 48);
    }

    [Fact]
    public void Calculate_ShrinksToFitSmallHostWithSafeMargins()
    {
        var layout = SafeModalLayoutCalculator.Calculate(400, 300);

        Assert.Equal(352, layout.Width);
        Assert.Equal(320, layout.MinWidth);
        Assert.Equal(252, layout.MaxHeight);
        Assert.Equal(60, layout.ContentMaxHeight);
    }
}

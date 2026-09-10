namespace CodexSwitcher.App.ViewModels;

/// <summary>
/// Represents a single activity bar in the compact daily token activity chart.
/// </summary>
public sealed class ActivityBarItemViewModel
{
    public string StartDateRaw { get; }
    public string DisplayDate { get; }
    public long Tokens { get; }
    public string FormattedTokens { get; }
    public double BarHeight { get; }
    public string TooltipText { get; }

    public ActivityBarItemViewModel(
        string startDateRaw,
        string displayDate,
        long tokens,
        string formattedTokens,
        double barHeight,
        string tooltipText)
    {
        StartDateRaw = startDateRaw;
        DisplayDate = displayDate;
        Tokens = tokens;
        FormattedTokens = formattedTokens;
        BarHeight = barHeight;
        TooltipText = tooltipText;
    }
}

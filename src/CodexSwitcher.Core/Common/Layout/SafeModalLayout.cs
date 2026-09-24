namespace CodexSwitcher.Core.Common.Layout;

/// <summary>
/// Framework-agnostic bounds for a centered modal editor within its host viewport.
/// The UI applies these bounds only to the editor that needs the extra space.
/// </summary>
public readonly record struct SafeModalLayout(
    double Width,
    double MinWidth,
    double MaxHeight,
    double ContentMaxHeight);

public static class SafeModalLayoutCalculator
{
    private const double PreferredWidth = 880;
    private const double MinimumWidth = 320;
    private const double HorizontalMargin = 48;
    private const double MaximumHeight = 1000;
    private const double VerticalMargin = 48;
    private const double EditorChromeHeight = 192;
    private const double MaximumContentHeight = 760;

    public static SafeModalLayout Calculate(double hostWidth, double hostHeight)
    {
        if (!double.IsFinite(hostWidth) || hostWidth <= 0) hostWidth = 1024;
        if (!double.IsFinite(hostHeight) || hostHeight <= 0) hostHeight = 800;

        var width = Math.Min(PreferredWidth, Math.Max(0, hostWidth - HorizontalMargin));
        var maxHeight = Math.Min(MaximumHeight, Math.Max(0, hostHeight - VerticalMargin));
        var contentMaxHeight = Math.Min(MaximumContentHeight, Math.Max(0, maxHeight - EditorChromeHeight));

        return new SafeModalLayout(
            Width: width,
            MinWidth: Math.Min(MinimumWidth, width),
            MaxHeight: maxHeight,
            ContentMaxHeight: contentMaxHeight);
    }
}

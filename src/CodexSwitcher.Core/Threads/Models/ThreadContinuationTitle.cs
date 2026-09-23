namespace CodexSwitcher.Core.Threads.Models;

/// <summary>Builds a stable, user-visible title for a persistent copied thread.</summary>
public static class ThreadContinuationTitle
{
    private const string CopySuffix = "[copy]";

    public static string CreateCopyTitle(string? explicitSourceName, string? displayedSourceTitle)
    {
        var sourceTitle = !string.IsNullOrWhiteSpace(explicitSourceName)
            ? explicitSourceName
            : displayedSourceTitle;
        var title = sourceTitle?.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            title = "Continuation";
        }

        // Each new fork gets its own marker. A source that is itself a copy
        // can therefore correctly produce a title ending in "[copy] [copy]".
        return $"{title} {CopySuffix}";
    }

    public static string CreateVerifiedNameLine(string? name, bool updateSucceeded)
    {
        return updateSucceeded && !string.IsNullOrWhiteSpace(name)
            ? $"Name: {name}\n"
            : string.Empty;
    }
}

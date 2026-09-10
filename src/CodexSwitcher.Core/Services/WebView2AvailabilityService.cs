using CodexSwitcher.Core.Abstractions;

namespace CodexSwitcher.Core.Services;

/// <summary>
/// Probes whether Microsoft Edge WebView2 Evergreen Runtime is available on the current system.
/// Required only for browser-based OAuth account additions.
/// </summary>
public sealed class WebView2AvailabilityService : IWebView2AvailabilityService
{
    private readonly Func<string?> _versionProvider;

    public WebView2AvailabilityService(Func<string?>? versionProvider = null)
    {
        _versionProvider = versionProvider ?? (() => null);
    }

    public bool IsAvailable()
    {
        try
        {
            var version = _versionProvider();
            return !string.IsNullOrWhiteSpace(version);
        }
        catch
        {
            return false;
        }
    }

    public string? GetInstalledVersion()
    {
        try
        {
            return _versionProvider();
        }
        catch
        {
            return null;
        }
    }
}

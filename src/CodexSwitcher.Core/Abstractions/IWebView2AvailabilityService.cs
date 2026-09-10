namespace CodexSwitcher.Core.Abstractions;

/// <summary>
/// Probes whether Microsoft Edge WebView2 Evergreen Runtime is available on the current system.
/// Required only for browser-based OAuth account additions; other functionality remains operative.
/// </summary>
public interface IWebView2AvailabilityService
{
    bool IsAvailable();
    string? GetInstalledVersion();
}

using System.Diagnostics;
using CodexSwitcher.Core.Abstractions;
using CodexSwitcher.Core.Services;
using Microsoft.Web.WebView2.Core;

namespace CodexSwitcher.App.Services;

/// <summary>
/// Probes for Microsoft Edge WebView2 Evergreen Runtime.
/// Provides safe redirection to official Microsoft documentation rather than silent downloading.
/// </summary>
public static class WebView2Bootstrap
{
    public const string OfficialInfoUrl = "https://developer.microsoft.com/en-us/microsoft-edge/webview2/";

    private static readonly IWebView2AvailabilityService Detector =
        new WebView2AvailabilityService(DefaultVersionProvider);

    private static string? DefaultVersionProvider()
    {
        try
        {
            return CoreWebView2Environment.GetAvailableBrowserVersionString(null);
        }
        catch
        {
            return null;
        }
    }

    public static bool IsRuntimeInstalled() => Detector.IsAvailable();

    public static string? GetInstalledVersion() => Detector.GetInstalledVersion();

    /// <summary>
    /// Opens the official Microsoft WebView2 info and download page in the user's default browser.
    /// Does not silently download or install any external binaries.
    /// </summary>
    public static bool OpenOfficialDownloadPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = OfficialInfoUrl,
                UseShellExecute = true
            });
            return true;
        }
        catch
        {
            return false;
        }
    }
}

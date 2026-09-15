using CodexSwitcher.Core.Accounts.Contracts;
using CodexSwitcher.Core.Accounts.Formatting;
using CodexSwitcher.Core.Accounts.Models;
using CodexSwitcher.Core.Accounts.Services;
using CodexSwitcher.Core.Common.Dispatcher;
using CodexSwitcher.Core.Common.Enums;
using CodexSwitcher.Core.Common.Environment;
using CodexSwitcher.Core.Common.Errors;
using CodexSwitcher.Core.Common.Lifecycle;
using CodexSwitcher.Core.Common.Logging;
using CodexSwitcher.Core.Common.Storage;
using CodexSwitcher.Core.Common.Time;
using CodexSwitcher.Core.Providers.Catalog;
using CodexSwitcher.Core.Providers.Contracts;
using CodexSwitcher.Core.Providers.Models;
using CodexSwitcher.Core.Providers.Services;
using CodexSwitcher.Core.Routing.Contracts;
using CodexSwitcher.Core.Routing.Models;
using CodexSwitcher.Core.Routing.Services;
using CodexSwitcher.Core.Security.Secrets;
using CodexSwitcher.Core.Security.Totp;
using CodexSwitcher.Core.Security.Verification;
using CodexSwitcher.Core.Settings.Contracts;
using CodexSwitcher.Core.Settings.Models;
using CodexSwitcher.Core.Threads.Contracts;
using CodexSwitcher.Core.Threads.Models;
using CodexSwitcher.Core.Transfer.Contracts;
using CodexSwitcher.Core.Transfer.Models;
using CodexSwitcher.Core.Transfer.Services;
using CodexSwitcher.Core.Usage.Contracts;
using CodexSwitcher.Core.Usage.Formatting;
using CodexSwitcher.Core.Usage.Models;
using CodexSwitcher.Core.Usage.Services;
using CodexSwitcher.Infra.Accounts.Storage;
using CodexSwitcher.Infra.Codex.Routing;
using CodexSwitcher.Infra.Codex.Runtime;
using CodexSwitcher.Infra.Codex.Threads;
using CodexSwitcher.Infra.Codex.Usage;
using CodexSwitcher.Infra.Common.Logging;
using CodexSwitcher.Infra.Common.Paths;
using CodexSwitcher.Infra.Common.Storage;
using CodexSwitcher.Infra.Common.Time;
using CodexSwitcher.Infra.Providers.Inspection;
using CodexSwitcher.Infra.Providers.Secrets;
using CodexSwitcher.Infra.Providers.Storage;
using CodexSwitcher.Infra.Scheduling;
using CodexSwitcher.Infra.Security.Dpapi;
using CodexSwitcher.Infra.Security.Hardening;
using CodexSwitcher.Infra.Security.Totp;
using CodexSwitcher.Infra.Settings;
using System.Diagnostics;
using Microsoft.Web.WebView2.Core;

namespace CodexSwitcher.App.Services;

/// <summary>
/// Probes for Microsoft Edge WebView2 Evergreen Runtime.
/// Provides safe redirection to official Microsoft documentation rather than silent downloading.
/// </summary>
public static class WebView2Bootstrap
{
    public const string OfficialInfoUrl = "https://developer.microsoft.com/en-us/microsoft-edge/webview2/";

    private static readonly WebView2AvailabilityService Detector =
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

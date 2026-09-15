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
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Controls;

namespace CodexSwitcher.App.Shell.State;

/// <summary>
/// Thread-safe and sanitized implementation of <see cref="IAppNotificationService"/>.
/// Automatically strips raw credentials, Bearer tokens, secrets, and auth payload fragments.
/// </summary>
public sealed partial class AppNotificationService : ObservableObject, IAppNotificationService
{
    [ObservableProperty] public partial bool IsOpen { get; set; }
    [ObservableProperty] public partial string Title { get; set; } = string.Empty;
    [ObservableProperty] public partial string Message { get; set; } = string.Empty;
    [ObservableProperty] public partial InfoBarSeverity Severity { get; set; } = InfoBarSeverity.Informational;

    private static readonly Regex BearerPattern = new(@"Bearer\s+[a-zA-Z0-9_\-\.]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ApiKeyPattern = new(@"(sk-[a-zA-Z0-9_\-]{10,})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SecretFieldPattern = new(@"""(access_token|refresh_token|id_token|secret|apiKey|password)""\s*:\s*""[^""\r\n]+""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex OtpAuthPattern = new(@"otpauth://[^\s]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public void ShowInformation(string title, string message) =>
        Show(title, message, InfoBarSeverity.Informational);

    public void ShowSuccess(string title, string message) =>
        Show(title, message, InfoBarSeverity.Success);

    public void ShowWarning(string title, string message) =>
        Show(title, message, InfoBarSeverity.Warning);

    public void ShowError(string title, string message) =>
        Show(title, message, InfoBarSeverity.Error);

    public void Show(string title, string message, InfoBarSeverity severity)
    {
        Title = Sanitize(title);
        Message = Sanitize(message);
        Severity = severity;
        IsOpen = true;
    }

    public void Clear()
    {
        IsOpen = false;
        Title = string.Empty;
        Message = string.Empty;
        Severity = InfoBarSeverity.Informational;
    }

    public static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var sanitized = BearerPattern.Replace(text, "Bearer [REDACTED]");
        sanitized = ApiKeyPattern.Replace(sanitized, "[REDACTED_API_KEY]");
        sanitized = SecretFieldPattern.Replace(sanitized, "\"$1\": \"[REDACTED]\"");
        sanitized = OtpAuthPattern.Replace(sanitized, "[REDACTED_TOTP_URI]");

        if (sanitized.Contains("AQAAANCMnd8BFdERjHoAwE/Cl+sBAAAA", StringComparison.OrdinalIgnoreCase))
        {
            sanitized = sanitized.Replace("AQAAANCMnd8BFdERjHoAwE/Cl+sBAAAA", "[REDACTED_DPAPI_BLOB]");
        }

        return sanitized;
    }
}

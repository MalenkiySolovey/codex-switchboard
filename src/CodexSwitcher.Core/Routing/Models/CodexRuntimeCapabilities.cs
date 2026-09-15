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
namespace CodexSwitcher.Core.Routing.Models;

/// <summary>
/// Normalized capabilities of a specific Codex app-server runtime.
/// Distinguishes required capabilities for rate-limit monitoring from optional capabilities.
/// </summary>
public sealed record CodexRuntimeCapabilities(
    CapabilityStatus AccountRead = CapabilityStatus.Supported,
    CapabilityStatus RateLimitsRead = CapabilityStatus.Supported,
    CapabilityStatus AccountUsageRead = CapabilityStatus.Unknown,
    CapabilityStatus RateLimitNotifications = CapabilityStatus.Unknown)
{
    /// <summary>
    /// Returns true if the runtime supports the mandatory RPCs required for rate-limit quota monitoring.
    /// </summary>
    public bool CanMonitorQuota =>
        AccountRead != CapabilityStatus.Unsupported &&
        RateLimitsRead != CapabilityStatus.Unsupported;

    /// <summary>
    /// Returns true if the runtime explicitly supports account/usage/read.
    /// </summary>
    public bool SupportsAccountUsage =>
        AccountUsageRead == CapabilityStatus.Supported;

    /// <summary>
    /// Default capabilities for a newly resolved runtime prior to capability probing.
    /// </summary>
    public static CodexRuntimeCapabilities Default => new();

    /// <summary>
    /// Capabilities for legacy runtimes known to lack account/usage/read (e.g. 0.130.0-alpha.5).
    /// </summary>
    public static readonly CodexRuntimeCapabilities LegacyUnsupportedUsage = new(
        AccountRead: CapabilityStatus.Supported,
        RateLimitsRead: CapabilityStatus.Supported,
        AccountUsageRead: CapabilityStatus.Unsupported,
        RateLimitNotifications: CapabilityStatus.Unsupported);

    /// <summary>
    /// Capabilities for modern runtimes known to support account/usage/read (e.g. >= 0.147.0 / 0.153.4).
    /// </summary>
    public static readonly CodexRuntimeCapabilities ModernFull = new(
        AccountRead: CapabilityStatus.Supported,
        RateLimitsRead: CapabilityStatus.Supported,
        AccountUsageRead: CapabilityStatus.Supported,
        RateLimitNotifications: CapabilityStatus.Supported);
}

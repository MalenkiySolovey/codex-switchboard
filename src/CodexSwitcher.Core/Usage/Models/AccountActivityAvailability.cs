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
namespace CodexSwitcher.Core.Usage.Models;

/// <summary>
/// Granular availability state for an account's historical token activity.
/// Distinguishes between data not loaded yet, unsupported runtime, temporary failure, and available data.
/// </summary>
public enum AccountActivityAvailability
{
    /// <summary>Activity has not been queried yet (e.g. cold start).</summary>
    Unknown = 0,

    /// <summary>Query is actively in-flight.</summary>
    Loading = 1,

    /// <summary>Activity snapshot is available and fresh or restored from cache.</summary>
    Available = 2,

    /// <summary>Query succeeded but the server reported no activity data for this account.</summary>
    NotReported = 3,

    /// <summary>The selected Codex app-server runtime does not support account/usage/read.</summary>
    UnsupportedRuntime = 4,

    /// <summary>Temporary failure (e.g. transient timeout, server 500/overload) while quota succeeded.</summary>
    TemporarilyUnavailable = 5,
}

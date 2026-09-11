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
/// The 9 visual presentation states for account quota and rate-limit display.
/// </summary>
public enum UsageVisualState
{
    /// <summary>Fresh authoritative data fetched from the live Codex app-server in the current session.</summary>
    Fresh = 0,

    /// <summary>Restored from cache on startup or stale after TTL ("STALE DATA" badge).</summary>
    StaleCache = 1,

    /// <summary>In-flight refresh operation in progress ("Refreshing..." badge).</summary>
    Refreshing = 2,

    /// <summary>Account has no cached usage data and has not been queried yet.</summary>
    NeverLoaded = 3,

    /// <summary>Account has reached rate limit or 100% quota exhausted ("Rate limited" badge).</summary>
    RateLimited = 4,

    /// <summary>Authentication expired or login required (notice banner).</summary>
    AuthRequired = 5,

    /// <summary>Token or API key type does not support rate-limit queries (notice banner).</summary>
    UnsupportedAccountType = 6,

    /// <summary>Codex app-server process unavailable, backing off, or error (notice banner).</summary>
    ProcessDown = 7,

    /// <summary>Credential conflict: active account does not match vault profile (notice banner).</summary>
    CredentialConflict = 8,
}

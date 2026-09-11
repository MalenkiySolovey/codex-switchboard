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
using CodexSwitcher.Core.Accounts.Contracts;
﻿namespace CodexSwitcher.Core.Accounts.Contracts;

/// <summary>
/// Coordinates operations touching profile credentials (usage refresh, switching, CAS mutations).
/// Provides keyed asynchronous locks per profile with deterministic ordering across multi-profile
/// operations to prevent deadlocks and ensure application-level CAS atomicity.
/// </summary>
public interface IProfileOperationCoordinator
{
    /// <summary>
    /// Acquires an exclusive lock for the specified profile asynchronously.
    /// </summary>
    Task<IDisposable> LockAsync(Guid profileId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Acquires exclusive locks for two profiles in deterministic order (Guid comparison)
    /// to avoid deadlocks in two-profile operations like switching.
    /// If both profile IDs are identical, locks the profile once.
    /// </summary>
    Task<IDisposable> LockTwoAsync(Guid profileIdA, Guid profileIdB, CancellationToken cancellationToken = default);

    /// <summary>
    /// Synchronously acquires an exclusive lock for the specified profile.
    /// Used by synchronous CAS operations like <see cref=" Services.VaultService.SaveBlobIfUnchanged\/>.
 /// </summary>
 IDisposable Lock(Guid profileId);
}

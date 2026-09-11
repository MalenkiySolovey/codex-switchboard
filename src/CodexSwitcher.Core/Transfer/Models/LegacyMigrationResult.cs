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
namespace CodexSwitcher.Core.Transfer.Models;

/// <summary>
/// Result of a legacy data migration attempt from %LOCALAPPDATA%\CodexSwitcher to %LOCALAPPDATA%\CodexSwitchboard.
/// </summary>
public sealed record LegacyMigrationResult
{
    public bool Success { get; init; }
    public int MigratedProfilesCount { get; init; }
    public string Message { get; init; } = string.Empty;
    public bool DestinationAlreadyExisted { get; init; }
    public bool NoLegacyDataFound { get; init; }

    public static LegacyMigrationResult Succeeded(int count, string message = "") =>
        new() { Success = true, MigratedProfilesCount = count, Message = message };

    public static LegacyMigrationResult Failed(string message, bool destinationAlreadyExisted = false, bool noLegacyDataFound = false) =>
        new() { Success = false, Message = message, DestinationAlreadyExisted = destinationAlreadyExisted, NoLegacyDataFound = noLegacyDataFound };
}

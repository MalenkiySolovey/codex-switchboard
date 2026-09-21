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
namespace CodexSwitcher.Core.Providers.Contracts;

/// <summary>
/// Secure storage for API provider keys encrypted at rest via DPAPI CurrentUser.
/// Isolates raw provider API keys from provider metadata, settings, and logs.
/// </summary>
public interface IApiKeySecretStore
{
    /// <summary>Checks whether an encrypted API key is stored for the given profile.</summary>
    bool HasApiKey(Guid profileId);

    /// <summary>Encrypts and saves the raw API key atomically to protected storage.</summary>
    void SaveApiKey(Guid profileId, string apiKey);

    /// <summary>
    /// Decrypts the stored API key on demand.
    /// Returns null if not found. Zeroes plaintext buffers immediately after caller use.
    /// </summary>
    string? GetApiKey(Guid profileId);

    /// <summary>Permanently deletes the encrypted API key file for the given profile.</summary>
    bool DeleteApiKey(Guid profileId);

    /// <summary>
    /// Atomically copies the encrypted API key payload from a source profile to a target profile.
    /// Invariant: Does not decrypt the payload to plaintext during the clone operation.
    /// </summary>
    void CloneApiKey(Guid sourceProfileId, Guid targetProfileId);
}

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
using CodexSwitcher.Core.Providers.Contracts;
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
using CodexSwitcher.Core.Providers.Catalog;
using CodexSwitcher.Core.Providers.Models;

namespace CodexSwitcher.Core.Providers.Contracts;

/// <summary>
/// Abstraction for declarative, read-only AI provider capability and model inspection.
/// Strictly executes GET/HEAD-only probes with safe JSON pointer extraction.
/// </summary>
public interface IDeclarativeProviderInspector
{
    /// <summary>
    /// Performs only the descriptor-authorized GET /models request. Unlike the
    /// broader inspection methods, this never probes balance, usage, or model
    /// compatibility endpoints.
    /// </summary>
    Task<ApiProviderSnapshot> DiscoverModelsAsync(
        ProviderDescriptor descriptor,
        string activeBaseUrl,
        string? apiKey,
        CancellationToken ct = default);

    /// <summary>
    /// Inspects a provider using its catalog descriptor and returns a normalized snapshot.
    /// </summary>
    Task<ApiProviderSnapshot> InspectAsync(
        ProviderDescriptor descriptor,
        string activeBaseUrl,
        string? apiKey,
        CancellationToken ct = default);

    /// <summary>
    /// Inspects a generic OpenAI-compatible provider using safe standard /models probe,
    /// never guessing balance or usage endpoints.
    /// </summary>
    Task<ApiProviderSnapshot> InspectGenericUnknownAsync(
        string rawBaseUrl,
        string? apiKey,
        CancellationToken ct = default);
}

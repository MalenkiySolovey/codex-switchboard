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

public sealed record ProviderModelCacheEntry(
    Guid ProfileId,
    string RouteKey,
    int Revision,
    IReadOnlyList<string> Models,
    DateTimeOffset CachedAt);

/// <summary>
/// Route-specific model discovery cache keyed by (profileId, routeId/baseUrl, revision).
/// Ensures model truth is preserved across route changes and shared with cross-provider thread handoff/fork.
/// </summary>
public interface IProviderModelCache
{
    bool TryGetModels(Guid profileId, string routeKey, int revision, out IReadOnlyList<string> models);
    void SetModels(Guid profileId, string routeKey, int revision, IReadOnlyList<string> models);
    void Invalidate(Guid profileId, string? routeKey = null);
    IReadOnlyList<string>? GetLatestModels(Guid profileId);
}

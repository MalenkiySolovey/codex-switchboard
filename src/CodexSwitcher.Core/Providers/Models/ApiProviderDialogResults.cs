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
namespace CodexSwitcher.Core.Providers.Models;

/// <summary>
/// Result data captured from the Add API Provider dialog.
/// ApiKey is ephemeral in-memory string passed to the caller for DPAPI encryption.
/// </summary>
public sealed class AddApiProviderResult
{
    public string CatalogProviderId { get; set; } = string.Empty;
    public string Nickname { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public string SelectedRouteId { get; set; } = "primary";
    public string SelectedModel { get; set; } = string.Empty;
    public CodexModelOverrides? ModelOverrides { get; set; }
    public ApiProviderTransportOverrides? TransportOverrides { get; set; }
    public bool SaveAndSwitch { get; set; }
}

/// <summary>
/// Result data captured from the Edit API Provider dialog.
/// </summary>
public sealed class EditApiProviderResult
{
    public string Nickname { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public string SelectedRouteId { get; set; } = string.Empty;
    public string SelectedModel { get; set; } = string.Empty;
    public CodexModelOverrides? ModelOverrides { get; set; }
    public ApiProviderTransportOverrides? TransportOverrides { get; set; }
}

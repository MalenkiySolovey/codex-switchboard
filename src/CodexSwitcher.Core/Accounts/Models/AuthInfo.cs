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
namespace CodexSwitcher.Core.Accounts.Models;

/// <summary>
/// Campos lidos do <c>auth.json</c> tratado como blob opaco. Só os estritamente necessários
/// (ver BUSINESS_RULES.md §2.3 e ponto 11). O restante do arquivo é preservado byte a byte.
/// </summary>
public sealed record AuthFileInfo(
    string? AuthMode,
    DateTimeOffset? LastRefresh,
    string? IdToken,
    string? AccountId);

/// <summary>Claims decodificados localmente do id_token (JWT), sem rede. Ver §7 e ponto 12.</summary>
public sealed record AccountClaims(
    string? Sub,
    string? Email,
    DateTimeOffset? ExpiresAt,
    string? PlanType);

/// <summary>Caminhos do Codex relevantes ao app (o slot ativo é externo, gerido pelo Codex).</summary>
public sealed record CodexPaths(
    string CodexHome,
    string ActiveAuthPath,
    string ConfigTomlPath)
{
    public static CodexPaths ForHome(string codexHome) => new(
        codexHome,
        Path.Combine(codexHome, "auth.json"),
        Path.Combine(codexHome, "config.toml"));
}

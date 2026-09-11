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
namespace CodexSwitcher.Core.Routing.Contracts;

/// <summary>Como o Codex guarda credenciais, conforme <c>cli_auth_credentials_store</c>.</summary>
public enum CredentialsStoreKind
{
    /// <summary>Não definido no config.toml (default do Codex = auto).</summary>
    Unset = 0,
    File = 1,
    Keyring = 2,
    Auto = 3,
}

/// <summary>
/// Lê/força a configuração <c>cli_auth_credentials_store = "file"</c> no config.toml,
/// preservando o resto do arquivo. Ver BUSINESS_RULES.md ponto 1 e §4.3 passo 8.
/// </summary>
public interface ICodexConfigStore
{
    /// <summary>Valor atual da chave (Unset quando ausente).</summary>
    CredentialsStoreKind ReadCredentialsStore(string configTomlPath);

    /// <summary>
    /// Garante <c>cli_auth_credentials_store = "file"</c> de forma idempotente, preservando o
    /// restante do TOML. Retorna true se alterou o arquivo.
    /// </summary>
    bool EnsureFileStore(string configTomlPath);
}

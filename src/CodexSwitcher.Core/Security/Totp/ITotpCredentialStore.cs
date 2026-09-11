using CodexSwitcher.Core.Accounts.Contracts;
using CodexSwitcher.Core.Accounts.Formatting;
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
using CodexSwitcher.Core.Accounts.Models;

namespace CodexSwitcher.Core.Security.Totp;

/// <summary>
/// Gerenciador de credenciais TOTP (2FA) por perfil, cifradas em repouso com DPAPI.
/// Isola o segredo de 2FA do auth.json do Codex e do ProfileMetadata.
/// </summary>
public interface ITotpCredentialStore
{
    /// <summary>Verifica se há segredo TOTP configurado para o perfil especificado.</summary>
    bool HasCredential(Guid profileId);

    /// <summary>
    /// Cifra e grava o segredo TOTP de forma atômica no armazenamento protegido.
    /// Valida o segredo antes da persistência.
    /// </summary>
    void Save(Guid profileId, string provisioning, DateTimeOffset createdAt);

    /// <summary>
    /// Cifra e grava o segredo TOTP de forma atômica no armazenamento protegido.
    /// Valida o segredo antes da persistência. Usa o relógio padrão para a data de criação.
    /// </summary>
    void Save(Guid profileId, string provisioning);

    /// <summary>
    /// Decifra o segredo sob demanda, gera o código TOTP atual e descarta buffers intermediários.
    /// Nunca expõe a semente de 2FA à camada de apresentação.
    /// </summary>
    bool TryComputeCode(Guid profileId, DateTimeOffset now, out TotpCode code, out string? error);

    /// <summary>Exclui o arquivo cifrado do segredo TOTP do perfil.</summary>
    bool Delete(Guid profileId);
}

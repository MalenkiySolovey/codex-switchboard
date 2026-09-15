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
namespace CodexSwitcher.Core.Security.Secrets;

/// <summary>
/// Cifra/decifra segredos em repouso. Implementação de produção usa DPAPI (escopo CurrentUser),
/// amarrando a decifragem ao mesmo usuário Windows da mesma máquina. Ver BUSINESS_RULES.md §7.
/// </summary>
public interface ISecretProtector
{
    /// <summary>Cifra os bytes em claro. Nunca loga o conteúdo.</summary>
    byte[] Protect(byte[] plaintext);

    /// <summary>
    /// Decifra os bytes. Lança <see cref="SecretDecryptionException"/> quando não é possível
    /// (ex.: blob criado em outra máquina/usuário) — tratado como perfil "Indisponível".
    /// </summary>
    byte[] Unprotect(byte[] ciphertext);
}

/// <summary>Falha de decifragem — normalmente perfil de outro usuário/máquina. Nunca contém tokens.</summary>
public sealed class SecretDecryptionException : Exception
{
    public SecretDecryptionException(string message, Exception? inner = null)
        : base(message, inner) { }
}

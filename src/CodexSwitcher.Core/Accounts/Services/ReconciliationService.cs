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
using CodexSwitcher.Core.Routing.Models;
using CodexSwitcher.Core.Accounts.Models;

namespace CodexSwitcher.Core.Accounts.Services;

/// <summary>
/// Detecta qual perfil ocupa o slot ativo comparando fingerprint e <c>sub</c> do auth.json real
/// com os perfis conhecidos. Não decifra o cofre — só lê o slot ativo em claro. Ver §4.6.
/// </summary>
public sealed class ReconciliationService
{
    private readonly IFileSystem _fs;
    private readonly CodexPaths _paths;

    public ReconciliationService(IFileSystem fs, CodexPaths paths)
    {
        _fs = fs ?? throw new ArgumentNullException(nameof(fs));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    /// <summary>
    /// Atualiza <see cref="ProfileMetadata.IsActive"/> de cada perfil conforme o slot ativo e
    /// devolve o resultado (inclui <see cref="ActiveMatch.SameAccountDrifted"/> quando o Codex
    /// renovou externamente e é preciso write-back).
    /// </summary>
    public ReconciliationResult Reconcile(IReadOnlyList<ProfileMetadata> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);

        foreach (var p in profiles)
            p.IsActive = false;

        if (!_fs.FileExists(_paths.ActiveAuthPath))
            return ReconciliationResult.NoActive();

        byte[] activeBytes;
        try
        {
            activeBytes = _fs.ReadAllBytes(_paths.ActiveAuthPath);
        }
        catch (IOException)
        {
            return ReconciliationResult.NoActive();
        }

        var fingerprint = Fingerprint.Compute(activeBytes);

        var exact = profiles.FirstOrDefault(p => p.BlobFingerprint == fingerprint);
        if (exact is not null)
        {
            exact.IsActive = true;
            return new ReconciliationResult(ActiveMatch.Exact, exact.Id, exact.AccountSub, fingerprint);
        }

        var (_, claims) = AuthJsonReader.Identify(activeBytes);
        if (!string.IsNullOrEmpty(claims.Sub))
        {
            var sameAccount = profiles.FirstOrDefault(p => p.AccountSub == claims.Sub);
            if (sameAccount is not null)
            {
                sameAccount.IsActive = true;
                return new ReconciliationResult(ActiveMatch.SameAccountDrifted, sameAccount.Id, claims.Sub, fingerprint);
            }
        }

        // Conta não gerenciada no slot ativo (candidata a adoção).
        return new ReconciliationResult(ActiveMatch.None, null, claims.Sub, fingerprint);
    }
}

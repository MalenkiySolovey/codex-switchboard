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
using CodexSwitcher.Infra.Accounts.Storage;
using CodexSwitcher.Infra.Codex.Routing;
using CodexSwitcher.Infra.Codex.Runtime;
using CodexSwitcher.Infra.Codex.Threads;
using CodexSwitcher.Infra.Codex.Usage;
using CodexSwitcher.Infra.Common.Logging;
using CodexSwitcher.Infra.Common.Paths;
using CodexSwitcher.Infra.Common.Storage;
using CodexSwitcher.Infra.Common.Time;
using CodexSwitcher.Infra.Providers.Inspection;
using CodexSwitcher.Infra.Providers.Secrets;
using CodexSwitcher.Infra.Providers.Storage;
using CodexSwitcher.Infra.Scheduling;
using CodexSwitcher.Infra.Security.Dpapi;
using CodexSwitcher.Infra.Security.Hardening;
using CodexSwitcher.Infra.Security.Totp;
using CodexSwitcher.Infra.Settings;
using CodexSwitcher.Core.Routing.Contracts;
using System.Text.RegularExpressions;

namespace CodexSwitcher.Infra.Codex.Routing;

/// <summary>
/// Lê/garante <c>cli_auth_credentials_store = "file"</c> no config.toml, preservando o resto do
/// arquivo. Só mexe na chave de nível-raiz (antes do primeiro cabeçalho de tabela). Idempotente:
/// não reescreve se já estiver "file". Ver BUSINESS_RULES.md ponto 1 e §4.3 passo 8.
/// </summary>
public sealed partial class ConfigTomlStore : ICodexConfigStore
{
    private const string Key = "cli_auth_credentials_store";
    private readonly IFileSystem _fs;

    public ConfigTomlStore(IFileSystem fs) => _fs = fs ?? throw new ArgumentNullException(nameof(fs));

    [GeneratedRegex(@"^\s*cli_auth_credentials_store\s*=\s*""(?<val>[^""]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex KeyLineRegex();

    [GeneratedRegex(@"^\s*\[")]
    private static partial Regex TableHeaderRegex();

    public CredentialsStoreKind ReadCredentialsStore(string configTomlPath)
    {
        if (!_fs.FileExists(configTomlPath))
            return CredentialsStoreKind.Unset;

        var lines = _fs.ReadAllText(configTomlPath).Split('\n');
        var firstTable = FirstTableIndex(lines);

        for (var i = 0; i < firstTable; i++)
        {
            var m = KeyLineRegex().Match(lines[i]);
            if (m.Success)
            {
                return m.Groups["val"].Value.ToLowerInvariant() switch
                {
                    "file" => CredentialsStoreKind.File,
                    "keyring" => CredentialsStoreKind.Keyring,
                    "auto" => CredentialsStoreKind.Auto,
                    _ => CredentialsStoreKind.Unset,
                };
            }
        }

        return CredentialsStoreKind.Unset;
    }

    public bool EnsureFileStore(string configTomlPath)
    {
        var raw = _fs.FileExists(configTomlPath) ? _fs.ReadAllText(configTomlPath) : string.Empty;
        var newline = raw.Contains("\r\n") ? "\r\n" : "\n";
        var lines = raw.Length == 0 ? [] : new List<string>(raw.Split('\n'));

        var firstTable = FirstTableIndex(lines);

        for (var i = 0; i < firstTable; i++)
        {
            var m = KeyLineRegex().Match(lines[i]);
            if (m.Success)
            {
                if (m.Groups["val"].Value.Equals("file", StringComparison.OrdinalIgnoreCase))
                    return false; // já está correto — não reescreve.

                lines[i] = KeyLineRegex().Replace(lines[i], $"{Key} = \"file\"");
                _fs.WriteAllTextAtomic(configTomlPath, string.Join(newline, lines));
                return true;
            }
        }

        // Chave ausente: inserir como chave de nível-raiz, antes do primeiro cabeçalho de tabela.
        var insertAt = firstTable;
        lines.Insert(insertAt, $"{Key} = \"file\"");
        _fs.WriteAllTextAtomic(configTomlPath, string.Join(newline, lines));
        return true;
    }

    private static int FirstTableIndex(IReadOnlyList<string> lines)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (TableHeaderRegex().IsMatch(lines[i]))
                return i;
        }
        return lines.Count;
    }
}

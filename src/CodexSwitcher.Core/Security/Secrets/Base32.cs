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
/// Decodificador Base32 (RFC 4648) usado pelos segredos TOTP de 2FA. Aceita minúsculas, espaços e
/// hífens (comuns quando o usuário cola a "chave secreta" de configuração), e o padding "=".
/// </summary>
public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>Decodifica o texto Base32 em bytes. Retorna false se houver caractere inválido.</summary>
    public static bool TryDecode(string? input, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrWhiteSpace(input)) return false;

        // Normaliza: remove separadores comuns, padding e caixa.
        var cleaned = input
            .Replace(" ", string.Empty)
            .Replace("-", string.Empty)
            .Replace("=", string.Empty)
            .Trim()
            .ToUpperInvariant();
        if (cleaned.Length == 0) return false;

        var output = new List<byte>(cleaned.Length * 5 / 8);
        var buffer = 0;
        var bitsInBuffer = 0;

        foreach (var c in cleaned)
        {
            var value = Alphabet.IndexOf(c);
            if (value < 0) return false;

            buffer = (buffer << 5) | value;
            bitsInBuffer += 5;
            if (bitsInBuffer >= 8)
            {
                bitsInBuffer -= 8;
                output.Add((byte)((buffer >> bitsInBuffer) & 0xFF));
            }
        }

        bytes = output.ToArray();
        return bytes.Length > 0;
    }
}

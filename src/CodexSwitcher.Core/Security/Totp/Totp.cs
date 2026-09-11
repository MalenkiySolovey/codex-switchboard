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
using System.Security.Cryptography;

namespace CodexSwitcher.Core.Security.Totp;

/// <summary>Algoritmo de hash do TOTP (RFC 6238). SHA1 é o padrão dos autenticadores.</summary>
public enum TotpAlgorithm { Sha1, Sha256, Sha512 }

/// <summary>Código TOTP calculado e quanto falta (em segundos) para ele expirar.</summary>
public readonly record struct TotpCode(string Code, int SecondsRemaining, int Period)
{
    /// <summary>Código agrupado para leitura, ex.: "123 456".</summary>
    public string Formatted =>
        Code.Length == 6 ? $"{Code[..3]} {Code[3..]}"
        : Code.Length == 8 ? $"{Code[..4]} {Code[4..]}"
        : Code;
}

/// <summary>
/// Segredo TOTP já normalizado (chave + parâmetros), pronto para gerar códigos de 2FA (RFC 6238).
/// Criado por <see cref="Totp.TryParse"/> a partir da chave Base32 colada pelo usuário ou de uma URI
/// <c>otpauth://</c>.
/// </summary>
public sealed class TotpSecret
{
    private readonly byte[] _key;

    public int Digits { get; }
    public int Period { get; }
    public TotpAlgorithm Algorithm { get; }
    public string? Label { get; }
    public string? Issuer { get; }

    internal TotpSecret(byte[] key, int digits, int period, TotpAlgorithm algorithm, string? label, string? issuer)
    {
        _key = key;
        Digits = digits;
        Period = period;
        Algorithm = algorithm;
        Label = label;
        Issuer = issuer;
    }

    /// <summary>Gera o código válido no instante <paramref name="now"/> e os segundos até expirar.</summary>
    public TotpCode Compute(DateTimeOffset now)
    {
        var unixSeconds = now.ToUnixTimeSeconds();
        var counter = unixSeconds / Period;

        var counterBytes = new byte[8];
        var c = counter;
        for (var i = 7; i >= 0; i--)
        {
            counterBytes[i] = (byte)(c & 0xFF);
            c >>= 8;
        }

        var hash = ComputeHmac(counterBytes);
        var offset = hash[^1] & 0x0F;
        var binary =
            ((hash[offset] & 0x7F) << 24) |
            ((hash[offset + 1] & 0xFF) << 16) |
            ((hash[offset + 2] & 0xFF) << 8) |
            (hash[offset + 3] & 0xFF);

        var modulo = (int)Math.Pow(10, Digits);
        var code = (binary % modulo).ToString().PadLeft(Digits, '0');

        var remaining = (int)(Period - (unixSeconds % Period));
        return new TotpCode(code, remaining, Period);
    }

    private byte[] ComputeHmac(byte[] message)
    {
        using HMAC hmac = Algorithm switch
        {
            TotpAlgorithm.Sha256 => new HMACSHA256(_key),
            TotpAlgorithm.Sha512 => new HMACSHA512(_key),
            _ => new HMACSHA1(_key),
        };
        return hmac.ComputeHash(message);
    }
}

/// <summary>Fábrica de <see cref="TotpSecret"/> tolerante ao que o usuário cola (2FA).</summary>
public static class Totp
{
    public const int DefaultDigits = 6;
    public const int DefaultPeriod = 30;

    /// <summary>
    /// Interpreta o texto colado: chave Base32 crua (com espaços/hífens/caixa livres) ou uma URI
    /// <c>otpauth://totp/...?secret=...&amp;issuer=...&amp;digits=...&amp;period=...&amp;algorithm=...</c>.
    /// Retorna false com uma mensagem se a chave for inválida.
    /// </summary>
    public static bool TryParse(string? input, out TotpSecret? secret, out string? error)
    {
        secret = null;
        error = null;

        if (string.IsNullOrWhiteSpace(input))
        {
            error = "Cole a chave secreta do 2FA.";
            return false;
        }

        var text = input.Trim();
        var digits = DefaultDigits;
        var period = DefaultPeriod;
        var algorithm = TotpAlgorithm.Sha1;
        string? label = null;
        string? issuer = null;
        string secretText;

        if (text.StartsWith("otpauth://", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseOtpAuth(text, out secretText, out digits, out period, out algorithm, out label, out issuer, out error))
                return false;
        }
        else
        {
            secretText = text;
        }

        if (!Base32.TryDecode(secretText, out var key))
        {
            error = "Chave secreta inválida. Verifique se copiou a chave completa (Base32).";
            return false;
        }

        if (digits is < 6 or > 8) digits = DefaultDigits;
        if (period <= 0) period = DefaultPeriod;

        secret = new TotpSecret(key, digits, period, algorithm, label, issuer);
        return true;
    }

    private static bool TryParseOtpAuth(
        string uri, out string secretText, out int digits, out int period,
        out TotpAlgorithm algorithm, out string? label, out string? issuer, out string? error)
    {
        secretText = string.Empty;
        digits = DefaultDigits;
        period = DefaultPeriod;
        algorithm = TotpAlgorithm.Sha1;
        label = null;
        issuer = null;
        error = null;

        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
        {
            error = "URI otpauth inválida.";
            return false;
        }

        if (!string.Equals(parsed.Host, "totp", StringComparison.OrdinalIgnoreCase))
        {
            error = "Somente 2FA por tempo (TOTP) é suportado.";
            return false;
        }

        var query = ParseQuery(parsed.Query);
        secretText = query.GetValueOrDefault("secret") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(secretText))
        {
            error = "A URI otpauth não contém uma chave secreta.";
            return false;
        }

        issuer = query.GetValueOrDefault("issuer");
        label = Uri.UnescapeDataString(parsed.AbsolutePath.TrimStart('/'));
        if (string.IsNullOrWhiteSpace(label)) label = null;

        if (int.TryParse(query.GetValueOrDefault("digits"), out var d)) digits = d;
        if (int.TryParse(query.GetValueOrDefault("period"), out var p)) period = p;

        algorithm = (query.GetValueOrDefault("algorithm") ?? string.Empty).ToUpperInvariant() switch
        {
            "SHA256" => TotpAlgorithm.Sha256,
            "SHA512" => TotpAlgorithm.Sha512,
            _ => TotpAlgorithm.Sha1,
        };

        return true;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq < 0) continue;
            var name = Uri.UnescapeDataString(pair[..eq]);
            var value = Uri.UnescapeDataString(pair[(eq + 1)..]);
            result[name] = value;
        }
        return result;
    }
}

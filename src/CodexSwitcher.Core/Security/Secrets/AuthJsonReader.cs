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
using System.Text.Json;

namespace CodexSwitcher.Core.Security.Secrets;

/// <summary>
/// Lê apenas os campos necessários do auth.json (blob opaco): auth_mode, last_refresh,
/// tokens.id_token, tokens.account_id. Nunca muta o arquivo; o restante é preservado byte a byte
/// pelo cofre. Ver BUSINESS_RULES.md §2.3 e ponto 11.
/// </summary>
public static class AuthJsonReader
{
    /// <summary>
    /// Lê os campos leves do auth.json. Retorna null se o conteúdo não for JSON válido
    /// (o chamador trata como <see cref="ErrorCategory.InvalidAuthFile"/>).
    /// </summary>
    public static AuthFileInfo? TryRead(byte[] authJsonBytes)
    {
        ArgumentNullException.ThrowIfNull(authJsonBytes);
        try
        {
            using var doc = JsonDocument.Parse(authJsonBytes);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            var authMode = GetString(root, "auth_mode");
            var lastRefresh = GetDateTime(root, "last_refresh");

            string? idToken = null;
            string? accountId = null;
            if (root.TryGetProperty("tokens", out var tokens) && tokens.ValueKind == JsonValueKind.Object)
            {
                idToken = GetString(tokens, "id_token");
                accountId = GetString(tokens, "account_id");
            }

            return new AuthFileInfo(authMode, lastRefresh, idToken, accountId);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Combina os campos do auth.json com os claims do id_token para identificar a conta.</summary>
    public static (AuthFileInfo? File, AccountClaims Claims) Identify(byte[] authJsonBytes)
    {
        var info = TryRead(authJsonBytes);
        var claims = JwtClaimsReader.Read(info?.IdToken);
        return (info, claims);
    }

    private static string? GetString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static DateTimeOffset? GetDateTime(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v))
            return null;

        return v.ValueKind switch
        {
            JsonValueKind.String when DateTimeOffset.TryParse(v.GetString(), out var dto) => dto,
            JsonValueKind.Number when v.TryGetInt64(out var unix) => DateTimeOffset.FromUnixTimeSeconds(unix),
            _ => null,
        };
    }
}

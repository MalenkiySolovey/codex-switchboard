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
using System.Text;
using System.Text.Json;

namespace CodexSwitcher.Core.Security.Secrets;

/// <summary>
/// Decodifica claims do id_token (JWT) <b>localmente</b>, sem rede, para identificar a conta.
/// Nunca valida assinatura (não é autenticação — é só exibição) e nunca loga o token.
/// Ver BUSINESS_RULES.md §7 e ponto 12.
/// </summary>
public static class JwtClaimsReader
{
    /// <summary>
    /// Extrai <c>sub</c>, <c>email</c>, expiração e plano do payload do JWT. Retorna claims
    /// vazios se o token for malformado (nunca lança por causa de conteúdo inválido).
    /// </summary>
    public static AccountClaims Read(string? idToken)
    {
        if (string.IsNullOrWhiteSpace(idToken))
            return new AccountClaims(null, null, null, null);

        var parts = idToken.Split('.');
        if (parts.Length < 2)
            return new AccountClaims(null, null, null, null);

        try
        {
            var payloadJson = DecodeBase64Url(parts[1]);
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;

            var sub = GetString(root, "sub");
            var email = GetString(root, "email");
            var exp = GetUnixSeconds(root, "exp");
            var plan = ReadPlan(root);

            return new AccountClaims(sub, email, exp, plan);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return new AccountClaims(null, null, null, null);
        }
    }

    private static string? ReadPlan(JsonElement root)
    {
        // O plano pode aparecer em claims aninhados dependendo da versão; tentamos alguns
        // caminhos comuns sem depender de schema fixo (blob opaco).
        var direct = GetString(root, "plan") ?? GetString(root, "plan_type") ?? GetString(root, "chatgpt_plan_type");
        if (direct is not null)
            return direct;

        if (root.TryGetProperty("https://api.openai.com/auth", out var authClaim) &&
            authClaim.ValueKind == JsonValueKind.Object)
        {
            return GetString(authClaim, "chatgpt_plan_type") ?? GetString(authClaim, "plan_type");
        }

        return null;
    }

    private static string? GetString(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object &&
        obj.TryGetProperty(name, out var v) &&
        v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static DateTimeOffset? GetUnixSeconds(JsonElement obj, string name)
    {
        if (obj.ValueKind == JsonValueKind.Object &&
            obj.TryGetProperty(name, out var v) &&
            v.ValueKind == JsonValueKind.Number &&
            v.TryGetInt64(out var seconds))
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds);
        }
        return null;
    }

    private static string DecodeBase64Url(string input)
    {
        var s = input.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        var bytes = Convert.FromBase64String(s);
        return Encoding.UTF8.GetString(bytes);
    }
}

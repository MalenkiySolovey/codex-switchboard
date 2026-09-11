using System.Globalization;
using System.Text;
using System.Text.Json;
using CodexSwitcher.Core.Models;

namespace CodexSwitcher.Core.Security;

/// <summary>
/// Extrai metadados de período de assinatura a partir de claims JWT em credenciais OAuth (auth.json).
/// Decodifica apenas localmente, sem rede.
/// Invariantes estritos:
/// 1. NUNCA utiliza JWT "exp" como data de assinatura (exp = expiração do token de acesso).
/// 2. NUNCA utiliza JWT "iat" como data de assinatura (iat = momento de emissão do token).
/// 3. Rejeita silenciosamente dados malformados sem falhar o login ou corromper perfis.
/// </summary>
public static class SubscriptionJwtClaimExtractor
{
    private const string OpenAiAuthNamespace = "https://api.openai.com/auth";

    // Planos de assinatura pessoal com ciclo mensal padrão na OpenAI
    private static readonly HashSet<string> KnownMonthlyConsumerPlans = new(StringComparer.OrdinalIgnoreCase)
    {
        "plus",
        "pro",
    };

    /// <summary>
    /// Extrai informações de assinatura observadas do payload do auth.json.
    /// Inspeciona primeiramente o id_token; se não houver claims de assinatura, inspeciona access_token.
    /// </summary>
    public static DetectedSubscriptionInfo? Extract(byte[]? authJsonBytes, DateTimeOffset now)
    {
        if (authJsonBytes is null || authJsonBytes.Length == 0)
            return null;

        try
        {
            using var doc = JsonDocument.Parse(authJsonBytes);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            string? idToken = null;
            string? accessToken = null;

            if (root.TryGetProperty("tokens", out var tokens) && tokens.ValueKind == JsonValueKind.Object)
            {
                idToken = GetString(tokens, "id_token");
                accessToken = GetString(tokens, "access_token");
            }

            // Prioridade 1: id_token
            if (!string.IsNullOrWhiteSpace(idToken))
            {
                var detected = ExtractFromJwt(idToken, now);
                if (detected is not null)
                    return detected;
            }

            // Prioridade 2: access_token fallback
            if (!string.IsNullOrWhiteSpace(accessToken))
            {
                var detected = ExtractFromJwt(accessToken, now);
                if (detected is not null)
                    return detected;
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Extrai informações de assinatura de um único token JWT.
    /// </summary>
    public static DetectedSubscriptionInfo? ExtractFromJwt(string? jwtToken, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(jwtToken))
            return null;

        var parts = jwtToken.Split('.');
        if (parts.Length < 2)
            return null;

        try
        {
            var payloadJson = DecodeBase64Url(parts[1]);
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            JsonElement? authNs = null;
            if (root.TryGetProperty(OpenAiAuthNamespace, out var nsEl) && nsEl.ValueKind == JsonValueKind.Object)
            {
                authNs = nsEl;
            }

            // Extrai plano (namespaced tem precedência)
            var planType = authNs.HasValue ? GetString(authNs.Value, "chatgpt_plan_type") : null;
            planType ??= GetString(root, "chatgpt_plan_type") ?? GetString(root, "plan_type") ?? GetString(root, "plan");

            // Extrai datas estritamente dos claims de assinatura; NUNCA de exp ou iat
            DateTimeOffset? activeStart = null;
            DateTimeOffset? activeUntil = null;

            if (authNs.HasValue)
            {
                activeStart = ParseTimestamp(authNs.Value, "chatgpt_subscription_active_start");
                activeUntil = ParseTimestamp(authNs.Value, "chatgpt_subscription_active_until");
            }

            // Fallback para top-level apenas se presentes com prefixo de assinatura explícito
            activeStart ??= ParseTimestamp(root, "chatgpt_subscription_active_start");
            activeUntil ??= ParseTimestamp(root, "chatgpt_subscription_active_until");

            // Se temos active_until diretamente observado, este outranka qualquer estimativa
            if (activeUntil.HasValue)
            {
                var isStale = activeUntil.Value < now && IsPaidPlan(planType);
                return new DetectedSubscriptionInfo(
                    ActiveStartUtc: activeStart,
                    ActiveUntilUtc: activeUntil,
                    ObservedAtUtc: now,
                    PlanType: planType,
                    Source: DetectedSubscriptionSource.OAuthTokenClaim,
                    IsStale: isStale);
            }

            // Se apenas active_start estiver disponível e for um plano mensal conhecido, calcula estimativa
            if (activeStart.HasValue && !string.IsNullOrWhiteSpace(planType) && KnownMonthlyConsumerPlans.Contains(planType))
            {
                var startDateOnly = DateOnly.FromDateTime(activeStart.Value.UtcDateTime);
                var todayDateOnly = DateOnly.FromDateTime(now.UtcDateTime);
                var estimatedNext = SubscriptionTracking.EstimateNextMonthlyRenewal(startDateOnly, todayDateOnly);

                if (estimatedNext.HasValue)
                {
                    var estimatedUntil = new DateTimeOffset(estimatedNext.Value.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
                    return new DetectedSubscriptionInfo(
                        ActiveStartUtc: activeStart,
                        ActiveUntilUtc: estimatedUntil,
                        ObservedAtUtc: now,
                        PlanType: planType,
                        Source: DetectedSubscriptionSource.EstimatedFromStart,
                        IsStale: false);
                }

                // Sem estimativa calculável: registra pelo menos o início observado
                return new DetectedSubscriptionInfo(
                    ActiveStartUtc: activeStart,
                    ActiveUntilUtc: null,
                    ObservedAtUtc: now,
                    PlanType: planType,
                    Source: DetectedSubscriptionSource.OAuthTokenClaim,
                    IsStale: false);
            }

            return null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }

    private static bool IsPaidPlan(string? planType) =>
        !string.IsNullOrWhiteSpace(planType) &&
        !string.Equals(planType, "free", StringComparison.OrdinalIgnoreCase);

    private static DateTimeOffset? ParseTimestamp(JsonElement obj, string propertyName)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(propertyName, out var prop))
            return null;

        // Formato 1: ISO-8601 String (ex.: "2026-08-27T12:00:00Z")
        if (prop.ValueKind == JsonValueKind.String)
        {
            var str = prop.GetString();
            if (string.IsNullOrWhiteSpace(str))
                return null;

            if (DateTimeOffset.TryParse(str, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsedDto))
            {
                // Sanity check: anos entre 2020 e 2100
                if (parsedDto.Year >= 2020 && parsedDto.Year <= 2100)
                    return parsedDto.ToUniversalTime();
            }

            // Tenta também se a string for dígitos numéricos de timestamp unix
            if (long.TryParse(str, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unixFromStr))
            {
                return ConvertUnixTimestamp(unixFromStr);
            }

            return null;
        }

        // Formato 2: Número (Unix timestamp seconds ou milliseconds)
        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out var unixNumber))
        {
            return ConvertUnixTimestamp(unixNumber);
        }

        return null;
    }

    private static DateTimeOffset? ConvertUnixTimestamp(long timestamp)
    {
        // Segundos razoáveis entre 2020-01-01 (1577836800) e 2100-01-01 (4102444800)
        if (timestamp >= 1577836800L && timestamp <= 4102444800L)
        {
            return DateTimeOffset.FromUnixTimeSeconds(timestamp);
        }

        // Milissegundos entre 2020 e 2100 (> 1577836800000)
        if (timestamp >= 1577836800000L && timestamp <= 4102444800000L)
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(timestamp);
        }

        // Fora de limites sensatos (evita ano 1970 ou datas absurdas)
        return null;
    }

    private static string? GetString(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object &&
        obj.TryGetProperty(name, out var v) &&
        v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

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

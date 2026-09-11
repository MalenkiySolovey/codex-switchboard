namespace CodexSwitcher.Core.Models;

/// <summary>
/// Origem da informação de assinatura detectada automaticamente.
/// </summary>
public enum DetectedSubscriptionSource
{
    /// <summary>Observado nos claims namespaced do token OAuth (id_token / access_token).</summary>
    OAuthTokenClaim,

    /// <summary>Estimado mensalmente a partir da data de início observada no token.</summary>
    EstimatedFromStart,
}

/// <summary>
/// Metadados observacionais de período de assinatura extraídos de credenciais OAuth.
/// Contém estritamente metadados temporais não-secretos; nunca armazena tokens ou chaves.
/// </summary>
public sealed record DetectedSubscriptionInfo(
    DateTimeOffset? ActiveStartUtc,
    DateTimeOffset? ActiveUntilUtc,
    DateTimeOffset ObservedAtUtc,
    string? PlanType,
    DetectedSubscriptionSource Source,
    bool IsStale = false)
{
    /// <summary>
    /// Indica se há pelo menos um limite temporal utilizável (início ou término).
    /// </summary>
    public bool HasAnyDate => ActiveStartUtc.HasValue || ActiveUntilUtc.HasValue;
}

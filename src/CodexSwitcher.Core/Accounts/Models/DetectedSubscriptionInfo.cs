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
namespace CodexSwitcher.Core.Accounts.Models;

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

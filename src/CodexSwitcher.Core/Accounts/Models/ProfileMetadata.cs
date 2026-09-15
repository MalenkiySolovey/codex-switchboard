using CodexSwitcher.Core.Accounts.Contracts;
using CodexSwitcher.Core.Accounts.Formatting;
using CodexSwitcher.Core.Accounts.Models;
using CodexSwitcher.Core.Accounts.Services;
using CodexSwitcher.Core.Common.Dispatcher;
using CodexSwitcher.Core.Common.Enums;
using CodexSwitcher.Core.Common.Environment;
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
using CodexSwitcher.Core.Common.Errors;

namespace CodexSwitcher.Core.Accounts.Models;

/// <summary>
/// Metadados persistidos de um perfil (sem segredos). Ver BUSINESS_RULES.md §2.2.
/// O <c>auth.json</c> em si vive cifrado no cofre, nunca aqui.
/// </summary>
public sealed class ProfileMetadata
{
    /// <summary>Versão atual do schema de metadados.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Identificador imutável do perfil; também nomeia o blob no cofre.</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Apelido editável pelo usuário.</summary>
    public string Nickname { get; set; } = string.Empty;

    /// <summary>E-mail da conta, extraído localmente do claim <c>email</c> do id_token. Pode ser nulo.</summary>
    public string? AccountEmail { get; set; }

    /// <summary>Claim <c>sub</c> do id_token — identificador estável da conta. Usado para deduplicar/reconciliar.</summary>
    public string? AccountSub { get; set; }

    /// <summary>Campo <c>auth_mode</c> do auth.json (ex.: "chatgpt", "apikey").</summary>
    public string? AuthMode { get; set; }

    /// <summary>Tipo de plano, se presente nos claims. Só exibição.</summary>
    public string? PlanType { get; set; }

    /// <summary>Momento de criação do perfil (UTC).</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Última vez que este perfil virou o slot ativo via app (UTC). Nulo se nunca.</summary>
    public DateTimeOffset? LastSwitchedAt { get; set; }

    /// <summary>Última renovação bem-sucedida (worker ou write-back) (UTC). Nulo se nunca.</summary>
    public DateTimeOffset? LastRefreshedAt { get; set; }

    /// <summary>Saúde atual da credencial.</summary>
    public HealthStatus HealthStatus { get; set; } = HealthStatus.Unknown;

    /// <summary>Último erro categorizado (sem tokens). Nulo quando saudável.</summary>
    public ErrorInfo? LastError { get; set; }

    /// <summary>Hash (SHA-256, hex) do conteúdo do auth.json decifrado no momento da gravação.</summary>
    public string? BlobFingerprint { get; set; }

    /// <summary>Indica se este perfil ocupa o slot ativo. Derivado por reconciliação; cache não-autoritativo.</summary>
    public bool IsActive { get; set; }

    /// <summary>Versão do schema para migração futura.</summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>Momento em que o usuário marcou manualmente este perfil como "usado" (UTC). O selo
    /// visual (borda verde) dura 24h a partir daqui. Nulo se nunca marcado ou já expirado.</summary>
    public DateTimeOffset? MarkedUsedAt { get; set; }

    /// <summary>Posição de exibição definida pelo usuário via drag-and-drop (crescente).</summary>
    public int SortOrder { get; set; }

    /// <summary>
    /// Metadados opcionais de rastreamento local de assinatura fornecidos pelo usuário.
    /// Nunca inferidos automaticamente de tokens ou datas de perfil.
    /// </summary>
    public SubscriptionTracking? SubscriptionTracking { get; set; }

    /// <summary>
    /// Metadados de período de assinatura observados de forma melhor-esforço no token OAuth.
    /// Nunca sobrepõem o rastreamento manual do usuário (<see cref="SubscriptionTracking"/>).
    /// </summary>
    public DetectedSubscriptionInfo? DetectedSubscription { get; set; }

    /// <summary>Nome de exibição preferido: apelido, senão e-mail, senão id curto.</summary>
    public string DisplayName =>
        !string.IsNullOrWhiteSpace(Nickname) ? Nickname
        : !string.IsNullOrWhiteSpace(AccountEmail) ? AccountEmail!
        : $"Conta {Id.ToString()[..8]}";
}

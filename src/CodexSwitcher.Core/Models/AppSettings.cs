namespace CodexSwitcher.Core.Models;

/// <summary>Preferências do app persistidas em settings.json. Ver BUSINESS_RULES.md §2.1/§4.5/§6.</summary>
public sealed class AppSettings
{
    /// <summary>Comportamento de fechar/reabrir apps do Codex no switch.</summary>
    public CloseReopenMode CloseReopenMode { get; set; } = CloseReopenMode.Automatic;

    /// <summary>Sempre exibir o popup de confirmação do switch (padrão). "Não perguntar" é só por sessão.</summary>
    public bool AlwaysConfirmSwitch { get; set; } = true;

    /// <summary>Timeout de fechamento gracioso antes do kill (segundos).</summary>
    public int GracefulCloseTimeoutSeconds { get; set; } = 5;

    /// <summary>Quantos backups do slot ativo manter (rotação). Ver §2.1.</summary>
    public int ActiveSlotBackupsToKeep { get; set; } = 10;

    /// <summary>Caminho manual para o binário codex, caso não esteja no PATH. Nulo = usar PATH.</summary>
    public string? CodexExecutablePathOverride { get; set; }

    /// <summary>Forçar o tema (null = seguir o sistema).</summary>
    public string? ForcedTheme { get; set; }

    public int SchemaVersion { get; set; } = 1;

    /// <summary>
    /// Conjunto de IDs de perfis cujos cards de conta estão no modo compacto (recolhido).
    /// Perfis não listados aqui são exibidos no modo expandido padrão.
    /// </summary>
    public HashSet<Guid> CollapsedProfileIds { get; set; } = [];
}

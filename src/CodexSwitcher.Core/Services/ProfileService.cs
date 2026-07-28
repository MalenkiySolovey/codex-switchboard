using CodexSwitcher.Core.Abstractions;
using CodexSwitcher.Core.Models;
using CodexSwitcher.Core.Security;
using System.Text.Json;

namespace CodexSwitcher.Core.Services;

/// <summary>
/// Fachada de alto nível sobre cofre + metadados + reconciliação. Mantém a lista de perfis em
/// memória e coordena adicionar/renomear/remover/adotar, deduplicando por <c>sub</c>. Os
/// ViewModels dependem só desta classe e do <see cref="SwitchService"/>/<see cref="RefreshService"/>.
/// </summary>
public sealed class ProfileService
{
    private readonly VaultService _vault;
    private readonly ProfileStore _store;
    private readonly ReconciliationService _reconciliation;
    private readonly IFileSystem _fs;
    private readonly CodexPaths _paths;
    private readonly IClock _clock;
    private readonly IAuditLog _audit;

    public List<ProfileMetadata> Profiles { get; private set; } = [];

    public ProfileService(
        VaultService vault, ProfileStore store, ReconciliationService reconciliation,
        IFileSystem fs, CodexPaths paths, IClock clock, IAuditLog audit)
    {
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _reconciliation = reconciliation ?? throw new ArgumentNullException(nameof(reconciliation));
        _fs = fs ?? throw new ArgumentNullException(nameof(fs));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

    /// <summary>Carrega os perfis do disco e reconcilia com o slot ativo.</summary>
    public ReconciliationResult Load()
    {
        Profiles = _store.LoadAll();
        EnsureSortOrderInitialized();
        return Reconcile();
    }

    /// <summary>
    /// Perfis salvos antes da feature de reordenação não têm <see cref="ProfileMetadata.SortOrder"/>
    /// definido (todos ficam em 0). Nesse caso, atribui uma ordem inicial replicando o critério
    /// antigo (ativa primeiro, depois trocada/criada mais recentemente), preservando a ordem que o
    /// usuário já via. Uma vez que existam valores distintos, respeita a ordem manual dali em diante.
    /// </summary>
    private void EnsureSortOrderInitialized()
    {
        if (Profiles.Count < 2) return;
        if (Profiles.Select(p => p.SortOrder).Distinct().Count() > 1) return;

        var legacyOrder = Profiles
            .OrderByDescending(p => p.IsActive)
            .ThenByDescending(p => p.LastSwitchedAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(p => p.CreatedAt)
            .ToList();
        for (var i = 0; i < legacyOrder.Count; i++)
            legacyOrder[i].SortOrder = i;
        _store.SaveAll(Profiles);
    }

    /// <summary>Reconcilia; em caso de drift (Codex renovou externamente) faz write-back no cofre.</summary>
    public ReconciliationResult Reconcile()
    {
        var result = _reconciliation.Reconcile(Profiles);

        if (result.Match == ActiveMatch.SameAccountDrifted && result.ActiveProfileId is { } id)
        {
            var profile = Profiles.FirstOrDefault(p => p.Id == id);
            if (profile is not null && _fs.FileExists(_paths.ActiveAuthPath))
            {
                var activeBytes = _fs.ReadAllBytes(_paths.ActiveAuthPath);
                profile.BlobFingerprint = _vault.SaveBlob(profile.Id, activeBytes);
                var info = AuthJsonReader.TryRead(activeBytes);
                if (info?.LastRefresh is { } lr) profile.LastRefreshedAt = lr;
                if (profile.HealthStatus is HealthStatus.Unknown) profile.HealthStatus = HealthStatus.Valid;
                _store.SaveAll(Profiles);
                _audit.Record("reconcile", "write-back", profile.DisplayName);
            }
        }

        return result;
    }

    /// <summary>Existe uma conta não gerenciada no slot ativo (candidata a adoção)?</summary>
    public bool HasUnmanagedActiveAccount()
    {
        if (!_fs.FileExists(_paths.ActiveAuthPath))
            return false;
        var result = _reconciliation.Reconcile(Profiles);
        return result is { Match: ActiveMatch.None, ActiveFingerprint: not null };
    }

    /// <summary>Adota a conta já logada no .codex como um novo perfil (importar). Ver §6 (extra).</summary>
    public ProfileMetadata? AdoptActiveAccount(string? nickname = null)
    {
        if (!_fs.FileExists(_paths.ActiveAuthPath))
            return null;
        var bytes = _fs.ReadAllBytes(_paths.ActiveAuthPath);
        var profile = AddFromAuthJson(bytes, nickname);
        profile.IsActive = true;
        _store.SaveAll(Profiles);
        _audit.Record("adopt", "ok", profile.DisplayName);
        return profile;
    }

    /// <summary>
    /// Cria (ou atualiza, se já existir o mesmo <c>sub</c>) um perfil a partir de um auth.json.
    /// Deduplica por conta. Persiste. Ver §5.2 e §9 (deduplicação).
    /// </summary>
    public ProfileMetadata AddFromAuthJson(byte[] authJson, string? nickname)
    {
        ArgumentNullException.ThrowIfNull(authJson);
        var (file, claims) = AuthJsonReader.Identify(authJson);
        if (!IsRecognizableAuthFile(file))
            throw new InvalidDataException("O arquivo selecionado não é um auth.json válido do Codex.");

        var existing = !string.IsNullOrEmpty(claims.Sub)
            ? Profiles.FirstOrDefault(p => p.AccountSub == claims.Sub)
            : null;

        if (existing is not null)
        {
            existing.BlobFingerprint = _vault.SaveBlob(existing.Id, authJson);
            existing.HealthStatus = HealthStatus.Valid;
            existing.LastError = null;
            existing.LastRefreshedAt = file?.LastRefresh ?? _clock.UtcNow;
            if (!string.IsNullOrWhiteSpace(nickname)) existing.Nickname = nickname!;
            _store.SaveAll(Profiles);
            _audit.Record("add", "updated-existing", existing.DisplayName);
            return existing;
        }

        var profile = new ProfileMetadata
        {
            Nickname = nickname ?? claims.Email ?? string.Empty,
            AccountEmail = claims.Email,
            AccountSub = claims.Sub,
            AuthMode = file?.AuthMode,
            PlanType = claims.PlanType,
            CreatedAt = _clock.UtcNow,
            LastRefreshedAt = file?.LastRefresh ?? _clock.UtcNow,
            HealthStatus = HealthStatus.Valid,
            SortOrder = Profiles.Count == 0 ? 0 : Profiles.Max(p => p.SortOrder) + 1,
        };
        profile.BlobFingerprint = _vault.SaveBlob(profile.Id, authJson);
        Profiles.Add(profile);
        _store.SaveAll(Profiles);
        _audit.Record("add", "ok", profile.DisplayName);
        return profile;
    }

    /// <summary>Exporta uma conta em um documento portátil. O resultado contém credenciais em claro.</summary>
    public string ExportOne(Guid id)
    {
        var profile = Profiles.FirstOrDefault(p => p.Id == id)
            ?? throw new InvalidOperationException("Perfil não encontrado.");
        return SerializeTransfer([ToTransferredAccount(profile)]);
    }

    /// <summary>Exporta todas as contas do cofre em um único documento portátil.</summary>
    public string ExportAll() => SerializeTransfer(Profiles
        .OrderBy(p => p.SortOrder)
        .Select(ToTransferredAccount)
        .ToList());

    /// <summary>
    /// Importa um documento produzido pelo Switcher. Contas iguais são atualizadas, sem duplicá-las.
    /// Retorna o número de entradas lidas do arquivo.
    /// </summary>
    public int Import(string document)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(document);

        AccountTransferDocument? transfer;
        try
        {
            transfer = JsonSerializer.Deserialize<AccountTransferDocument>(document);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("O arquivo de importação não é válido.", ex);
        }

        if (transfer is null || transfer.Accounts.Count == 0)
        {
            var authJson = System.Text.Encoding.UTF8.GetBytes(document);
            if (!IsRecognizableAuthFile(AuthJsonReader.TryRead(authJson)))
                throw new InvalidDataException("O arquivo de importação está vazio ou usa um formato incompatível.");

            AddFromAuthJson(authJson, null);
            _audit.Record("import", "raw-auth-json");
            return 1;
        }

        if (transfer.FormatVersion != AccountTransferDocument.CurrentFormatVersion)
            throw new InvalidDataException("O arquivo de importação usa um formato incompatível.");

        var validated = new List<(TransferredAccount Entry, byte[] AuthJson)>();
        foreach (var entry in transfer.Accounts)
        {
            byte[] authJson;
            try { authJson = Convert.FromBase64String(entry.AuthJsonBase64); }
            catch (FormatException ex) { throw new InvalidDataException("O arquivo de importação contém uma credencial inválida.", ex); }
            if (!IsRecognizableAuthFile(AuthJsonReader.TryRead(authJson)))
                throw new InvalidDataException("O arquivo de importação contém um auth.json inválido.");
            validated.Add((entry, authJson));
        }

        foreach (var (entry, authJson) in validated)
        {
            var profile = AddFromAuthJson(authJson, entry.Nickname);
            profile.SortOrder = entry.SortOrder;
        }

        _store.SaveAll(Profiles);
        _audit.Record("import", "ok", transfer.Accounts.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return transfer.Accounts.Count;
    }

    public void Rename(Guid id, string nickname)
    {
        var p = Profiles.FirstOrDefault(x => x.Id == id);
        if (p is null) return;
        p.Nickname = nickname?.Trim() ?? string.Empty;
        _store.SaveAll(Profiles);
        _audit.Record("rename", "ok", p.DisplayName);
    }

    public void Remove(Guid id)
    {
        var p = Profiles.FirstOrDefault(x => x.Id == id);
        if (p is null) return;
        _vault.DeleteBlob(id);
        Profiles.Remove(p);
        _store.SaveAll(Profiles);
        _audit.Record("remove", "ok", p.DisplayName);
    }

    public void MarkNeedsReLogin(Guid id)
    {
        var p = Profiles.FirstOrDefault(x => x.Id == id);
        if (p is null) return;
        p.HealthStatus = HealthStatus.NeedsReLogin;
        p.LastError = ErrorInfo.Create(ErrorCategory.RefreshTokenExpired,
            "Marcado manualmente como precisa re-login.", _clock.UtcNow);
        _store.SaveAll(Profiles);
    }

    /// <summary>Marca um perfil como "usado" agora; o selo visual expira sozinho após 24h.</summary>
    public void MarkUsed(Guid id)
    {
        var p = Profiles.FirstOrDefault(x => x.Id == id);
        if (p is null) return;
        p.MarkedUsedAt = _clock.UtcNow;
        _store.SaveAll(Profiles);
        _audit.Record("mark-used", "ok", p.DisplayName);
    }

    /// <summary>Remove o selo de "usado" manualmente, antes da expiração natural de 24h.</summary>
    public void UnmarkUsed(Guid id)
    {
        var p = Profiles.FirstOrDefault(x => x.Id == id);
        if (p is null) return;
        p.MarkedUsedAt = null;
        _store.SaveAll(Profiles);
        _audit.Record("unmark-used", "ok", p.DisplayName);
    }

    /// <summary>
    /// Reordena os perfis conforme a lista de ids fornecida (ex.: resultado de um drag-and-drop),
    /// atribuindo <see cref="ProfileMetadata.SortOrder"/> sequencial. Ids desconhecidos são ignorados.
    /// </summary>
    public void Reorder(IReadOnlyList<Guid> orderedIds)
    {
        ArgumentNullException.ThrowIfNull(orderedIds);
        var order = 0;
        foreach (var id in orderedIds)
        {
            var p = Profiles.FirstOrDefault(x => x.Id == id);
            if (p is null) continue;
            p.SortOrder = order++;
        }
        _store.SaveAll(Profiles);
        _audit.Record("reorder", "ok");
    }

    public void Save() => _store.SaveAll(Profiles);

    private TransferredAccount ToTransferredAccount(ProfileMetadata profile) => new()
    {
        Nickname = profile.Nickname,
        SortOrder = profile.SortOrder,
        AuthJsonBase64 = Convert.ToBase64String(_vault.LoadBlob(profile.Id)),
    };

    private static string SerializeTransfer(IReadOnlyList<TransferredAccount> accounts) =>
        JsonSerializer.Serialize(new AccountTransferDocument { Accounts = accounts.ToList() }, new JsonSerializerOptions
        {
            WriteIndented = true,
        });

    private static bool IsRecognizableAuthFile(AuthFileInfo? file) =>
        file is not null && (!string.IsNullOrWhiteSpace(file.AuthMode) || !string.IsNullOrWhiteSpace(file.IdToken));
}

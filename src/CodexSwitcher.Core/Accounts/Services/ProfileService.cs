using CodexSwitcher.Core.Accounts.Formatting;
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
using CodexSwitcher.Core.Routing.Models;
using CodexSwitcher.Core.Routing.Services;
using CodexSwitcher.Core.Accounts.Contracts;
using CodexSwitcher.Core.Accounts.Models;
using CodexSwitcher.Core.Common.Errors;
using System.Text.Json;

namespace CodexSwitcher.Core.Accounts.Services;

/// <summary>
/// Fachada de alto nível sobre cofre + metadados + reconciliação. Mantém a lista de perfis em
/// memória e coordena adicionar/renomear/remover/adotar, deduplicando por <c>sub</c>. Os
/// ViewModels dependem só desta classe e do <see cref="SwitchService"/>/<see cref="RefreshService"/>.
/// </summary>
public sealed class ProfileService
{
    private readonly IVaultService _vault;
    private readonly IProfileStore _store;
    private readonly ReconciliationService _reconciliation;
    private readonly IFileSystem _fs;
    private readonly CodexPaths _paths;
    private readonly IClock _clock;
    private readonly IAuditLog _audit;
    private readonly ITotpCredentialStore? _totpStore;
    private readonly object _sync = new();

    public List<ProfileMetadata> Profiles { get; private set; } = [];

    public ProfileService(
        IVaultService vault, IProfileStore store, ReconciliationService reconciliation,
        IFileSystem fs, CodexPaths paths, IClock clock, IAuditLog audit,
        ITotpCredentialStore? totpStore = null)
    {
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _reconciliation = reconciliation ?? throw new ArgumentNullException(nameof(reconciliation));
        _fs = fs ?? throw new ArgumentNullException(nameof(fs));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _totpStore = totpStore;
    }

    /// <summary>Carrega os perfis do disco e reconcilia com o slot ativo.</summary>
    public ReconciliationResult Load(bool auditOrphans = false)
    {
        lock (_sync)
        {
            Profiles = _store.LoadAll();
            EnsureSortOrderInitialized();
            EnsureDetectedSubscriptions();
            if (auditOrphans)
            {
                AuditOrphanVaultBlobs();
            }
            return Reconcile();
        }
    }

    /// <summary>Audita blobs orfãos no cofre e registra auditoria se existirem.</summary>
    public void AuditOrphanVaultBlobs()
    {
        lock (_sync)
        {
            var orphanBlobs = DetectOrphanVaultBlobs();
            if (orphanBlobs.Count > 0)
            {
                _audit.Record("vault-audit", "orphans-detected", $"{orphanBlobs.Count} orphan vault blob(s) detected.");
            }
        }
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

    /// <summary>
    /// Garante que perfis existentes tenham metadados de assinatura detectados preenchidos
    /// a partir do cofre ou do slot ativo caso ainda estejam nulos (migração transparente pós-v0.1.2).
    /// </summary>
    public void EnsureDetectedSubscriptions()
    {
        var changed = false;
        var now = _clock.UtcNow;

        foreach (var profile in Profiles)
        {
            if (profile.DetectedSubscription is not null)
                continue;

            try
            {
                if (profile.IsActive && _fs.FileExists(_paths.ActiveAuthPath))
                {
                    var activeBytes = _fs.ReadAllBytes(_paths.ActiveAuthPath);
                    var sub = SubscriptionJwtClaimExtractor.Extract(activeBytes, now);
                    if (sub is not null)
                    {
                        profile.DetectedSubscription = sub;
                        changed = true;
                        continue;
                    }
                }

                if (_vault.Exists(profile.Id))
                {
                    var blob = _vault.LoadBlob(profile.Id);
                    var sub = SubscriptionJwtClaimExtractor.Extract(blob, now);
                    if (sub is not null)
                    {
                        profile.DetectedSubscription = sub;
                        changed = true;
                    }
                }
            }
            catch
            {
                // Silencioso em caso de falha de leitura pontual
            }
        }

        if (changed)
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
                var sub = SubscriptionJwtClaimExtractor.Extract(activeBytes, _clock.UtcNow);
                if (sub is not null) profile.DetectedSubscription = sub;
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

    /// <summary>
    /// Retorna detalhes read-only da conta não gerenciada no slot ativo (.codex/auth.json), se existir.
    /// Nunca expõe tokens; lê apenas email e plano (se disponíveis).
    /// </summary>
    public (bool Detected, string? Email, string? PlanType) GetUnmanagedActiveAccountInfo()
    {
        if (!HasUnmanagedActiveAccount())
            return (false, null, null);

        try
        {
            var bytes = _fs.ReadAllBytes(_paths.ActiveAuthPath);
            var (_, claims) = AuthJsonReader.Identify(bytes);
            return (true, claims.Email, claims.PlanType);
        }
        catch
        {
            return (true, null, null);
        }
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
            var sub = SubscriptionJwtClaimExtractor.Extract(authJson, _clock.UtcNow);
            if (sub is not null) existing.DetectedSubscription = sub;
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
            DetectedSubscription = SubscriptionJwtClaimExtractor.Extract(authJson, _clock.UtcNow),
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

    public void UpdateSubscriptionTracking(Guid id, SubscriptionTracking? tracking)
    {
        var p = Profiles.FirstOrDefault(x => x.Id == id);
        if (p is null) return;
        p.SubscriptionTracking = tracking;
        _store.SaveAll(Profiles);
        _audit.Record("subscription-tracking", "update", p.DisplayName);
    }

    public void Remove(Guid id)
    {
        lock (_sync)
        {
            var p = Profiles.FirstOrDefault(x => x.Id == id);
            if (p is null) return;
            _vault.DeleteBlob(id);
            _totpStore?.Delete(id);
            Profiles.Remove(p);
            _store.SaveAll(Profiles, ProfileSaveIntent.ExplicitDelete);
            _audit.Record("remove", "ok", p.DisplayName);
        }
    }

    /// <summary>
    /// Detecta GUIDs de credenciais presentes fisicamente no cofre mas ausentes no índice profiles.json.
    /// Invariante de segurança: blobs órfãos NUNCA são deletados automaticamente pelo app.
    /// </summary>
    public IReadOnlyList<Guid> DetectOrphanVaultBlobs()
    {
        lock (_sync)
        {
            var blobs = _vault.EnumerateBlobs();
            var knownIds = Profiles.Select(p => p.Id).ToHashSet();
            return blobs.Where(id => !knownIds.Contains(id)).ToList();
        }
    }

    /// <summary>
    /// Reconstitui perfis a partir de blobs órfãos descriptografáveis preservando os GUIDs originais.
    /// </summary>
    public int RecoverOrphanProfiles()
    {
        lock (_sync)
        {
            var orphans = DetectOrphanVaultBlobs();
            if (orphans.Count == 0) return 0;

            int recovered = 0;
            foreach (var orphanId in orphans)
            {
                try
                {
                    var bytes = _vault.LoadBlob(orphanId);
                    var (file, claims) = AuthJsonReader.Identify(bytes);
                    if (file is null && string.IsNullOrEmpty(claims.Sub))
                        continue;

                    var profile = new ProfileMetadata
                    {
                        Id = orphanId,
                        Nickname = claims.Email ?? string.Empty,
                        AccountEmail = claims.Email,
                        AccountSub = claims.Sub,
                        AuthMode = file?.AuthMode ?? "chatgpt",
                        PlanType = claims.PlanType ?? "free",
                        CreatedAt = file?.LastRefresh ?? _clock.UtcNow,
                        LastRefreshedAt = file?.LastRefresh,
                        HealthStatus = HealthStatus.Valid,
                        SortOrder = Profiles.Count == 0 ? 0 : Profiles.Max(p => p.SortOrder) + 1,
                        DetectedSubscription = SubscriptionJwtClaimExtractor.Extract(bytes, _clock.UtcNow),
                        BlobFingerprint = Fingerprint.Compute(bytes),
                    };

                    Profiles.Add(profile);
                    recovered++;
                }
                catch (Exception ex)
                {
                    _audit.Record("recovery", "failed-orphan", $"{orphanId:N}: {ex.Message}");
                }
            }

            if (recovered > 0)
            {
                _store.SaveAll(Profiles, ProfileSaveIntent.NormalUpdate);
                _audit.Record("recovery", "ok", $"{recovered} orphan profile(s) recovered.");
            }

            return recovered;
        }
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

    /// <summary>
    /// Reautentica um perfil existente no mesmo slot com novo auth.json.
    /// Valida o auth.json, atualiza o blob no cofre, reseta status para Valid, limpa erros,
    /// atualiza metadados/assinatura, e se o perfil for ativo, sincroniza para o slot ativo .codex\auth.json.
    /// </summary>
    public ProfileMetadata Reauthenticate(Guid id, byte[] authJson)
    {
        ArgumentNullException.ThrowIfNull(authJson);
        var p = Profiles.FirstOrDefault(x => x.Id == id)
            ?? throw new InvalidOperationException("Perfil não encontrado.");

        var (file, claims) = AuthJsonReader.Identify(authJson);
        if (!IsRecognizableAuthFile(file))
            throw new InvalidDataException("O arquivo fornecido não é um auth.json válido do Codex.");

        if (!string.IsNullOrEmpty(p.AccountSub) && !string.IsNullOrEmpty(claims.Sub) && p.AccountSub != claims.Sub)
        {
            var duplicate = Profiles.FirstOrDefault(x => x.Id != id && x.AccountSub == claims.Sub);
            if (duplicate is not null)
            {
                throw new InvalidOperationException($"Esta conta já está cadastrada como '{duplicate.DisplayName}'.");
            }
        }

        p.BlobFingerprint = _vault.SaveBlob(p.Id, authJson);
        if (!string.IsNullOrEmpty(claims.Sub)) p.AccountSub = claims.Sub;
        if (!string.IsNullOrEmpty(claims.Email)) p.AccountEmail = claims.Email;
        if (!string.IsNullOrEmpty(claims.PlanType)) p.PlanType = claims.PlanType;
        if (!string.IsNullOrEmpty(file?.AuthMode)) p.AuthMode = file.AuthMode;
        p.HealthStatus = HealthStatus.Valid;
        p.LastError = null;
        p.LastRefreshedAt = file?.LastRefresh ?? _clock.UtcNow;

        var sub = SubscriptionJwtClaimExtractor.Extract(authJson, _clock.UtcNow);
        if (sub is not null) p.DetectedSubscription = sub;

        if (p.IsActive && _fs != null && _paths != null)
        {
            _fs.WriteAllBytesAtomic(_paths.ActiveAuthPath, authJson);
        }

        _store.SaveAll(Profiles);
        _audit.Record("reauthenticate", "ok", p.DisplayName);
        return p;
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

    private static readonly JsonSerializerOptions s_transferJsonOptions = new() { WriteIndented = true };

    private static string SerializeTransfer(IReadOnlyList<TransferredAccount> accounts) =>
        JsonSerializer.Serialize(new AccountTransferDocument { Accounts = accounts.ToList() }, s_transferJsonOptions);

    private static bool IsRecognizableAuthFile(AuthFileInfo? file) =>
        file is not null && (!string.IsNullOrWhiteSpace(file.AuthMode) || !string.IsNullOrWhiteSpace(file.IdToken));
}

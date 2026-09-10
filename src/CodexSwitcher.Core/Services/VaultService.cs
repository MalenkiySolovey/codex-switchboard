using CodexSwitcher.Core.Abstractions;
using CodexSwitcher.Core.Security;

namespace CodexSwitcher.Core.Services;

/// <summary>
/// Cofre cifrado — <b>fonte da verdade</b> das credenciais. Cada auth.json é cifrado (via
/// <see cref="ISecretProtector"/>) e gravado atomicamente (via <see cref="IFileSystem"/>).
/// O auth.json é preservado byte a byte (blob opaco). Ver BUSINESS_RULES.md §2, §7, pontos 3 e 11.
/// </summary>
public sealed class VaultService
{
    private readonly ISecretProtector _protector;
    private readonly IFileSystem _fs;
    private readonly string _vaultDir;
    private readonly IProfileOperationCoordinator _coordinator;

    public IProfileOperationCoordinator Coordinator => _coordinator;

    public VaultService(
        ISecretProtector protector,
        IFileSystem fs,
        string vaultDir,
        IProfileOperationCoordinator? coordinator = null)
    {
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
        _fs = fs ?? throw new ArgumentNullException(nameof(fs));
        _vaultDir = vaultDir ?? throw new ArgumentNullException(nameof(vaultDir));
        _coordinator = coordinator ?? new ProfileOperationCoordinator();
    }

    /// <summary>Caminho do blob cifrado de um perfil.</summary>
    public string BlobPath(Guid profileId) => Path.Combine(_vaultDir, profileId.ToString("N") + ".bin");

    public bool Exists(Guid profileId) => _fs.FileExists(BlobPath(profileId));

    /// <summary>
    /// Cifra e grava o auth.json de um perfil no cofre (atômico). Retorna o fingerprint do
    /// conteúdo em claro (para reconciliação). Gravação imediata é obrigatória após todo refresh
    /// para não guardar refresh token rotacionado antigo (ponto 3).
    /// </summary>
    public string SaveBlob(Guid profileId, byte[] authJson)
    {
        ArgumentNullException.ThrowIfNull(authJson);
        var fingerprint = Fingerprint.Compute(authJson);
        SaveEncryptedFile(BlobPath(profileId), authJson);
        return fingerprint;
    }

    /// <summary>
    /// Compare-and-swap update for an inactive profile: only persists newAuthJson if the existing
    /// blob decodes to expectedOriginalFingerprint. Protected by <see cref="IProfileOperationCoordinator"/>
    /// to ensure true CAS atomicity across concurrent in-process operations.
    /// Returns (true, newFingerprint) on success, or (false, currentFingerprint) if a conflict occurred.
    /// Never used for active profiles without external generation coordination.
    /// </summary>
    public (bool Success, string? CurrentFingerprint) SaveBlobIfUnchanged(
        Guid profileId,
        string expectedOriginalFingerprint,
        byte[] newAuthJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedOriginalFingerprint);
        ArgumentNullException.ThrowIfNull(newAuthJson);

        using (_coordinator.Lock(profileId))
        {
            if (!Exists(profileId))
                return (false, null);

            var currentBytes = LoadBlob(profileId);
            var currentFingerprint = Fingerprint.Compute(currentBytes);

            if (!string.Equals(currentFingerprint, expectedOriginalFingerprint, StringComparison.OrdinalIgnoreCase))
            {
                return (false, currentFingerprint);
            }

            var newFingerprint = SaveBlob(profileId, newAuthJson);
            return (true, newFingerprint);
        }
    }

    /// <summary>
    /// Asynchronous compare-and-swap update for an inactive profile under coordinator lock.
    /// </summary>
    public async Task<(bool Success, string? CurrentFingerprint)> SaveBlobIfUnchangedAsync(
        Guid profileId,
        string expectedOriginalFingerprint,
        byte[] newAuthJson,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedOriginalFingerprint);
        ArgumentNullException.ThrowIfNull(newAuthJson);

        using (await _coordinator.LockAsync(profileId, cancellationToken).ConfigureAwait(false))
        {
            if (!Exists(profileId))
                return (false, null);

            var currentBytes = LoadBlob(profileId);
            var currentFingerprint = Fingerprint.Compute(currentBytes);

            if (!string.Equals(currentFingerprint, expectedOriginalFingerprint, StringComparison.OrdinalIgnoreCase))
            {
                return (false, currentFingerprint);
            }

            var newFingerprint = SaveBlob(profileId, newAuthJson);
            return (true, newFingerprint);
        }
    }

    /// <summary>Carrega e decifra o auth.json de um perfil. Lança <see cref="SecretDecryptionException"/> se não decifrar.</summary>
    public byte[] LoadBlob(Guid profileId) => LoadEncryptedFile(BlobPath(profileId));

    public void DeleteBlob(Guid profileId)
    {
        var path = BlobPath(profileId);
        if (_fs.FileExists(path))
            _fs.Delete(path);
    }

    /// <summary>Grava bytes cifrados atomicamente em um caminho arbitrário (ex.: backups do slot ativo).</summary>
    public void SaveEncryptedFile(string path, byte[] plaintext)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            _fs.CreateDirectory(dir);
        var cipher = _protector.Protect(plaintext);
        _fs.WriteAllBytesAtomic(path, cipher);
    }

    /// <summary>Lê e decifra bytes de um caminho arbitrário cifrado.</summary>
    public byte[] LoadEncryptedFile(string path)
    {
        var cipher = _fs.ReadAllBytes(path);
        return _protector.Unprotect(cipher);
    }
}

namespace CodexSwitcher.Core.Abstractions;

/// <summary>
/// Coordinates operations touching profile credentials (usage refresh, switching, CAS mutations).
/// Provides keyed asynchronous locks per profile with deterministic ordering across multi-profile
/// operations to prevent deadlocks and ensure application-level CAS atomicity.
/// </summary>
public interface IProfileOperationCoordinator
{
    /// <summary>
    /// Acquires an exclusive lock for the specified profile asynchronously.
    /// </summary>
    Task<IDisposable> LockAsync(Guid profileId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Acquires exclusive locks for two profiles in deterministic order (Guid comparison)
    /// to avoid deadlocks in two-profile operations like switching.
    /// If both profile IDs are identical, locks the profile once.
    /// </summary>
    Task<IDisposable> LockTwoAsync(Guid profileIdA, Guid profileIdB, CancellationToken cancellationToken = default);

    /// <summary>
    /// Synchronously acquires an exclusive lock for the specified profile.
    /// Used by synchronous CAS operations like <see cref=" Services.VaultService.SaveBlobIfUnchanged\/>.
 /// </summary>
 IDisposable Lock(Guid profileId);
}


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
using CodexSwitcher.Core.Accounts.Contracts;


namespace CodexSwitcher.Core.Accounts.Services;

/// <summary>
/// Thread-safe coordinator providing keyed asynchronous exclusive locks per profile.
/// Enforces deterministic Guid ordering for multi-profile locks to eliminate deadlocks.
/// Collects and disposes idle semaphores via reference counting to prevent leaks.
/// </summary>
public sealed class ProfileOperationCoordinator : IProfileOperationCoordinator
{
    private sealed class RefCountedSemaphore
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public int RefCount = 1;
    }

    private readonly Dictionary<Guid, RefCountedSemaphore> _semaphores = new();
    private readonly object _sync = new();

    public async Task<IDisposable> LockAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        RefCountedSemaphore entry;
        lock (_sync)
        {
            if (_semaphores.TryGetValue(profileId, out var existing))
            {
                existing.RefCount++;
                entry = existing;
            }
            else
            {
                entry = new RefCountedSemaphore();
                _semaphores[profileId] = entry;
            }
        }

        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (_sync)
            {
                entry.RefCount--;
                if (entry.RefCount == 0)
                {
                    _semaphores.Remove(profileId);
                    entry.Semaphore.Dispose();
                }
            }
            throw;
        }

        return new Releaser(this, profileId, entry);
    }

    public IDisposable Lock(Guid profileId)
    {
        RefCountedSemaphore entry;
        lock (_sync)
        {
            if (_semaphores.TryGetValue(profileId, out var existing))
            {
                existing.RefCount++;
                entry = existing;
            }
            else
            {
                entry = new RefCountedSemaphore();
                _semaphores[profileId] = entry;
            }
        }

        try
        {
            entry.Semaphore.Wait();
        }
        catch
        {
            lock (_sync)
            {
                entry.RefCount--;
                if (entry.RefCount == 0)
                {
                    _semaphores.Remove(profileId);
                    entry.Semaphore.Dispose();
                }
            }
            throw;
        }

        return new Releaser(this, profileId, entry);
    }

    public async Task<IDisposable> LockTwoAsync(Guid profileIdA, Guid profileIdB, CancellationToken cancellationToken = default)
    {
        if (profileIdA == profileIdB)
        {
            return await LockAsync(profileIdA, cancellationToken).ConfigureAwait(false);
        }

        var (first, second) = profileIdA.CompareTo(profileIdB) < 0
            ? (profileIdA, profileIdB)
            : (profileIdB, profileIdA);

        var lock1 = await LockAsync(first, cancellationToken).ConfigureAwait(false);
        try
        {
            var lock2 = await LockAsync(second, cancellationToken).ConfigureAwait(false);
            return new CombinedReleaser(lock1, lock2);
        }
        catch
        {
            lock1.Dispose();
            throw;
        }
    }

    private void Release(Guid profileId, RefCountedSemaphore entry)
    {
        lock (_sync)
        {
            entry.Semaphore.Release();
            entry.RefCount--;
            if (entry.RefCount == 0)
            {
                _semaphores.Remove(profileId);
                entry.Semaphore.Dispose();
            }
        }
    }

    private sealed class Releaser : IDisposable
    {
        private ProfileOperationCoordinator? _coordinator;
        private readonly Guid _profileId;
        private readonly RefCountedSemaphore _entry;
        private int _disposed;

        public Releaser(ProfileOperationCoordinator coordinator, Guid profileId, RefCountedSemaphore entry)
        {
            _coordinator = coordinator;
            _profileId = profileId;
            _entry = entry;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _coordinator?.Release(_profileId, _entry);
                _coordinator = null;
            }
        }
    }

    private sealed class CombinedReleaser : IDisposable
    {
        private IDisposable? _first;
        private IDisposable? _second;
        private int _disposed;

        public CombinedReleaser(IDisposable first, IDisposable second)
        {
            _first = first;
            _second = second;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Interlocked.Exchange(ref _second, null)?.Dispose();
                Interlocked.Exchange(ref _first, null)?.Dispose();
            }
        }
    }
}

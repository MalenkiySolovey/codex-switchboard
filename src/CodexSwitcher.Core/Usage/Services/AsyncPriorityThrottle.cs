namespace CodexSwitcher.Core.Usage.Services;

public enum UsagePriority
{
    Background = 0,
    Interactive = 1,
}

/// <summary>
/// Bounded concurrency throttle with priority queueing.
/// Ensures interactive (manual) requests preempt background polling tasks for the next available slot.
/// </summary>
public sealed class AsyncPriorityThrottle : IDisposable
{
    private readonly object _gate = new();
    private readonly int _maxConcurrency;
    private int _activeCount;
    private bool _disposed;

    private readonly LinkedList<TaskCompletionSource<IDisposable>> _highPriority = new();
    private readonly LinkedList<TaskCompletionSource<IDisposable>> _lowPriority = new();

    public int MaxConcurrency => _maxConcurrency;
    public int ActiveCount { get { lock (_gate) return _activeCount; } }
    public int WaitingHighPriorityCount { get { lock (_gate) return _highPriority.Count; } }
    public int WaitingLowPriorityCount { get { lock (_gate) return _lowPriority.Count; } }

    public AsyncPriorityThrottle(int maxConcurrency)
    {
        if (maxConcurrency < 1)
            throw new ArgumentOutOfRangeException(nameof(maxConcurrency), "Concurrency must be at least 1.");
        _maxConcurrency = maxConcurrency;
    }

    public ValueTask<IDisposable> AcquireAsync(UsagePriority priority, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromCanceled<IDisposable>(cancellationToken);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_activeCount < _maxConcurrency)
            {
                _activeCount++;
                return ValueTask.FromResult<IDisposable>(new Releaser(this));
            }

            var tcs = new TaskCompletionSource<IDisposable>(TaskCreationOptions.RunContinuationsAsynchronously);
            var queue = priority == UsagePriority.Interactive ? _highPriority : _lowPriority;
            var node = queue.AddLast(tcs);

            if (cancellationToken.CanBeCanceled)
            {
                var registration = cancellationToken.Register(() =>
                {
                    lock (_gate)
                    {
                        if (node.List is not null)
                        {
                            node.List.Remove(node);
                            tcs.TrySetCanceled(cancellationToken);
                        }
                    }
                });

                // Dispose registration once completed
                tcs.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);
            }

            return new ValueTask<IDisposable>(tcs.Task);
        }
    }

    private void Release()
    {
        TaskCompletionSource<IDisposable>? nextTcs = null;

        lock (_gate)
        {
            while (_highPriority.Count > 0)
            {
                var first = _highPriority.First!.Value;
                _highPriority.RemoveFirst();
                if (!first.Task.IsCompleted)
                {
                    nextTcs = first;
                    break;
                }
            }

            if (nextTcs is null)
            {
                while (_lowPriority.Count > 0)
                {
                    var first = _lowPriority.First!.Value;
                    _lowPriority.RemoveFirst();
                    if (!first.Task.IsCompleted)
                    {
                        nextTcs = first;
                        break;
                    }
                }
            }

            if (nextTcs is null)
            {
                _activeCount--;
                return;
            }
        }

        nextTcs.TrySetResult(new Releaser(this));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;

            foreach (var tcs in _highPriority)
                tcs.TrySetCanceled();
            _highPriority.Clear();

            foreach (var tcs in _lowPriority)
                tcs.TrySetCanceled();
            _lowPriority.Clear();
        }
    }

    private sealed class Releaser : IDisposable
    {
        private AsyncPriorityThrottle? _owner;

        public Releaser(AsyncPriorityThrottle owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.Release();
        }
    }
}

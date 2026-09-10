using CodexSwitcher.Core.Abstractions;

namespace CodexSwitcher.Core.Services;

/// <summary>
/// Thread-safe owner of the application lifetime cancellation token.
/// </summary>
public sealed class AppLifetime : IAppLifetime, IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private int _stopped;

    public CancellationToken ApplicationStopping => _cts.Token;

    public bool IsStopping => Volatile.Read(ref _stopped) != 0 || _cts.IsCancellationRequested;

    public void StopApplication()
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 0)
        {
            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException) { }
            catch (AggregateException) { }
        }
    }

    public void Dispose()
    {
        StopApplication();
        _cts.Dispose();
    }
}
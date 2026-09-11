using CodexSwitcher.Core.Common.Lifecycle;
using Microsoft.Extensions.Hosting;

namespace CodexSwitcher.App.Composition;

/// <summary>
/// Bridges <see cref="IHostApplicationLifetime"/> to the domain <see cref="IAppLifetime"/> contract.
/// Guarantees that there is exactly ONE stopping truth across the entire application runtime.
/// </summary>
public sealed class HostAppLifetimeAdapter : IAppLifetime
{
    private readonly IHostApplicationLifetime _hostLifetime;

    public HostAppLifetimeAdapter(IHostApplicationLifetime hostLifetime)
    {
        _hostLifetime = hostLifetime ?? throw new ArgumentNullException(nameof(hostLifetime));
    }

    public CancellationToken ApplicationStopping => _hostLifetime.ApplicationStopping;

    public bool IsStopping => _hostLifetime.ApplicationStopping.IsCancellationRequested;

    public void StopApplication() => _hostLifetime.StopApplication();
}

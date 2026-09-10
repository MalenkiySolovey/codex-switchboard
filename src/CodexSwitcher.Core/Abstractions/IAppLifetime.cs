namespace CodexSwitcher.Core.Abstractions;

/// <summary>
/// Controls and observes the application-level shutdown lifecycle.
/// </summary>
public interface IAppLifetime
{
    /// <summary>
    /// Triggered when application shutdown has been initiated.
    /// In-flight network operations and background polling tasks should observe this token.
    /// </summary>
    CancellationToken ApplicationStopping { get; }

    /// <summary>
    /// Gets a value indicating whether shutdown has commenced.
    /// </summary>
    bool IsStopping { get; }

    /// <summary>
    /// Commences application shutdown, signalling <see cref=ApplicationStopping/>.
    /// Idempotent.
    /// </summary>
    void StopApplication();
}
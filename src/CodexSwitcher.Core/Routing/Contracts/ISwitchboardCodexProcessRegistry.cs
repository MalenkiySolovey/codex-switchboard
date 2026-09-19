namespace CodexSwitcher.Core.Routing.Contracts;

/// <summary>
/// Thread-safe registry tracking child Codex app-server and CLI processes spawned by Codex Switchboard.
/// Enables excluding internal monitor processes from external user CLI remnant checks and facilitates
/// clean, bounded child process termination during application shutdown.
/// </summary>
public interface ISwitchboardCodexProcessRegistry
{
    /// <summary>Registers an owned child process ID.</summary>
    void RegisterOwnedProcess(int processId);

    /// <summary>Unregisters an owned child process ID when it completes or terminates.</summary>
    void UnregisterOwnedProcess(int processId);

    /// <summary>Determines whether the specified PID is owned by Switchboard.</summary>
    bool IsOwnedProcess(int processId);

    /// <summary>Returns a snapshot of all currently registered owned process IDs.</summary>
    IReadOnlyCollection<int> GetOwnedProcessIds();

    /// <summary>
    /// Terminates all currently registered child processes within the specified bounded timeout.
    /// Strictly operates only on registered owned child PIDs, never touching user Desktop or user CLI processes.
    /// </summary>
    Task TerminateAllOwnedProcessesAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
}

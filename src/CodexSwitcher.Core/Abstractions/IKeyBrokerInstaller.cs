namespace CodexSwitcher.Core.Abstractions;

/// <summary>
/// Manages the installation and integrity verification of CodexSwitchboard.KeyBroker.exe
/// at its stable location in %LOCALAPPDATA%\CodexSwitchboard\broker\.
/// </summary>
public interface IKeyBrokerInstaller
{
    /// <summary>
    /// Path to the stable broker executable.
    /// </summary>
    string BrokerExecutablePath { get; }

    /// <summary>
    /// Checks whether the broker executable exists at the stable path and matches trusted payload integrity.
    /// </summary>
    bool IsInstalledAndValid();

    /// <summary>
    /// Ensures that the trusted broker executable is installed and hardened at the stable path.
    /// Returns the verified executable path.
    /// </summary>
    string EnsureInstalled(string? explicitSourcePath = null);
}

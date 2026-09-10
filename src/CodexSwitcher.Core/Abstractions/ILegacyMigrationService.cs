namespace CodexSwitcher.Core.Abstractions;

using CodexSwitcher.Core.Models;

/// <summary>
/// Safely detects and migrates legacy data from %LOCALAPPDATA%\CodexSwitcher
/// to the isolated product root %LOCALAPPDATA%\CodexSwitchboard without mutating legacy files.
/// </summary>
public interface ILegacyMigrationService
{
    bool CanMigrate();
    Task<LegacyMigrationResult> MigrateAsync(CancellationToken ct = default);
}

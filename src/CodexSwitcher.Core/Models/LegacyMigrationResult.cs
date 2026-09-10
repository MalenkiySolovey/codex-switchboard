namespace CodexSwitcher.Core.Models;

/// <summary>
/// Result of a legacy data migration attempt from %LOCALAPPDATA%\CodexSwitcher to %LOCALAPPDATA%\CodexSwitchboard.
/// </summary>
public sealed record LegacyMigrationResult
{
    public bool Success { get; init; }
    public int MigratedProfilesCount { get; init; }
    public string Message { get; init; } = string.Empty;
    public bool DestinationAlreadyExisted { get; init; }
    public bool NoLegacyDataFound { get; init; }

    public static LegacyMigrationResult Succeeded(int count, string message = "") =>
        new() { Success = true, MigratedProfilesCount = count, Message = message };

    public static LegacyMigrationResult Failed(string message, bool destinationAlreadyExisted = false, bool noLegacyDataFound = false) =>
        new() { Success = false, Message = message, DestinationAlreadyExisted = destinationAlreadyExisted, NoLegacyDataFound = noLegacyDataFound };
}

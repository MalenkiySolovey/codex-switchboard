using System;

namespace CodexSwitcher.Core.Common.Errors;

/// <summary>
/// Thrown when a persistence write operation violates the data-loss safety invariant
/// by attempting to silently shrink the profile count without explicit delete intent.
/// </summary>
public sealed class ProfileTruncationException : InvalidOperationException
{
    public int ExistingCount { get; }
    public int AttemptedCount { get; }

    public ProfileTruncationException(int existingCount, int attemptedCount)
        : base($"Data-loss safety invariant violated: attempted to save {attemptedCount} profile(s) over an existing index of {existingCount} profile(s) under NormalUpdate intent. Disk was not modified.")
    {
        ExistingCount = existingCount;
        AttemptedCount = attemptedCount;
    }
}

/// <summary>
/// Thrown when the profile index is corrupt on disk and cannot be safely recovered from rolling backups.
/// Fails safe to prevent silent conversion to an empty account list.
/// </summary>
public sealed class CorruptProfileIndexException : InvalidOperationException
{
    public CorruptProfileIndexException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

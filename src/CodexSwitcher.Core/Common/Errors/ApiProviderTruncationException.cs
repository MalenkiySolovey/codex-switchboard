using System;

namespace CodexSwitcher.Core.Common.Errors;

/// <summary>
/// Thrown when an API provider persistence write operation violates the data-loss safety invariant
/// by attempting to silently shrink the API provider profile count without explicit delete intent.
/// </summary>
public sealed class ApiProviderTruncationException : InvalidOperationException
{
    public int ExistingCount { get; }
    public int AttemptedCount { get; }

    public ApiProviderTruncationException(int existingCount, int attemptedCount)
        : base($"Data-loss safety invariant violated: attempted to save {attemptedCount} API provider profile(s) over an existing index of {existingCount} profile(s) under NormalUpdate intent. Disk was not modified.")
    {
        ExistingCount = existingCount;
        AttemptedCount = attemptedCount;
    }
}

/// <summary>
/// Thrown when the API provider index is corrupt on disk and cannot be safely recovered from rolling backups.
/// Fails safe to prevent silent conversion to an empty API provider list.
/// </summary>
public sealed class CorruptApiProviderIndexException : InvalidOperationException
{
    public CorruptApiProviderIndexException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

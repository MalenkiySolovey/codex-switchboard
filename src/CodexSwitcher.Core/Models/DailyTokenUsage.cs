namespace CodexSwitcher.Core.Models;

/// <summary>
/// Server-reported token activity for a single calendar date bucket.
/// Preserves the raw transport date string and does NOT invent timezone conversions.
/// </summary>
public sealed record DailyTokenUsage(
    string StartDateRaw,
    DateOnly? ParsedDate,
    long Tokens);

using System;

namespace CodexSwitcher.Core.Providers.Models;

/// <summary>
/// Status of verified evidence for a given capability.
/// </summary>
public enum CapabilityEvidenceState
{
    Unknown = 0,
    Documented = 1,
    Declared = 2,
    ProbePassed = 3,
    ProbeFailed = 4
}

/// <summary>
/// Immutable record of capability evidence capturing verification state,
/// authoritative source, verification timestamp, and the Codex runtime identity.
/// </summary>
public sealed record CapabilityEvidence(
    CapabilityEvidenceState State,
    string Source,
    DateTimeOffset Timestamp,
    string? CodexRuntimeIdentity = null,
    string? Detail = null)
{
    public static CapabilityEvidence Unknown(string source = "unspecified") =>
        new(CapabilityEvidenceState.Unknown, source, DateTimeOffset.UtcNow);

    public static CapabilityEvidence Documented(string source, string? detail = null) =>
        new(CapabilityEvidenceState.Documented, source, DateTimeOffset.UtcNow, Detail: detail);

    public static CapabilityEvidence Declared(string source, string? detail = null) =>
        new(CapabilityEvidenceState.Declared, source, DateTimeOffset.UtcNow, Detail: detail);

    public static CapabilityEvidence ProbePassed(string source, string? runtimeIdentity = null, string? detail = null) =>
        new(CapabilityEvidenceState.ProbePassed, source, DateTimeOffset.UtcNow, runtimeIdentity, detail);

    public static CapabilityEvidence ProbeFailed(string source, string? runtimeIdentity = null, string? detail = null) =>
        new(CapabilityEvidenceState.ProbeFailed, source, DateTimeOffset.UtcNow, runtimeIdentity, detail);

    public bool IsSupported => State is CapabilityEvidenceState.Documented or CapabilityEvidenceState.Declared or CapabilityEvidenceState.ProbePassed;
}

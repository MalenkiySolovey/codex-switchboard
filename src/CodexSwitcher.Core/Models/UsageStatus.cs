namespace CodexSwitcher.Core.Models;

/// <summary>
/// Status of rate-limit / usage monitoring for an account.
/// </summary>
public enum UsageStatus
{
    Unknown = 0,
    Healthy = 1,
    AuthRequired = 2,
    RateLimited = 3,
    ProcessDown = 4,
    BackingOff = 5,
    Error = 6,
    UnsupportedAccountType = 7,
}

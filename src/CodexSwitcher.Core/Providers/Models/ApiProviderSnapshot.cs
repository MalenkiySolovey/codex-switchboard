using CodexSwitcher.Core.Accounts.Contracts;
using CodexSwitcher.Core.Accounts.Formatting;
using CodexSwitcher.Core.Accounts.Models;
using CodexSwitcher.Core.Accounts.Services;
using CodexSwitcher.Core.Common.Dispatcher;
using CodexSwitcher.Core.Common.Enums;
using CodexSwitcher.Core.Common.Environment;
using CodexSwitcher.Core.Common.Errors;
using CodexSwitcher.Core.Common.Lifecycle;
using CodexSwitcher.Core.Common.Logging;
using CodexSwitcher.Core.Common.Storage;
using CodexSwitcher.Core.Common.Time;
using CodexSwitcher.Core.Providers.Catalog;
using CodexSwitcher.Core.Providers.Contracts;
using CodexSwitcher.Core.Providers.Models;
using CodexSwitcher.Core.Providers.Services;
using CodexSwitcher.Core.Routing.Contracts;
using CodexSwitcher.Core.Routing.Services;
using CodexSwitcher.Core.Security.Secrets;
using CodexSwitcher.Core.Security.Totp;
using CodexSwitcher.Core.Security.Verification;
using CodexSwitcher.Core.Settings.Contracts;
using CodexSwitcher.Core.Settings.Models;
using CodexSwitcher.Core.Threads.Contracts;
using CodexSwitcher.Core.Threads.Models;
using CodexSwitcher.Core.Transfer.Contracts;
using CodexSwitcher.Core.Transfer.Models;
using CodexSwitcher.Core.Transfer.Services;
using CodexSwitcher.Core.Usage.Contracts;
using CodexSwitcher.Core.Usage.Formatting;
using CodexSwitcher.Core.Usage.Models;
using CodexSwitcher.Core.Usage.Services;
using CodexSwitcher.Core.Routing.Models;

namespace CodexSwitcher.Core.Providers.Models;

/// <summary>
/// Normalized outcome of inspecting an API provider.
/// Decouples presentation and domain logic from provider-specific response JSON.
/// </summary>
public sealed record ApiProviderSnapshot
{
    /// <summary>Connection and reachability status.</summary>
    public HealthStatus ConnectionStatus { get; init; } = HealthStatus.Unknown;

    /// <summary>Available models discovered via inspection (if supported).</summary>
    public IReadOnlyList<string> Models { get; init; } = Array.Empty<string>();

    /// <summary>Remaining balance in currency units if known.</summary>
    public decimal? Balance { get; init; }

    /// <summary>Currency code (e.g. "USD", "CNY") if known.</summary>
    public string? Currency { get; init; }

    /// <summary>Total credits used if reported by provider.</summary>
    public decimal? UsedCredits { get; init; }

    /// <summary>Remaining credits if reported separately from balance.</summary>
    public decimal? RemainingCredits { get; init; }

    /// <summary>Hard credit limit if reported by provider.</summary>
    public decimal? CreditLimit { get; init; }

    /// <summary>Generic usage metric if reported.</summary>
    public decimal? Usage { get; init; }

    /// <summary>Expected quota or credit reset timestamp (UTC) if known.</summary>
    public DateTimeOffset? ResetAt { get; init; }

    /// <summary>Status of individual capabilities (e.g. models, balance, usage).</summary>
    public IReadOnlyDictionary<string, CapabilityStatus> Capabilities { get; init; } =
        new Dictionary<string, CapabilityStatus>();

    /// <summary>Timestamp when this snapshot was captured (UTC).</summary>
    public DateTimeOffset LastCheckedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Sanitized error message if inspection failed. Never contains secrets or URLs with tokens.</summary>
    public string? Error { get; init; }
}

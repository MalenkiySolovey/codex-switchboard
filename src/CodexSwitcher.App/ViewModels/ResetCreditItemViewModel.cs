using CodexSwitcher.App.Localization;
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
using CodexSwitcher.Core.Routing.Models;
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
using CodexSwitcher.Core.Usage.Services;
using CodexSwitcher.Infra.Accounts.Storage;
using CodexSwitcher.Infra.Codex.Routing;
using CodexSwitcher.Infra.Codex.Runtime;
using CodexSwitcher.Infra.Codex.Threads;
using CodexSwitcher.Infra.Codex.Usage;
using CodexSwitcher.Infra.Common.Logging;
using CodexSwitcher.Infra.Common.Paths;
using CodexSwitcher.Infra.Common.Storage;
using CodexSwitcher.Infra.Common.Time;
using CodexSwitcher.Infra.Providers.Inspection;
using CodexSwitcher.Infra.Providers.Secrets;
using CodexSwitcher.Infra.Providers.Storage;
using CodexSwitcher.Infra.Scheduling;
using CodexSwitcher.Infra.Security.Dpapi;
using CodexSwitcher.Infra.Security.Hardening;
using CodexSwitcher.Infra.Security.Totp;
using CodexSwitcher.Infra.Settings;
using CodexSwitcher.Core.Usage.Formatting;
using CodexSwitcher.Core.Usage.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace CodexSwitcher.App.ViewModels;

/// <summary>
/// ViewModel representing an individual reset credit item displayed in the details flyout.
/// Completely decoupled from opaque internal IDs; never displays or logs sensitive data.
/// </summary>
public sealed partial class ResetCreditItemViewModel : ObservableObject
{
    private static Strings Loc => Strings.Current;

    private static readonly SolidColorBrush NormalBrush = new(Color.FromArgb(255, 16, 124, 65));
    private static readonly SolidColorBrush WarningBrush = new(Color.FromArgb(255, 216, 59, 1));
    private static readonly SolidColorBrush CriticalBrush = new(Color.FromArgb(255, 168, 0, 0));

    public string Title { get; }
    public string Status { get; }
    public DateTimeOffset? GrantedAt { get; }
    public DateTimeOffset? ExpiresAt { get; }
    public CreditExpiryUrgency Urgency { get; }
    public string ExpiryText { get; }
    public SolidColorBrush UrgencyBrush { get; }

    public ResetCreditItemViewModel(RateLimitResetCredit credit, DateTimeOffset now)
    {
        Title = !string.IsNullOrWhiteSpace(credit.Title)
            ? credit.Title!
            : (Loc.Pt ? "Crédito de reinício" : "Reset credit");

        Status = credit.Status;
        GrantedAt = credit.GrantedAt;
        ExpiresAt = credit.ExpiresAt;

        Urgency = UsageCountdownFormatter.GetExpiryUrgency(credit.ExpiresAt, now);
        ExpiryText = UsageCountdownFormatter.FormatCreditExpiry(credit.ExpiresAt, now, culture: null, pt: Loc.Pt);

        UrgencyBrush = Urgency switch
        {
            CreditExpiryUrgency.Critical or CreditExpiryUrgency.Expired => CriticalBrush,
            CreditExpiryUrgency.Warning => WarningBrush,
            _ => NormalBrush
        };
    }
}

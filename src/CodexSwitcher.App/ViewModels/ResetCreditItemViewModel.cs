using CodexSwitcher.App.Localization;
using CodexSwitcher.Core.Models;
using CodexSwitcher.Core.Support;
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

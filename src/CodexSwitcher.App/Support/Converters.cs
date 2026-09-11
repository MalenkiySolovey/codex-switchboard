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
using CodexSwitcher.Core.Usage.Formatting;
using CodexSwitcher.Core.Usage.Models;
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
using CodexSwitcher.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace CodexSwitcher.App.Support;

internal static class Brushes
{
    public static Brush Resource(string key) =>
        Application.Current.Resources[key] as Brush ?? new SolidColorBrush(Microsoft.UI.Colors.Gray);
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is Visibility.Visible;
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is Visibility.Collapsed;
}

public sealed class BadgeToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var loc = Localization.Strings.Current;
        return value is AccountBadge b ? b switch
        {
            AccountBadge.ActiveNow => loc.BadgeActiveNow,
            AccountBadge.CredentialActive => loc.CredentialActiveBadge,
            AccountBadge.Healthy => loc.BadgeHealthy,
            AccountBadge.NeedsReLogin => loc.BadgeNeedsReLogin,
            AccountBadge.Error => loc.BadgeError,
            _ => loc.BadgeUnavailable,
        } : string.Empty;
    }
    public object ConvertBack(object value, Type t, object p, string l) => throw new NotSupportedException();
}

public sealed class BadgeToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        Brushes.Resource(value is AccountBadge b ? b switch
        {
            AccountBadge.ActiveNow => "BrandAccentBrush",
            AccountBadge.CredentialActive => "HealthWarnBrush",
            AccountBadge.Healthy => "HealthOkBrush",
            AccountBadge.NeedsReLogin => "HealthDangerBrush",
            AccountBadge.Error => "HealthDangerBrush",
            _ => "TextFillColorTertiaryBrush",
        } : "TextFillColorTertiaryBrush");
    public object ConvertBack(object value, Type t, object p, string l) => throw new NotSupportedException();
}

public sealed class ActiveToBackgroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value switch
        {
            AccountItemViewModel { IsActive: true, IsRoutingActive: true } => Brushes.Resource("BrandAccentSoftBrush"),
            _ => Brushes.Resource("CardBackgroundFillColorDefaultBrush"),
        };
    public object ConvertBack(object value, Type t, object p, string l) => throw new NotSupportedException();
}

/// <summary>Borda do card: ativa (acento) > marcada como usada nas últimas 24h (verde) > padrão.</summary>
public sealed class CardBorderBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value switch
        {
            AccountItemViewModel { IsActive: true, IsRoutingActive: true } => Brushes.Resource("BrandAccentBrush"),
            AccountItemViewModel { IsActive: true, IsRoutingActive: false } => Brushes.Resource("HealthWarnBrush"),
            AccountItemViewModel { IsMarkedUsed: true } => Brushes.Resource("HealthOkBrush"),
            _ => Brushes.Resource("CardStrokeColorDefaultBrush"),
        };
    public object ConvertBack(object value, Type t, object p, string l) => throw new NotSupportedException();
}

public sealed class IntToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is int intVal && parameter is string targetStr && int.TryParse(targetStr, out var targetInt))
        {
            return intVal == targetInt ? Visibility.Visible : Visibility.Collapsed;
        }
        return Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class QuotaProgressBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value switch
        {
            UsageWindowViewModel { IsExhausted: true } => Brushes.Resource("HealthDangerBrush"),
            UsageWindowViewModel { IsLowQuota: true } => Brushes.Resource("HealthWarnBrush"),
            _ => Brushes.Resource("BrandAccentBrush"),
        };
    public object ConvertBack(object value, Type t, object p, string l) => throw new NotSupportedException();
}

public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        !string.IsNullOrWhiteSpace(value as string) ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class BoolToChevronConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? "\uE70E" : "\uE70D"; // ChevronUp vs ChevronDown
    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class BoolToCopyGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? "\uE73E" : "\uE8C8"; // Checkmark vs Copy
    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class ApiCardBorderBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value switch
        {
            ApiProviderItemViewModel { IsTargetActive: true } => Brushes.Resource("BrandAccentBrush"),
            ApiProviderItemViewModel { HasSecret: false } => Brushes.Resource("HealthWarnBrush"),
            _ => Brushes.Resource("CardStrokeColorDefaultBrush"),
        };
    public object ConvertBack(object value, Type t, object p, string l) => throw new NotSupportedException();
}

public sealed class ApiCardBackgroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value switch
        {
            ApiProviderItemViewModel { IsTargetActive: true } => Brushes.Resource("BrandAccentSoftBrush"),
            _ => Brushes.Resource("CardBackgroundFillColorDefaultBrush"),
        };
    public object ConvertBack(object value, Type t, object p, string l) => throw new NotSupportedException();
}

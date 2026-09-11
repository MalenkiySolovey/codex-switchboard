using Microsoft.UI.Xaml;

namespace CodexSwitcher.App.Shell.Theme;

public enum AppTheme
{
    System,
    Light,
    Dark
}

/// <summary>
/// Presentation contract for application theme mapping and visual application.
/// </summary>
public interface IThemeService
{
    AppTheme CurrentTheme { get; }
    void Initialize(FrameworkElement? rootVisual);
    void SetTheme(AppTheme theme);
    AppTheme MapFromSetting(string? setting);
    string? MapToSetting(AppTheme theme);
    ElementTheme MapToElementTheme(AppTheme theme);
}

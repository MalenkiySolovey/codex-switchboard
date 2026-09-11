using CodexSwitcher.Core.Models;
using CodexSwitcher.Core.Services;
using Microsoft.UI.Xaml;

namespace CodexSwitcher.App.Shell.Theme;

/// <summary>
/// WinUI 3 implementation of <see cref="IThemeService"/>.
/// Applies runtime theme changes directly to FrameworkElement.RequestedTheme on the root visual.
/// </summary>
public sealed class WinUiThemeService : IThemeService
{
    private readonly SettingsStore? _settingsStore;
    private readonly AppSettings? _settings;
    private FrameworkElement? _rootVisual;

    public AppTheme CurrentTheme { get; private set; }

    public WinUiThemeService(SettingsStore? settingsStore = null, AppSettings? settings = null)
    {
        _settingsStore = settingsStore;
        _settings = settings;
        CurrentTheme = MapFromSetting(_settings?.ForcedTheme);
    }

    public void Initialize(FrameworkElement? rootVisual)
    {
        _rootVisual = rootVisual;
        ApplyThemeToVisual(_rootVisual, CurrentTheme);
    }

    public void SetTheme(AppTheme theme)
    {
        CurrentTheme = theme;
        if (_settings is not null && _settingsStore is not null)
        {
            _settings.ForcedTheme = MapToSetting(theme);
            _settingsStore.Save(_settings);
        }

        ApplyThemeToVisual(_rootVisual, theme);
    }

    public AppTheme MapFromSetting(string? setting)
    {
        if (string.Equals(setting, "Light", StringComparison.OrdinalIgnoreCase))
            return AppTheme.Light;
        if (string.Equals(setting, "Dark", StringComparison.OrdinalIgnoreCase))
            return AppTheme.Dark;
        return AppTheme.System;
    }

    public string? MapToSetting(AppTheme theme) => theme switch
    {
        AppTheme.Light => "Light",
        AppTheme.Dark => "Dark",
        _ => null
    };

    public ElementTheme MapToElementTheme(AppTheme theme) => theme switch
    {
        AppTheme.Light => ElementTheme.Light,
        AppTheme.Dark => ElementTheme.Dark,
        _ => ElementTheme.Default
    };

    private void ApplyThemeToVisual(FrameworkElement? visual, AppTheme theme)
    {
        if (visual is null) return;
        visual.RequestedTheme = MapToElementTheme(theme);
    }
}

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

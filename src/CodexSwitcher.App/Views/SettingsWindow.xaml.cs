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
using CodexSwitcher.App.Features.Settings;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using Windows.Storage.Pickers;

namespace CodexSwitcher.App.Views;

public sealed partial class SettingsWindow : Window
{
    public SettingsViewModel ViewModel { get; }

    public SettingsWindow(SettingsViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();

        Title = ViewModel.Loc.SettingsTitle;
        ConfigureWindow();
    }

    private void ConfigureWindow()
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(id);

            appWindow.Resize(new SizeInt32(660, 700));

            var work = DisplayArea.GetFromWindowId(id, DisplayAreaFallback.Nearest).WorkArea;
            appWindow.Move(new PointInt32(
                work.X + (work.Width - appWindow.Size.Width) / 2,
                work.Y + (work.Height - appWindow.Size.Height) / 2));

            var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico");
            if (System.IO.File.Exists(iconPath)) appWindow.SetIcon(iconPath);
        }
        catch
        {
            // Cosmetic window adjustments
        }
    }

    private async void OnBrowseExecutableClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".exe");
        picker.FileTypeFilter.Add(".cmd");
        picker.FileTypeFilter.Add(".bat");
        picker.FileTypeFilter.Add(".ps1");

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            CustomPathBox.Text = file.Path;
            ViewModel.ApplyCustomExecutable(file.Path);
        }
    }

    private void OnAutoDetectClick(object sender, RoutedEventArgs e)
    {
        CustomPathBox.Text = string.Empty;
        ViewModel.AutoDetect();
    }

    private void OnApplyCustomExecutableClick(object sender, RoutedEventArgs e)
    {
        ViewModel.ApplyCustomExecutable(CustomPathBox.Text);
    }

    private async void OnMigrateLegacyClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.MigrateLegacyDataAsync();
    }

    private bool _isHandlingToggle;

    private async void OnVerificationToggled(object sender, RoutedEventArgs e)
    {
        if (_isHandlingToggle) return;
        if (sender is ToggleSwitch toggle)
        {
            _isHandlingToggle = true;
            try
            {
                bool desired = toggle.IsOn;
                bool success = await ViewModel.SetRequireWindowsVerificationAsync(desired);
                if (!success)
                {
                    toggle.IsOn = !desired;
                }
            }
            finally
            {
                _isHandlingToggle = false;
            }
        }
    }

    private async void OnCheckAgainClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.CheckAgainAsync();
    }

    private async void OnOpenSignInOptionsClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.OpenWindowsSignInOptionsAsync();
    }

    private void OnReloadCatalogClick(object sender, RoutedEventArgs e)
    {
        ViewModel.ReloadCatalog();
    }
}

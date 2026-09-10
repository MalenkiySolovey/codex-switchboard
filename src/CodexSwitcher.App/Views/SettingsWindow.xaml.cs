using CodexSwitcher.App.ViewModels;
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
}

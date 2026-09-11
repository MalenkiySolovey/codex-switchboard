using System.Reflection;
using Xunit;

namespace CodexSwitcher.Core.Tests;

public sealed class ShellArchitectureBoundaryTests
{
    private static Assembly? LoadAppAssembly()
    {
        var baseDir = AppContext.BaseDirectory;
        var probePaths = new[]
        {
            Path.Combine(baseDir, "..", "..", "..", "..", "..", "src", "CodexSwitcher.App", "bin", "x64", "Release", "net10.0-windows10.0.19041.0", "win-x64", "CodexSwitchboard.dll"),
            Path.Combine(baseDir, "..", "..", "..", "..", "..", "src", "CodexSwitcher.App", "bin", "Release", "net10.0-windows10.0.19041.0", "win-x64", "CodexSwitchboard.dll"),
            Path.Combine(baseDir, "CodexSwitchboard.dll")
        };

        var existingCandidates = probePaths
            .Select(Path.GetFullPath)
            .Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToList();

        foreach (var full in existingCandidates)
        {
            try
            {
                return Assembly.LoadFrom(full);
            }
            catch
            {
                // Continue probing
            }
        }
        return null;
    }

    [Fact]
    public void ViewModels_AndCoreInfra_MustNotConstructContentDialog()
    {
        var coreTypes = typeof(CodexSwitcher.Core.Models.AppSettings).Assembly.GetTypes();
        var infraTypes = typeof(CodexSwitcher.Infra.FileAuditLog).Assembly.GetTypes();

        foreach (var t in coreTypes.Concat(infraTypes))
        {
            Assert.False(
                t.Name.Contains("ContentDialog", StringComparison.OrdinalIgnoreCase),
                $"Type {t.FullName} in Core/Infra must not reference or inherit ContentDialog");
        }

        var appAssembly = LoadAppAssembly();
        if (appAssembly is null) return;

        Type[] appTypes;
        try
        {
            appTypes = appAssembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            appTypes = ex.Types.Where(t => t != null).Select(t => t!).ToArray();
        }

        var forbiddenVmNames = new[]
        {
            "ShellViewModel",
            "AccountsViewModel",
            "ApiProvidersViewModel",
            "SettingsViewModel"
        };

        foreach (var vmName in forbiddenVmNames)
        {
            var vm = appTypes.FirstOrDefault(t => t.Name == vmName);
            if (vm is null) continue;

            foreach (var field in vm.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                Assert.False(
                    field.FieldType.Name.Contains("ContentDialog"),
                    $"ViewModel {vmName} must not hold field of type {field.FieldType.Name}");
            }

            foreach (var prop in vm.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                Assert.False(
                    prop.PropertyType.Name.Contains("ContentDialog"),
                    $"ViewModel {vmName} must not expose property of type {prop.PropertyType.Name}");
            }
        }
    }

    [Fact]
    public void DialogInterfaces_MustBeStrictlySegregatedBetweenFeatures()
    {
        var appAssembly = LoadAppAssembly();
        if (appAssembly is null) return;

        Type[] appTypes;
        try
        {
            appTypes = appAssembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            appTypes = ex.Types.Where(t => t != null).Select(t => t!).ToArray();
        }

        // AccountsViewModel: must depend on IAccountDialogService, NOT IProviderDialogService or IUiInteraction
        var accountsVm = appTypes.First(t => t.Name == "AccountsViewModel");
        var accountsCtor = accountsVm.GetConstructors().First();
        var accountsParamTypes = accountsCtor.GetParameters().Select(p => p.ParameterType.Name).ToList();

        Assert.Contains("IAccountDialogService", accountsParamTypes);
        Assert.DoesNotContain("IProviderDialogService", accountsParamTypes);
        Assert.DoesNotContain("IUiInteraction", accountsParamTypes);

        // ApiProvidersViewModel: must depend on IProviderDialogService, NOT IAccountDialogService or IUiInteraction
        var providersVm = appTypes.First(t => t.Name == "ApiProvidersViewModel");
        var providersCtor = providersVm.GetConstructors().First();
        var providersParamTypes = providersCtor.GetParameters().Select(p => p.ParameterType.Name).ToList();

        Assert.Contains("IProviderDialogService", providersParamTypes);
        Assert.DoesNotContain("IAccountDialogService", providersParamTypes);
        Assert.DoesNotContain("IUiInteraction", providersParamTypes);

        // SettingsViewModel: must depend on ISettingsDialogService, NOT IAccountDialogService or IProviderDialogService
        var settingsVm = appTypes.First(t => t.Name == "SettingsViewModel");
        var settingsCtor = settingsVm.GetConstructors().First();
        var settingsParamTypes = settingsCtor.GetParameters().Select(p => p.ParameterType.Name).ToList();

        Assert.Contains("ISettingsDialogService", settingsParamTypes);
        Assert.DoesNotContain("IAccountDialogService", settingsParamTypes);
        Assert.DoesNotContain("IProviderDialogService", settingsParamTypes);
        Assert.DoesNotContain("IUiInteraction", settingsParamTypes);
    }

    [Fact]
    public void AppNotificationService_SanitizesSensitiveSecretsAndManagesState()
    {
        var appAssembly = LoadAppAssembly();
        if (appAssembly is null) return;

        var serviceType = appAssembly.GetType("CodexSwitcher.App.Shell.State.AppNotificationService");
        Assert.NotNull(serviceType);

        var service = Activator.CreateInstance(serviceType)!;
        var isOpenProp = serviceType.GetProperty("IsOpen")!;
        var titleProp = serviceType.GetProperty("Title")!;
        var messageProp = serviceType.GetProperty("Message")!;

        Assert.False((bool)isOpenProp.GetValue(service)!);

        var showInfoMethod = serviceType.GetMethod("ShowInformation")!;
        showInfoMethod.Invoke(service, new object[] { "Header", "Normal informational message" });

        Assert.True((bool)isOpenProp.GetValue(service)!);
        Assert.Equal("Header", (string)titleProp.GetValue(service)!);
        Assert.Equal("Normal informational message", (string)messageProp.GetValue(service)!);

        // Sanitization of Bearer token
        var showErrorMethod = serviceType.GetMethod("ShowError")!;
        showErrorMethod.Invoke(service, new object[] { "Auth Error", "Failed request: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.secret" });

        var sanitizedMsg = (string)messageProp.GetValue(service)!;
        Assert.Contains("Bearer [REDACTED]", sanitizedMsg);
        Assert.DoesNotContain("eyJhbGci", sanitizedMsg);

        // Sanitization of API Key
        var showWarningMethod = serviceType.GetMethod("ShowWarning")!;
        showWarningMethod.Invoke(service, new object[] { "Key Alert", "Invalid sk-abcdef1234567890 supplied" });
        sanitizedMsg = (string)messageProp.GetValue(service)!;
        Assert.Contains("[REDACTED_API_KEY]", sanitizedMsg);
        Assert.DoesNotContain("sk-abcdef1234567890", sanitizedMsg);

        // Sanitization of TOTP URI
        showErrorMethod.Invoke(service, new object[] { "2FA Error", "Uri: otpauth://totp/Test:user?secret=JBSWY3DPEHPK3PXP" });
        sanitizedMsg = (string)messageProp.GetValue(service)!;
        Assert.Contains("[REDACTED_TOTP_URI]", sanitizedMsg);
        Assert.DoesNotContain("JBSWY3DPEHPK3PXP", sanitizedMsg);

        // Clear
        var clearMethod = serviceType.GetMethod("Clear")!;
        clearMethod.Invoke(service, null);
        Assert.False((bool)isOpenProp.GetValue(service)!);
        Assert.Empty((string)titleProp.GetValue(service)!);
        Assert.Empty((string)messageProp.GetValue(service)!);
    }

    [Fact]
    public void AppBusyService_HandlesSingle_Nested_AndOverlappingLeasesSafely()
    {
        var appAssembly = LoadAppAssembly();
        if (appAssembly is null) return;

        var busyType = appAssembly.GetType("CodexSwitcher.App.Shell.State.AppBusyService");
        Assert.NotNull(busyType);

        var busy = Activator.CreateInstance(busyType)!;
        var isBusyProp = busyType.GetProperty("IsBusy")!;
        var busyTextProp = busyType.GetProperty("BusyText")!;
        var beginMethod = busyType.GetMethod("Begin")!;

        Assert.False((bool)isBusyProp.GetValue(busy)!);
        Assert.Null(busyTextProp.GetValue(busy));

        // 1. Single lease
        var lease = (IDisposable)beginMethod.Invoke(busy, new object[] { "Loading data..." })!;
        Assert.True((bool)isBusyProp.GetValue(busy)!);
        Assert.Equal("Loading data...", (string)busyTextProp.GetValue(busy)!);

        lease.Dispose();
        Assert.False((bool)isBusyProp.GetValue(busy)!);
        Assert.Null(busyTextProp.GetValue(busy));

        // 2. Nested lease
        var outerLease = (IDisposable)beginMethod.Invoke(busy, new object[] { "Outer operation" })!;
        Assert.True((bool)isBusyProp.GetValue(busy)!);
        Assert.Equal("Outer operation", (string)busyTextProp.GetValue(busy)!);

        var innerLease = (IDisposable)beginMethod.Invoke(busy, new object[] { "Inner operation" })!;
        Assert.True((bool)isBusyProp.GetValue(busy)!);
        Assert.Equal("Inner operation", (string)busyTextProp.GetValue(busy)!);

        innerLease.Dispose();
        // After inner releases, outer is still active
        Assert.True((bool)isBusyProp.GetValue(busy)!);
        Assert.Equal("Outer operation", (string)busyTextProp.GetValue(busy)!);

        outerLease.Dispose();
        Assert.False((bool)isBusyProp.GetValue(busy)!);

        // 3. Overlapping asynchronous operations
        var lease1 = (IDisposable)beginMethod.Invoke(busy, new object[] { "Op 1" })!;
        var lease2 = (IDisposable)beginMethod.Invoke(busy, new object[] { "Op 2" })!;

        Assert.True((bool)isBusyProp.GetValue(busy)!);
        Assert.Equal("Op 2", (string)busyTextProp.GetValue(busy)!);

        lease1.Dispose();
        Assert.True((bool)isBusyProp.GetValue(busy)!);
        Assert.Equal("Op 2", (string)busyTextProp.GetValue(busy)!);

        lease2.Dispose();
        Assert.False((bool)isBusyProp.GetValue(busy)!);
        Assert.Null(busyTextProp.GetValue(busy));
    }

    [Fact]
    public void WinUiThemeService_MapsThemesFrameworkNeutrally()
    {
        var appAssembly = LoadAppAssembly();
        if (appAssembly is null) return;

        var themeType = appAssembly.GetType("CodexSwitcher.App.Shell.Theme.WinUiThemeService");
        Assert.NotNull(themeType);

        var themeService = Activator.CreateInstance(themeType, new object?[] { null, null })!;
        var mapFromSettingMethod = themeType.GetMethod("MapFromSetting")!;
        var mapToSettingMethod = themeType.GetMethod("MapToSetting")!;

        var lightTheme = mapFromSettingMethod.Invoke(themeService, new object[] { "Light" });
        var darkTheme = mapFromSettingMethod.Invoke(themeService, new object[] { "Dark" });
        var systemTheme = mapFromSettingMethod.Invoke(themeService, new object?[] { null });

        Assert.Equal("Light", lightTheme?.ToString());
        Assert.Equal("Dark", darkTheme?.ToString());
        Assert.Equal("System", systemTheme?.ToString());

        Assert.Equal("Light", mapToSettingMethod.Invoke(themeService, new object?[] { lightTheme }));
        Assert.Equal("Dark", mapToSettingMethod.Invoke(themeService, new object?[] { darkTheme }));
        Assert.Null(mapToSettingMethod.Invoke(themeService, new object?[] { systemTheme }));
    }

    [Fact]
    public void WindowLifecycleCoordinator_HandlesActivationAndForegroundTransitions()
    {
        var appAssembly = LoadAppAssembly();
        if (appAssembly is null) return;

        var coordType = appAssembly.GetType("CodexSwitcher.App.Shell.Windowing.WindowLifecycleCoordinator");
        Assert.NotNull(coordType);

        var coordinator = Activator.CreateInstance(coordType, new object?[] { null })!;
        var handleForegroundMethod = coordType.GetMethod("HandleForegroundChange")!;

        bool totpHidden = false;
        bool foregroundActive = false;

        // Foreground change: Minimized -> inactive + hide TOTP
        handleForegroundMethod.Invoke(coordinator, new object[] {
            true, // isVisible
            true, // isMinimized
            (Action<bool>)(setActive => foregroundActive = setActive),
            (Action)(() => totpHidden = true)
        });

        Assert.False(foregroundActive);
        Assert.True(totpHidden);

        // Foreground change: Normal visible -> active + no hide
        totpHidden = false;
        handleForegroundMethod.Invoke(coordinator, new object[] {
            true, // isVisible
            false, // isMinimized
            (Action<bool>)(setActive => foregroundActive = setActive),
            (Action)(() => totpHidden = true)
        });

        Assert.True(foregroundActive);
        Assert.False(totpHidden);
    }
}

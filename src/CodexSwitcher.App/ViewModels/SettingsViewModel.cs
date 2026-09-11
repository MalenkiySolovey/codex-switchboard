using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using CodexSwitcher.App.Localization;
using CodexSwitcher.App.Services;
using CodexSwitcher.Core.Abstractions;
using CodexSwitcher.Core.Models;
using CodexSwitcher.Core.Services;
using CodexSwitcher.Infra;
using Microsoft.UI.Xaml.Controls;

namespace CodexSwitcher.App.ViewModels;

public sealed class SettingsViewModel : INotifyPropertyChanged
{
    private readonly AppSettings _settings;
    private readonly SettingsStore _settingsStore;
    private readonly ICodexRuntimeResolver _runtimeResolver;
    private readonly ICodexCapabilityCache _capabilityCache;
    private readonly ILegacyMigrationService _migrationService;
    private readonly AppPaths _paths;
    private readonly IWindowsUserVerificationService _verificationService;
    private readonly ITotpRevealAuthorizationService _authService;
    private readonly IUiInteraction _ui;
    private readonly IProviderCatalogService? _catalogService;
    private readonly IKeyBrokerInstaller? _brokerInstaller;

    public Strings Loc => Strings.Current;

    public Action? OnMigrationCompleted { get; set; }

    public string AppName => "Codex Switchboard";
    public string AppVersion =>
        typeof(SettingsViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3)
        ?? "0.1.4";
    public string RepositoryUrl => "https://github.com/MalenkiySolovey/codex-switchboard";
    public string LicenseNotice => "MIT License • Copyright (c) 2026";
    public string IndependenceStatement => Loc.IndependenceNotice;
    public string DataRootPath => _paths.Root;

    private string _detectedExecutablePath = string.Empty;
    public string DetectedExecutablePath
    {
        get => _detectedExecutablePath;
        private set => SetField(ref _detectedExecutablePath, value);
    }

    private string _detectedVersion = string.Empty;
    public string DetectedVersion
    {
        get => _detectedVersion;
        private set => SetField(ref _detectedVersion, value);
    }

    private string _rateLimitsCapability = string.Empty;
    public string RateLimitsCapability
    {
        get => _rateLimitsCapability;
        private set => SetField(ref _rateLimitsCapability, value);
    }

    private string _accountActivityCapability = string.Empty;
    public string AccountActivityCapability
    {
        get => _accountActivityCapability;
        private set => SetField(ref _accountActivityCapability, value);
    }

    private string _customExecutablePath = string.Empty;
    public string CustomExecutablePath
    {
        get => _customExecutablePath;
        set => SetField(ref _customExecutablePath, value);
    }

    private bool _canMigrateLegacy;
    public bool CanMigrateLegacy
    {
        get => _canMigrateLegacy;
        private set => SetField(ref _canMigrateLegacy, value);
    }

    private bool _isMigrating;
    public bool IsMigrating
    {
        get => _isMigrating;
        private set
        {
            if (SetField(ref _isMigrating, value))
            {
                OnPropertyChanged(nameof(IsNotMigrating));
            }
        }
    }

    public bool IsNotMigrating => !_isMigrating;

    private string _statusMessage = string.Empty;
    public string StatusMessage
    {
        get => _statusMessage;
        set
        {
            if (SetField(ref _statusMessage, value))
            {
                HasStatus = !string.IsNullOrWhiteSpace(value);
            }
        }
    }

    private bool _hasStatus;
    public bool HasStatus
    {
        get => _hasStatus;
        private set => SetField(ref _hasStatus, value);
    }

    private InfoBarSeverity _statusSeverity = InfoBarSeverity.Informational;
    public InfoBarSeverity StatusSeverity
    {
        get => _statusSeverity;
        set => SetField(ref _statusSeverity, value);
    }

    public bool AlwaysConfirmSwitch
    {
        get => _settings.AlwaysConfirmSwitch;
        set
        {
            if (_settings.AlwaysConfirmSwitch != value)
            {
                _settings.AlwaysConfirmSwitch = value;
                _settingsStore.Save(_settings);
                OnPropertyChanged();
            }
        }
    }

    public sealed record DurationOption(int Minutes, string Label);

    public bool RequireWindowsVerificationForTotpReveal => _settings.RequireWindowsVerificationForTotpReveal;

    public IReadOnlyList<DurationOption> DurationOptions { get; }

    private DurationOption _selectedDuration;
    public DurationOption SelectedDuration
    {
        get => _selectedDuration;
        set
        {
            if (SetField(ref _selectedDuration, value))
            {
                _settings.TotpWindowsVerificationDurationMinutes = value.Minutes;
                _settingsStore.Save(_settings);
                _authService.RecordSettingsChanged();
            }
        }
    }

    private string _windowsHelloStatus = string.Empty;
    public string WindowsHelloStatus
    {
        get => _windowsHelloStatus;
        private set => SetField(ref _windowsHelloStatus, value);
    }

    private string _windowsPasswordStatus = string.Empty;
    public string WindowsPasswordStatus
    {
        get => _windowsPasswordStatus;
        private set => SetField(ref _windowsPasswordStatus, value);
    }

    private string _windowsVerificationStatus = string.Empty;
    public string WindowsVerificationStatus
    {
        get => _windowsVerificationStatus;
        private set => SetField(ref _windowsVerificationStatus, value);
    }

    private WindowsVerificationAvailability _availability = WindowsVerificationAvailability.Unknown;
    private bool _isPasswordSupported;

    public bool CanOpenSignInOptions => _availability != WindowsVerificationAvailability.Available;
    public bool IsDegradedProtection => RequireWindowsVerificationForTotpReveal && _availability != WindowsVerificationAvailability.Available && !_isPasswordSupported;

    public async Task CheckAgainAsync()
    {
        await RefreshVerificationMethodsStatusAsync();
    }

    public async Task RefreshVerificationMethodsStatusAsync()
    {
        try
        {
            var status = await _authService.GetVerificationMethodsStatusAsync();
            _availability = status.HelloAvailability;
            _isPasswordSupported = status.IsPasswordSupported;

            WindowsHelloStatus = status.HelloAvailability == WindowsVerificationAvailability.Available
                ? Loc.WindowsHelloAvailable
                : (status.IsPasswordSupported ? Loc.WindowsHelloNotConfiguredUsingPassword : Loc.WindowsHelloNotAvailable);

            WindowsPasswordStatus = status.IsPasswordSupported
                ? Loc.WindowsMethodReady
                : Loc.WindowsHelloNotAvailable;

            WindowsVerificationStatus = status.HelloAvailability == WindowsVerificationAvailability.Available
                ? Loc.WindowsHelloAvailable
                : (status.IsPasswordSupported
                    ? Loc.WindowsVerificationStatusDeviceNotPresent
                    : Loc.WindowsVerificationStatusUnsupported);
        }
        catch
        {
            WindowsVerificationStatus = Loc.WindowsVerificationStatusUnsupported;
        }

        OnPropertyChanged(nameof(CanOpenSignInOptions));
        OnPropertyChanged(nameof(IsDegradedProtection));
    }

    public async Task OpenWindowsSignInOptionsAsync()
    {
        await _ui.OpenWindowsSignInOptionsAsync();
    }

    private string _catalogVersion = string.Empty;
    public string CatalogVersion
    {
        get => _catalogVersion;
        private set => SetField(ref _catalogVersion, value);
    }

    private string _catalogSource = string.Empty;
    public string CatalogSource
    {
        get => _catalogSource;
        private set => SetField(ref _catalogSource, value);
    }

    private string _keyBrokerStatus = string.Empty;
    public string KeyBrokerStatus
    {
        get => _keyBrokerStatus;
        private set => SetField(ref _keyBrokerStatus, value);
    }

    public void RefreshCatalogDiagnostics()
    {
        if (_catalogService is not null)
        {
            var res = _catalogService.CurrentResult;
            CatalogVersion = $"{res.Catalog.CatalogVersion} (Schema {res.Catalog.SchemaVersion})";
            CatalogSource = $"{res.ActiveLayer} ({res.Catalog.Providers.Count} providers)";
        }
        else
        {
            CatalogVersion = "-";
            CatalogSource = "-";
        }

        if (_brokerInstaller is not null)
        {
            bool installed = _brokerInstaller.IsInstalledAndValid();
            KeyBrokerStatus = installed ? "Installed & Verified" : "Available";
        }
        else
        {
            KeyBrokerStatus = "-";
        }
    }

    public void ReloadCatalog()
    {
        if (_catalogService is not null)
        {
            var res = _catalogService.Reload();
            RefreshCatalogDiagnostics();
            StatusMessage = $"Catalog reloaded from {res.ActiveLayer}. Found {res.Catalog.Providers.Count} providers.";
            StatusSeverity = InfoBarSeverity.Success;
        }
    }

    public SettingsViewModel(
        AppSettings settings,
        SettingsStore settingsStore,
        ICodexRuntimeResolver runtimeResolver,
        ICodexCapabilityCache capabilityCache,
        ILegacyMigrationService migrationService,
        AppPaths paths,
        IWindowsUserVerificationService verificationService,
        ITotpRevealAuthorizationService authService,
        IUiInteraction ui,
        IProviderCatalogService? catalogService = null,
        IKeyBrokerInstaller? brokerInstaller = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _runtimeResolver = runtimeResolver ?? throw new ArgumentNullException(nameof(runtimeResolver));
        _capabilityCache = capabilityCache ?? throw new ArgumentNullException(nameof(capabilityCache));
        _migrationService = migrationService ?? throw new ArgumentNullException(nameof(migrationService));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _verificationService = verificationService ?? throw new ArgumentNullException(nameof(verificationService));
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _catalogService = catalogService;
        _brokerInstaller = brokerInstaller;

        RefreshCatalogDiagnostics();

        var currentMin = _settings.TotpWindowsVerificationDurationMinutes;
        var options = new List<DurationOption>
        {
            new(1, Loc.WindowsVerificationDurationMinute(1)),
            new(2, Loc.WindowsVerificationDurationMinute(2)),
            new(5, Loc.WindowsVerificationDurationMinute(5)),
            new(10, Loc.WindowsVerificationDurationMinute(10)),
            new(15, Loc.WindowsVerificationDurationMinute(15)),
            new(30, Loc.WindowsVerificationDurationMinute(30)),
        };
        if (!options.Any(o => o.Minutes == currentMin))
        {
            options.Add(new(currentMin, Loc.WindowsVerificationDurationMinute(currentMin)));
            options.Sort((a, b) => a.Minutes.CompareTo(b.Minutes));
        }
        DurationOptions = options;
        _selectedDuration = options.First(o => o.Minutes == currentMin);

        _customExecutablePath = _settings.CodexExecutablePathOverride ?? string.Empty;
        RefreshDiagnostics();
    }

    public void RefreshDiagnostics()
    {
        var candidate = _runtimeResolver.ResolveCurrentRuntime(_settings.CodexExecutablePathOverride);
        if (candidate is not null)
        {
            DetectedExecutablePath = candidate.ExecutablePath;
            DetectedVersion = candidate.Version ?? "Unknown";
            RateLimitsCapability = FormatCapability(candidate.Capabilities.RateLimitsRead);
            AccountActivityCapability = FormatCapability(candidate.Capabilities.AccountUsageRead);
        }
        else
        {
            DetectedExecutablePath = "Not found on system or PATH";
            DetectedVersion = "N/A";
            RateLimitsCapability = Loc.CapabilityUnavailable;
            AccountActivityCapability = Loc.CapabilityUnsupported;
        }

        CanMigrateLegacy = _migrationService.CanMigrate();

        _ = RefreshVerificationMethodsStatusAsync();
    }

    public async Task<bool> SetRequireWindowsVerificationAsync(bool enable)
    {
        if (_settings.RequireWindowsVerificationForTotpReveal == enable)
            return true;

        if (enable)
        {
            _settings.RequireWindowsVerificationForTotpReveal = true;
            _settingsStore.Save(_settings);
            _authService.RecordSettingsChanged();
            OnPropertyChanged(nameof(RequireWindowsVerificationForTotpReveal));
            OnPropertyChanged(nameof(IsDegradedProtection));
            return true;
        }

        // Desativação (ON -> OFF): HARD REQUIREMENT: Exige validação da senha do usuário atual do Windows!
        var verified = await _authService.VerifyPasswordToDisableProtectionAsync(Loc.WindowsVerificationDisablePasswordPrompt);
        if (verified)
        {
            _settings.RequireWindowsVerificationForTotpReveal = false;
            _settingsStore.Save(_settings);
            _authService.RecordSettingsChanged();
            OnPropertyChanged(nameof(RequireWindowsVerificationForTotpReveal));
            OnPropertyChanged(nameof(IsDegradedProtection));
            return true;
        }
        else
        {
            OnPropertyChanged(nameof(RequireWindowsVerificationForTotpReveal));
            OnPropertyChanged(nameof(IsDegradedProtection));
            StatusMessage = Loc.WindowsVerificationDisableFailed;
            StatusSeverity = InfoBarSeverity.Warning;
            return false;
        }
    }

    private string FormatVerificationAvailability(WindowsVerificationAvailability availability) => availability switch
    {
        WindowsVerificationAvailability.Available => Loc.WindowsVerificationStatusAvailable,
        WindowsVerificationAvailability.DeviceNotPresent => Loc.WindowsVerificationStatusDeviceNotPresent,
        WindowsVerificationAvailability.NotConfiguredForUser => Loc.WindowsVerificationStatusNotConfigured,
        WindowsVerificationAvailability.DisabledByPolicy => Loc.WindowsVerificationStatusDisabledByPolicy,
        WindowsVerificationAvailability.DeviceBusy => Loc.WindowsVerificationStatusDeviceBusy,
        WindowsVerificationAvailability.UnsupportedOperatingSystem => Loc.WindowsVerificationStatusUnsupported,
        _ => "Unknown"
    };

    private string FormatCapability(CapabilityStatus status) => status switch
    {
        CapabilityStatus.Supported => Loc.CapabilitySupported,
        CapabilityStatus.Unsupported => Loc.CapabilityUnsupported,
        _ => "Unknown"
    };

    public bool ApplyCustomExecutable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            _settings.CodexExecutablePathOverride = null;
            _settingsStore.Save(_settings);
            _capabilityCache.Clear();
            CustomExecutablePath = string.Empty;
            RefreshDiagnostics();
            StatusMessage = Loc.SettingsSavedMsg;
            StatusSeverity = InfoBarSeverity.Success;
            return true;
        }

        var trimmed = path.Trim();
        if (!_runtimeResolver.ValidateExecutable(trimmed, out var version, out var validationError))
        {
            StatusMessage = validationError ?? Loc.ExecutableInvalidMsg;
            StatusSeverity = InfoBarSeverity.Error;
            return false;
        }

        _settings.CodexExecutablePathOverride = trimmed;
        _settingsStore.Save(_settings);
        _capabilityCache.Clear();
        CustomExecutablePath = trimmed;
        RefreshDiagnostics();
        StatusMessage = Loc.SettingsSavedMsg;
        StatusSeverity = InfoBarSeverity.Success;
        return true;
    }

    public void AutoDetect()
    {
        ApplyCustomExecutable(null);
    }

    public async Task MigrateLegacyDataAsync()
    {
        if (IsMigrating) return;
        IsMigrating = true;
        StatusMessage = string.Empty;

        try
        {
            var result = await _migrationService.MigrateAsync();
            if (result.Success)
            {
                StatusMessage = Loc.MigrationSuccessMsg(result.MigratedProfilesCount);
                StatusSeverity = InfoBarSeverity.Success;
                CanMigrateLegacy = false;
                OnMigrationCompleted?.Invoke();
            }
            else
            {
                StatusMessage = Loc.MigrationFailedMsg(result.Message);
                StatusSeverity = InfoBarSeverity.Error;
            }
        }
        catch (Exception ex)
        {
            StatusMessage = Loc.MigrationFailedMsg(ex.Message);
            StatusSeverity = InfoBarSeverity.Error;
        }
        finally
        {
            IsMigrating = false;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}

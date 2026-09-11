using CodexSwitcher.Core.Accounts.Formatting;
using CodexSwitcher.Core.Accounts.Models;
using CodexSwitcher.Core.Common.Dispatcher;
using CodexSwitcher.Core.Common.Enums;
using CodexSwitcher.Core.Common.Environment;
using CodexSwitcher.Core.Common.Errors;
using CodexSwitcher.Core.Common.Lifecycle;
using CodexSwitcher.Core.Common.Logging;
using CodexSwitcher.Core.Common.Storage;
using CodexSwitcher.Core.Common.Time;
using CodexSwitcher.Core.Providers.Models;
using CodexSwitcher.Core.Routing.Models;
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
using CodexSwitcher.Core.Usage.Formatting;
using CodexSwitcher.Core.Usage.Models;
using CodexSwitcher.Infra.Codex.Threads;
using CodexSwitcher.Infra.Common.Logging;
using CodexSwitcher.Infra.Common.Paths;
using CodexSwitcher.Infra.Common.Storage;
using CodexSwitcher.Infra.Common.Time;
using CodexSwitcher.Infra.Scheduling;
using CodexSwitcher.Infra.Security.Dpapi;
using CodexSwitcher.Infra.Security.Hardening;
using CodexSwitcher.Infra.Security.Totp;
using CodexSwitcher.Infra.Settings;
using CodexSwitcher.Core.Routing.Contracts;
using CodexSwitcher.Core.Routing.Services;
using CodexSwitcher.Infra.Codex.Routing;
using CodexSwitcher.Infra.Codex.Runtime;
using CodexSwitcher.Core.Providers.Catalog;
using CodexSwitcher.Core.Providers.Contracts;
using CodexSwitcher.Core.Providers.Services;
using CodexSwitcher.Infra.Providers.Secrets;
using CodexSwitcher.Infra.Providers.Storage;
using CodexSwitcher.Core.Accounts.Contracts;
using CodexSwitcher.Core.Accounts.Services;
using CodexSwitcher.Infra.Accounts.Storage;
using CodexSwitcher.Core.Usage.Contracts;
using CodexSwitcher.Core.Usage.Services;
using CodexSwitcher.Infra.Codex.Usage;
using CodexSwitcher.App.Dialogs;
using CodexSwitcher.App.Features.Accounts;
using CodexSwitcher.App.Features.Providers;
using CodexSwitcher.App.Features.Settings;
using CodexSwitcher.App.Shell;
using CodexSwitcher.App.Shell.Routing;
using CodexSwitcher.App.Shell.State;
using CodexSwitcher.App.Shell.Theme;
using CodexSwitcher.App.Shell.Windowing;
using CodexSwitcher.Infra.Providers.Inspection;
using Microsoft.Extensions.DependencyInjection;

namespace CodexSwitcher.App.Services;

/// <summary>Raiz de composição (DI). Monta todos os serviços com os caminhos do app.</summary>
public static class AppHost
{
    public static IServiceProvider Services { get; private set; } = null!;

    public static IServiceProvider Build()
    {
        var paths = new AppPaths();
        paths.EnsureDirectories();
        DirectoryHardening.TryRestrictToCurrentUser(paths.Root);
        // Remove pastas efêmeras de login (WebView2/CODEX_HOME) que sobraram de sessões anteriores.
        TempCleanup.SweepLoginTemp(paths.TempRoot);

        var services = new ServiceCollection();

        services.AddSingleton(paths);
        services.AddSingleton(paths.Codex);
        services.AddSingleton<IFileSystem, PhysicalFileSystem>();
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<ISecretProtector>(_ => new DpapiSecretProtector());
        services.AddSingleton<IAuditLog>(_ => new FileAuditLog(paths.AuditLogPath));
        services.AddSingleton<ICodexConfigStore, ConfigTomlStore>();

        services.AddSingleton(sp => new SettingsStore(sp.GetRequiredService<IFileSystem>(), paths.SettingsPath));
        services.AddSingleton<ISettingsStore>(sp => sp.GetRequiredService<SettingsStore>());
        services.AddSingleton(sp => sp.GetRequiredService<SettingsStore>().Load());
        services.AddSingleton<ICodexCli>(sp =>
            new CodexCliRunner(sp.GetRequiredService<AppSettings>().CodexExecutablePathOverride));
        services.AddSingleton<IProcessManager, CodexProcessManager>();

        services.AddSingleton<IProfileOperationCoordinator, ProfileOperationCoordinator>();

        services.AddSingleton(sp => new VaultService(
            sp.GetRequiredService<ISecretProtector>(),
            sp.GetRequiredService<IFileSystem>(),
            paths.VaultDir,
            sp.GetRequiredService<IProfileOperationCoordinator>()));
        services.AddSingleton<IVaultService>(sp => sp.GetRequiredService<VaultService>());
        services.AddSingleton<ITotpCredentialStore>(sp => new TotpCredentialStore(
            sp.GetRequiredService<ISecretProtector>(),
            sp.GetRequiredService<IFileSystem>(),
            paths.TotpDir,
            sp.GetRequiredService<IProfileOperationCoordinator>()));

        services.AddSingleton<WindowHandleProvider>();
        services.AddSingleton<IWindowHandleProvider>(sp => sp.GetRequiredService<WindowHandleProvider>());
        services.AddSingleton<IWindowsUserVerificationService, WindowsUserVerificationService>();
        services.AddSingleton<IWindowsPasswordVerificationService, WindowsPasswordVerificationService>();
        services.AddSingleton<ITotpRevealAuthorizationService>(sp => new TotpRevealAuthorizationService(
            sp.GetRequiredService<AppSettings>(),
            sp.GetRequiredService<IWindowsUserVerificationService>(),
            sp.GetRequiredService<IWindowsPasswordVerificationService>()));

        services.AddSingleton(sp => new ProfileStore(sp.GetRequiredService<IFileSystem>(), paths.ProfilesPath));
        services.AddSingleton<IProfileStore>(sp => sp.GetRequiredService<ProfileStore>());
        services.AddSingleton(sp => new ReconciliationService(sp.GetRequiredService<IFileSystem>(), paths.Codex));
        services.AddSingleton<ProfileService>();

        services.AddSingleton(sp => new SwitchService(
            sp.GetRequiredService<VaultService>(),
            sp.GetRequiredService<ProfileStore>(),
            sp.GetRequiredService<IFileSystem>(),
            sp.GetRequiredService<IProcessManager>(),
            sp.GetRequiredService<ICodexConfigStore>(),
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<IAuditLog>(),
            paths.Codex,
            paths.BackupsDir,
            sp.GetRequiredService<IProfileOperationCoordinator>()));

        services.AddSingleton<AppLifetime>();
        services.AddSingleton<IAppLifetime>(sp => sp.GetRequiredService<AppLifetime>());

        services.AddSingleton<IUiDispatcher>(_ =>
        {
            var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            return queue is not null ? new WinUiDispatcher(queue) : ImmediateUiDispatcher.Instance;
        });

        services.AddSingleton<ICodexCapabilityCache, CodexCapabilityCache>();
        services.AddSingleton<ICodexRuntimeResolver>(sp => new CodexRuntimeResolver(
            sp.GetRequiredService<ICodexCapabilityCache>()));

        services.AddSingleton<IUsageCache>(sp => new UsageCache(sp.GetRequiredService<IFileSystem>(), paths.UsageCachePath));
        services.AddSingleton<ICodexUsageProvider>(sp => new CodexUsageProvider(
            sp.GetRequiredService<IFileSystem>(),
            paths.TempRoot,
            sp.GetRequiredService<AppSettings>().CodexExecutablePathOverride,
            capabilityCache: sp.GetRequiredService<ICodexCapabilityCache>(),
            executablePathAccessor: () => sp.GetRequiredService<AppSettings>().CodexExecutablePathOverride));
        services.AddSingleton<IUsageService>(sp => new UsageService(
            sp.GetRequiredService<ICodexUsageProvider>(),
            sp.GetRequiredService<IUsageCache>(),
            sp.GetRequiredService<VaultService>(),
            sp.GetRequiredService<IProfileOperationCoordinator>(),
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<IAppLifetime>()));
        services.AddSingleton(sp => new UsagePollingCoordinator(
            sp.GetRequiredService<IUsageService>(),
            sp.GetRequiredService<IClock>()));

        services.AddSingleton<ILegacyMigrationService>(sp => new LegacyMigrationService(
            sp.GetRequiredService<IFileSystem>(),
            paths.LegacyRoot,
            paths.Root));

        // Phase 10B/10C: Provider Catalog, DPAPI Secret Store, Routing Config, Target Switching & Thread Handoff
        services.AddSingleton<IKeyBrokerInstaller>(sp => new KeyBrokerInstaller(paths, sp.GetRequiredService<IFileSystem>()));
        services.AddSingleton<IApiKeySecretStore>(sp => new ApiKeySecretStore(
            sp.GetRequiredService<ISecretProtector>(),
            sp.GetRequiredService<IFileSystem>(),
            paths.ApiKeysDir,
            sp.GetRequiredService<IProfileOperationCoordinator>()));
        services.AddSingleton<IApiProviderStore>(sp => new ApiProviderStore(
            sp.GetRequiredService<IFileSystem>(),
            paths.ApiProvidersPath,
            sp.GetRequiredService<IApiKeySecretStore>()));

        services.AddSingleton(sp => new ProviderCatalogLoader(
            sp.GetRequiredService<IFileSystem>(),
            paths.CatalogPath,
            paths.CatalogSigPath,
            paths.CatalogPreviousPath,
            paths.LocalCatalogPath,
            "0.1.4"));
        services.AddSingleton<IProviderCatalogService, ProviderCatalogService>();
        services.AddSingleton<IDeclarativeProviderInspector>(sp => new DeclarativeProviderInspector());
        services.AddSingleton<IProviderModelCache, ProviderModelCache>();
        services.AddSingleton<IProviderInspectionService>(sp => new ProviderInspectionService(
            sp.GetRequiredService<IApiProviderStore>(),
            sp.GetRequiredService<IApiKeySecretStore>(),
            sp.GetRequiredService<IProviderCatalogService>(),
            sp.GetRequiredService<IDeclarativeProviderInspector>(),
            sp.GetRequiredService<IProviderModelCache>()));

        services.AddSingleton<ICodexRoutingConfigStore>(sp => new CodexRoutingConfigStore(
            sp.GetRequiredService<IFileSystem>(),
            paths));
        services.AddSingleton<ICodexActiveTargetResolver, CodexActiveTargetResolver>();

        services.AddSingleton<ICodexTargetSwitchService>(sp => new CodexTargetSwitchService(
            sp.GetRequiredService<SwitchService>(),
            sp.GetRequiredService<ICodexRoutingConfigStore>(),
            sp.GetRequiredService<IApiProviderStore>(),
            sp.GetRequiredService<IApiKeySecretStore>(),
            sp.GetRequiredService<IKeyBrokerInstaller>(),
            sp.GetRequiredService<IProcessManager>(),
            sp.GetRequiredService<IFileSystem>(),
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<IAuditLog>(),
            paths.Codex));

        services.AddSingleton<ICodexThreadHandoffService>(sp =>
        {
            var settings = sp.GetRequiredService<AppSettings>();
            var p = sp.GetRequiredService<AppPaths>();
            var resolver = sp.GetRequiredService<ICodexRuntimeResolver>();
            return new CodexThreadHandoffService(async () =>
            {
                var runtime = resolver.ResolveCurrentRuntime(settings.CodexExecutablePathOverride);
                var codexPath = !string.IsNullOrWhiteSpace(runtime?.ExecutablePath)
                    ? runtime.ExecutablePath
                    : (!string.IsNullOrWhiteSpace(settings.CodexExecutablePathOverride) ? settings.CodexExecutablePathOverride : "codex");
                var client = new CodexAppServerClient(codexPath, p.Codex.CodexHome);
                await client.StartAsync();
                return client;
            });
        });

        services.AddSingleton<CodexSwitcher.App.Dialogs.Shared.IDialogHost, CodexSwitcher.App.Dialogs.Shared.DialogHostContext>();
        services.AddSingleton<ICommonDialogService, CodexSwitcher.App.Dialogs.Common.CommonDialogService>();
        services.AddSingleton<IAccountDialogService, CodexSwitcher.App.Dialogs.Accounts.AccountDialogService>();
        services.AddSingleton<ITotpDialogService, CodexSwitcher.App.Dialogs.Totp.TotpDialogService>();
        services.AddSingleton<IProviderDialogService, CodexSwitcher.App.Dialogs.Providers.ProviderDialogService>();
        services.AddSingleton<ISettingsDialogService, CodexSwitcher.App.Dialogs.Settings.SettingsDialogService>();

        services.AddSingleton<IAppNotificationService, AppNotificationService>();
        services.AddSingleton<IAppBusyService, AppBusyService>();
        services.AddTransient<ActiveTargetPresentationCoordinator>();
        services.AddSingleton<IThemeService, WinUiThemeService>();
        services.AddSingleton<WindowChromeService>();
        services.AddSingleton<WindowLifecycleCoordinator>();

        services.AddTransient<TotpPresentationCoordinator>();
        services.AddTransient<AccountUsageCoordinator>();
        services.AddTransient<AccountsViewModel>();
        services.AddTransient<ApiProvidersViewModel>();
        services.AddTransient<ShellViewModel>();
        services.AddTransient<SettingsViewModel>();

        Services = services.BuildServiceProvider();
        return Services;
    }

    private static int _isShuttingDown;

    /// <summary>
    /// Idempotently coordinates clean, prompt application shutdown.
    /// Signals lifetime cancellation, stops timers, cancels in-flight app-server requests,
    /// and disposes singleton resources without blocking the UI thread.
    /// </summary>
    public static void Shutdown()
    {
        if (Interlocked.Exchange(ref _isShuttingDown, 1) != 0)
            return;

        try
        {
            // 1. Signal application stopping token to cancel in-flight operations
            var lifetime = Services?.GetService<IAppLifetime>() as AppLifetime;
            lifetime?.StopApplication();

            // 2. Invalidate TOTP reveal authorization session
            Services?.GetService<ITotpRevealAuthorizationService>()?.Invalidate();

            // 3. Stop usage coordinator and detach event subscribers
            if (Services?.GetService<UsagePollingCoordinator>() is { } coordinator)
            {
                coordinator.Stop();
            }

            // 4. Dispose the service provider (disposes UsageService, terminates child processes)
            if (Services is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
        catch
        {
            // Best-effort non-blocking shutdown
        }
    }
}

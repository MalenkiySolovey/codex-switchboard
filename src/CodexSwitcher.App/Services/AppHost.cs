using CodexSwitcher.App.ViewModels;
using CodexSwitcher.Core.Abstractions;
using CodexSwitcher.Core.Models;
using CodexSwitcher.Core.Services;
using CodexSwitcher.Infra;
using CodexSwitcher.Infra.Codex;
using CodexSwitcher.Infra.Io;
using CodexSwitcher.Infra.Processes;
using CodexSwitcher.Infra.Security;
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

        services.AddSingleton<IUiInteraction, UiInteractionService>();
        services.AddTransient<MainViewModel>();
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

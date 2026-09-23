using CodexSwitcher.App.Dialogs;
using CodexSwitcher.App.Dialogs.Accounts;
using CodexSwitcher.App.Dialogs.Common;
using CodexSwitcher.App.Dialogs.Providers;
using CodexSwitcher.App.Dialogs.Settings;
using CodexSwitcher.App.Dialogs.Shared;
using CodexSwitcher.App.Dialogs.Totp;
using CodexSwitcher.App.Features.Accounts;
using CodexSwitcher.App.Features.Providers;
using CodexSwitcher.App.Features.Settings;
using CodexSwitcher.App.Services;
using CodexSwitcher.App.Shell;
using CodexSwitcher.App.Shell.Routing;
using CodexSwitcher.App.Shell.State;
using CodexSwitcher.App.Shell.Theme;
using CodexSwitcher.App.Shell.Windowing;
using CodexSwitcher.Core.Accounts.Contracts;
using CodexSwitcher.Core.Accounts.Services;
using CodexSwitcher.Core.Common.Dispatcher;
using CodexSwitcher.Core.Common.Lifecycle;
using CodexSwitcher.Core.Common.Logging;
using CodexSwitcher.Core.Common.Storage;
using CodexSwitcher.Core.Common.Time;
using CodexSwitcher.Core.Providers.Catalog;
using CodexSwitcher.Core.Providers.Contracts;
using CodexSwitcher.Core.Providers.Services;
using CodexSwitcher.Core.Routing.Contracts;
using CodexSwitcher.Core.Routing.Services;
using CodexSwitcher.Core.Security.Secrets;
using CodexSwitcher.Core.Security.Totp;
using CodexSwitcher.Core.Security.Verification;
using CodexSwitcher.Core.Settings.Contracts;
using CodexSwitcher.Core.Settings.Models;
using CodexSwitcher.Core.Threads.Contracts;
using CodexSwitcher.Core.Transfer.Contracts;
using CodexSwitcher.Core.Transfer.Services;
using CodexSwitcher.Core.Usage.Contracts;
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
using CodexSwitcher.Infra.Security.Dpapi;
using CodexSwitcher.Infra.Security.Hardening;
using CodexSwitcher.Infra.Security.Totp;
using CodexSwitcher.Infra.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace CodexSwitcher.App.Composition;

/// <summary>
/// Modular DI service registrations for Codex Switchboard.
/// Organizes registrations by domain and infrastructure layer.
/// Prevents secondary container instantiation inside registration modules.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCoreInfrastructure(this IServiceCollection services, AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(paths);

        services.AddSingleton(paths);
        services.AddSingleton(paths.Codex);
        services.AddSingleton<IFileSystem, PhysicalFileSystem>();
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<ISecretProtector>(_ => new DpapiSecretProtector());
        services.AddSingleton<IAuditLog>(_ => new FileAuditLog(paths.AuditLogPath));

        services.AddSingleton<IUiDispatcher>(_ =>
        {
            try
            {
                var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
                return queue is not null ? new WinUiDispatcher(queue) : ImmediateUiDispatcher.Instance;
            }
            catch
            {
                return ImmediateUiDispatcher.Instance;
            }
        });

        services.AddSingleton<IAppLifetime, HostAppLifetimeAdapter>();

        return services;
    }

    public static IServiceCollection AddStorageInfrastructure(this IServiceCollection services, AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(paths);

        services.AddSingleton<ICodexConfigStore, ConfigTomlStore>();
        services.AddSingleton(sp => new SettingsStore(sp.GetRequiredService<IFileSystem>(), paths.SettingsPath));
        services.AddSingleton<ISettingsStore>(sp => sp.GetRequiredService<SettingsStore>());
        services.AddSingleton(sp => sp.GetRequiredService<SettingsStore>().Load());
        services.AddSingleton<IUsageCache>(sp => new UsageCache(sp.GetRequiredService<IFileSystem>(), paths.UsageCachePath));

        return services;
    }

    public static IServiceCollection AddCodexInfrastructure(this IServiceCollection services, AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(paths);

        services.AddSingleton<ISwitchboardCodexProcessRegistry, SwitchboardCodexProcessRegistry>();
        services.AddSingleton<ICodexCli>(sp =>
            new CodexCliRunner(
                sp.GetRequiredService<AppSettings>().CodexExecutablePathOverride,
                sp.GetRequiredService<ISwitchboardCodexProcessRegistry>()));
        services.AddSingleton<IProcessManager>(sp =>
            new CodexProcessManager(sp.GetRequiredService<ISwitchboardCodexProcessRegistry>()));
        services.AddSingleton<ICodexCapabilityCache, CodexCapabilityCache>();
        services.AddSingleton<ICodexRuntimeResolver>(sp => new CodexRuntimeResolver(
            sp.GetRequiredService<ICodexCapabilityCache>()));
        services.AddSingleton<ICodexUsageProvider>(sp => new CodexUsageProvider(
            sp.GetRequiredService<IFileSystem>(),
            paths.TempRoot,
            sp.GetRequiredService<AppSettings>().CodexExecutablePathOverride,
            capabilityCache: sp.GetRequiredService<ICodexCapabilityCache>(),
            executablePathAccessor: () => sp.GetRequiredService<AppSettings>().CodexExecutablePathOverride,
            registry: sp.GetRequiredService<ISwitchboardCodexProcessRegistry>()));

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
            }, sp.GetService<IAuditLog>());
        });

        services.AddSingleton<ILegacyMigrationService>(sp => new LegacyMigrationService(
            sp.GetRequiredService<IFileSystem>(),
            paths.LegacyRoot,
            paths.Root));

        return services;
    }

    public static IServiceCollection AddSecurityInfrastructure(this IServiceCollection services, AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(paths);

        services.AddSingleton<IKeyBrokerInstaller>(sp => new KeyBrokerInstaller(paths, sp.GetRequiredService<IFileSystem>()));
        services.AddSingleton<IApiKeySecretStore>(sp => new ApiKeySecretStore(
            sp.GetRequiredService<ISecretProtector>(),
            sp.GetRequiredService<IFileSystem>(),
            paths.ApiKeysDir,
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

        return services;
    }

    public static IServiceCollection AddAccountsInfrastructure(this IServiceCollection services, AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(paths);

        services.AddSingleton<IProfileOperationCoordinator, ProfileOperationCoordinator>();

        services.AddSingleton(sp => new VaultService(
            sp.GetRequiredService<ISecretProtector>(),
            sp.GetRequiredService<IFileSystem>(),
            paths.VaultDir,
            sp.GetRequiredService<IProfileOperationCoordinator>()));
        services.AddSingleton<IVaultService>(sp => sp.GetRequiredService<VaultService>());

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

        services.AddSingleton<IUsageService>(sp => new UsageService(
            sp.GetRequiredService<ICodexUsageProvider>(),
            sp.GetRequiredService<IUsageCache>(),
            sp.GetRequiredService<VaultService>(),
            sp.GetRequiredService<IProfileOperationCoordinator>(),
            sp.GetRequiredService<IClock>(),
            options: null,
            appLifetime: sp.GetRequiredService<IAppLifetime>(),
            fs: sp.GetRequiredService<IFileSystem>(),
            codexPaths: paths.Codex,
            profileStore: null,
            onProfilesPersistNeeded: () => sp.GetRequiredService<ProfileService>().Save()));

        services.AddSingleton(sp => new UsagePollingCoordinator(
            sp.GetRequiredService<IUsageService>(),
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<IAppLifetime>()));

        return services;
    }

    public static IServiceCollection AddProviderInfrastructure(this IServiceCollection services, AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(paths);

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
            "0.2.0"));

        services.AddSingleton<ProviderCatalogService>();
        services.AddSingleton<IProviderCatalogService>(sp => sp.GetRequiredService<ProviderCatalogService>());

        services.AddSingleton<IProviderProbePlanner, ProviderProbePlanner>();
        services.AddSingleton<ISafeProviderHttpTransport, SafeProviderHttpTransport>();
        services.AddSingleton<IProviderProbeResponseMapper, ProviderProbeResponseMapper>();
        services.AddSingleton<IDeclarativeProviderInspector>(sp => new DeclarativeProviderInspector(
            sp.GetRequiredService<IProviderProbePlanner>(),
            sp.GetRequiredService<ISafeProviderHttpTransport>(),
            sp.GetRequiredService<IProviderProbeResponseMapper>()));
        services.AddSingleton<IProviderModelCache, ProviderModelCache>();
        services.AddSingleton<IProviderInspectionService>(sp => new ProviderInspectionService(
            sp.GetRequiredService<IApiProviderStore>(),
            sp.GetRequiredService<IApiKeySecretStore>(),
            sp.GetRequiredService<IProviderCatalogService>(),
            sp.GetRequiredService<IDeclarativeProviderInspector>(),
            sp.GetRequiredService<IProviderModelCache>()));
        services.AddSingleton<IProviderCompatibilityProbeService>(sp => new ProviderCompatibilityProbeService(
            runtimeResolver: sp.GetService<ICodexRuntimeResolver>()));

        return services;
    }

    public static IServiceCollection AddRoutingInfrastructure(this IServiceCollection services, AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(paths);

        services.AddSingleton<ICodexRoutingConfigStore>(sp => new CodexRoutingConfigStore(
            sp.GetRequiredService<IFileSystem>(),
            paths));
        services.AddSingleton<ICodexActiveTargetResolver, CodexActiveTargetResolver>();

        services.AddSingleton<ISwitchPlanBuilder>(sp => new SwitchPlanBuilder(
            sp.GetRequiredService<IApiProviderStore>(),
            sp.GetRequiredService<IApiKeySecretStore>(),
            sp.GetRequiredService<IKeyBrokerInstaller>(),
            sp.GetRequiredService<ICodexRoutingConfigStore>(),
            paths.Codex));

        services.AddSingleton<CodexSwitcher.Core.Providers.Contracts.ICodexModelMetadataResolver, CodexSwitcher.Core.Providers.Services.CodexModelMetadataResolver>();
        services.AddSingleton<CodexSwitcher.Core.Providers.Contracts.IEffectiveModelDescriptorResolver, CodexSwitcher.Core.Providers.Services.EffectiveModelDescriptorResolver>();

        services.AddSingleton<ICodexModelCatalogService>(sp => new CodexModelCatalogService(
            sp.GetRequiredService<IFileSystem>(),
            paths,
            runtimeResolver: sp.GetRequiredService<ICodexRuntimeResolver>(),
            metadataResolver: sp.GetRequiredService<CodexSwitcher.Core.Providers.Contracts.ICodexModelMetadataResolver>(),
            descriptorResolver: sp.GetRequiredService<CodexSwitcher.Core.Providers.Contracts.IEffectiveModelDescriptorResolver>()));

        services.AddSingleton<ISwitchTransactionExecutor>(sp => new SwitchTransactionExecutor(
            sp.GetRequiredService<SwitchService>(),
            sp.GetRequiredService<ICodexRoutingConfigStore>(),
            sp.GetRequiredService<IApiProviderStore>(),
            sp.GetRequiredService<IProcessManager>(),
            sp.GetRequiredService<IFileSystem>(),
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<IAuditLog>(),
            paths.Codex,
            modelCatalogService: sp.GetRequiredService<ICodexModelCatalogService>()));

        services.AddSingleton<ICodexTargetSwitchService>(sp => new CodexTargetSwitchService(
            sp.GetRequiredService<ISwitchPlanBuilder>(),
            sp.GetRequiredService<ISwitchTransactionExecutor>()));

        return services;
    }

    public static IServiceCollection AddDialogServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IDialogHost, DialogHostContext>();
        services.AddSingleton<ICommonDialogService, CommonDialogService>();
        services.AddSingleton<IAccountDialogService, AccountDialogService>();
        services.AddSingleton<ITotpDialogService, TotpDialogService>();
        services.AddSingleton<IProviderDialogService, ProviderDialogService>();
        services.AddSingleton<ISettingsDialogService, SettingsDialogService>();

        return services;
    }

    public static IServiceCollection AddPresentationServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IAppNotificationService, AppNotificationService>();
        services.AddSingleton<IAppBusyService, AppBusyService>();
        services.AddSingleton<IThemeService, WinUiThemeService>();
        services.AddSingleton<WindowChromeService>();
        services.AddSingleton<WindowLifecycleCoordinator>();

        services.AddTransient<ActiveTargetPresentationCoordinator>();
        services.AddTransient<TotpPresentationCoordinator>();
        services.AddTransient<AccountUsageCoordinator>();
        services.AddTransient<AccountsViewModel>();
        services.AddTransient<ApiProvidersViewModel>();
        services.AddTransient<ShellViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<MainWindow>();

        services.AddSingleton<Func<SettingsViewModel>>(sp => () => sp.GetRequiredService<SettingsViewModel>());

        return services;
    }

    public static IServiceCollection AddSwitchboardServices(this IServiceCollection services, AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(paths);

        services
            .AddCoreInfrastructure(paths)
            .AddStorageInfrastructure(paths)
            .AddCodexInfrastructure(paths)
            .AddSecurityInfrastructure(paths)
            .AddAccountsInfrastructure(paths)
            .AddProviderInfrastructure(paths)
            .AddRoutingInfrastructure(paths)
            .AddDialogServices()
            .AddPresentationServices();

        return services;
    }
}

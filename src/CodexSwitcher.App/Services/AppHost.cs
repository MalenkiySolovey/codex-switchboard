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

        services.AddSingleton(sp => new VaultService(
            sp.GetRequiredService<ISecretProtector>(), sp.GetRequiredService<IFileSystem>(), paths.VaultDir));
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
            paths.BackupsDir));

        services.AddSingleton<IUiInteraction, UiInteractionService>();
        services.AddTransient<MainViewModel>();

        Services = services.BuildServiceProvider();
        return Services;
    }
}

using CodexSwitcher.Infra.Common.Paths;
using CodexSwitcher.Infra.Common.Storage;
using CodexSwitcher.Infra.Security.Hardening;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CodexSwitcher.App.Composition;

/// <summary>
/// Composition factory that constructs the .NET Generic Host for Codex Switchboard.
/// Configures a minimal host with DisableDefaults to prevent reading configuration or secrets
/// from environment variables, CLI args, or appsettings.json.
/// Exposes NO static service locator properties.
/// </summary>
public static class SwitchboardHostBuilder
{
    public static HostApplicationBuilder CreateApplicationBuilder(AppPaths? paths = null)
    {
        var appPaths = paths ?? new AppPaths();
        appPaths.EnsureDirectories();
        DirectoryHardening.TryRestrictToCurrentUser(appPaths.Root);
        TempCleanup.SweepLoginTemp(appPaths.TempRoot);

        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true
        });

        // Enforce strict container validation: captive dependencies and scope validation
        builder.Services.Configure<ServiceProviderOptions>(options =>
        {
            options.ValidateOnBuild = true;
            options.ValidateScopes = true;
        });

        builder.Services.AddSwitchboardServices(appPaths);

        return builder;
    }

    public static IHost CreateHost(
        AppPaths? paths = null,
        Action<IServiceCollection>? configureServices = null)
    {
        var builder = CreateApplicationBuilder(paths);
        configureServices?.Invoke(builder.Services);
        return builder.Build();
    }
}

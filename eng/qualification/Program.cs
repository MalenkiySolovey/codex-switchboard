using CodexSwitcher.Core.Common.Enums;
using CodexSwitcher.Core.Common.Storage;
using CodexSwitcher.Core.Providers.Models;
using CodexSwitcher.Core.Routing.Models;
using CodexSwitcher.Core.Settings.Models;
using CodexSwitcher.Infra.Codex.Runtime;
using CodexSwitcher.Infra.Codex.Routing;
using CodexSwitcher.Infra.Codex.Threads;
using CodexSwitcher.Infra.Common.Paths;
using CodexSwitcher.Infra.Common.Storage;
using CodexSwitcher.Infra.Providers.Storage;

return await ContinueChatQualification.RunAsync(args);

internal static class ContinueChatQualification
{
    private static readonly System.Text.Json.JsonSerializerOptions SettingsJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static async Task<int> RunAsync(string[] args)
    {
        if (!TryReadArguments(args, out var targetProfileId, out var sourceThreadId))
        {
            Console.Error.WriteLine("Usage: dotnet run --project eng/qualification/ContinueChatQualification.csproj -- --target-profile <profile-guid> --source-thread <thread-id>");
            return 2;
        }

        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (string.IsNullOrWhiteSpace(codexHome))
        {
            codexHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        }

        var paths = new AppPaths(codexHome: codexHome);
        var fs = new PhysicalFileSystem();
        var settings = ReadSettings(fs, paths.SettingsPath);
        var resolver = new CodexRuntimeResolver();
        var runtime = resolver.ResolveCurrentRuntime(settings.CodexExecutablePathOverride);
        if (string.IsNullOrWhiteSpace(runtime.ExecutablePath) || !File.Exists(runtime.ExecutablePath))
        {
            Console.Error.WriteLine("The current production Codex runtime could not be resolved. No thread was created.");
            return 2;
        }

        var apiStore = new ApiProviderStore(fs, paths.ApiProvidersPath);
        var profile = apiStore.GetById(targetProfileId);
        if (profile is null)
        {
            Console.Error.WriteLine($"Target profile {targetProfileId:D} was not found. No thread was created.");
            return 2;
        }

        var targetModel = profile.SelectedModel;
        if (string.IsNullOrWhiteSpace(targetModel))
        {
            Console.Error.WriteLine("The target profile has no selected model. No thread was created.");
            return 2;
        }

        var inventory = profile.ModelInventory;
        if (inventory is null)
        {
            Console.Error.WriteLine("The target profile has no explicit model inventory. No thread was created.");
            return 2;
        }

        var selectedInventoryItem = inventory.Models.FirstOrDefault(item =>
            string.Equals(item.Slug, targetModel, StringComparison.Ordinal));
        if (selectedInventoryItem is null || !selectedInventoryItem.Enabled)
        {
            Console.Error.WriteLine("The selected target model is not explicitly enabled in the target profile inventory. No thread was created.");
            return 2;
        }

        var routing = new CodexRoutingConfigStore(fs, paths);
        var active = routing.ReadRoutingState(paths.Codex.ConfigTomlPath);
        if (!string.Equals(active.ModelProvider, profile.StableCodexProviderId, StringComparison.Ordinal) ||
            !string.Equals(active.Model, targetModel, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(active.ModelCatalogJson) ||
            !File.Exists(active.ModelCatalogJson))
        {
            Console.Error.WriteLine("Active config.toml does not exactly match the target profile provider, selected model, and existing profile catalog. Activate the profile first. No thread was created.");
            return 2;
        }

        var enabledModels = inventory.GetEnabledModels().Select(item => item.Slug).ToArray();
        if (enabledModels.Length == 0 || !enabledModels.Contains(targetModel, StringComparer.Ordinal))
        {
            Console.Error.WriteLine("The target profile has no coherent enabled model inventory. No thread was created.");
            return 2;
        }

        var registry = new SwitchboardCodexProcessRegistry();
        var processes = new CodexProcessManager(registry);
        var desktops = processes.FindRunningCodexProcesses()
            .Where(process => process.Kind == CodexProcessKind.DesktopApp)
            .ToList();

        Console.WriteLine($"RUNTIME_PATH={runtime.ExecutablePath}");
        Console.WriteLine($"RUNTIME_VERSION={runtime.Version}");
        Console.WriteLine($"SOURCE_THREAD_ID={sourceThreadId}");
        Console.WriteLine($"TARGET_PROFILE_ID={profile.Id:D}");
        Console.WriteLine($"TARGET_PROVIDER={profile.StableCodexProviderId}");
        Console.WriteLine($"TARGET_MODEL={targetModel}");
        Console.WriteLine($"TARGET_CATALOG_PATH={active.ModelCatalogJson}");
        Console.WriteLine("This will stop only detected Codex Desktop processes, create one persistent fork through the production app-server protocol, restart Desktop through its registered AppsFolder entry, and dispatch the codex:// thread link.");
        Console.Write($"Type CREATE {profile.Id:D} to continue: ");
        if (!string.Equals(Console.ReadLine(), $"CREATE {profile.Id:D}", StringComparison.Ordinal))
        {
            Console.WriteLine("Canceled before changing Codex state.");
            return 0;
        }

        var desktopWasStopped = false;
        var desktopRestarted = false;
        var forkId = string.Empty;

        try
        {
            if (processes.AnyCodexCliRunning())
            {
                throw new InvalidOperationException("A Codex CLI process is running. Close it explicitly, then rerun qualification; this harness will not stop CLI processes.");
            }

            if (desktops.Count > 0)
            {
                await processes.CloseGracefullyThenKillAsync(desktops, TimeSpan.FromSeconds(10));
                desktopWasStopped = true;
                var remainingDesktop = processes.FindRunningCodexProcesses()
                    .Any(process => process.Kind == CodexProcessKind.DesktopApp);
                if (remainingDesktop)
                {
                    throw new InvalidOperationException("Codex Desktop did not stop cleanly. No fork was attempted.");
                }
            }

            var verifier = new CodexRuntimeModelCatalogVerifier(
                resolver,
                settings,
                paths,
                registry,
                routing);
            var catalogVerification = await verifier.VerifyAsync(
                profile.StableCodexProviderId,
                targetModel,
                enabledModels);
            if (!catalogVerification.Succeeded)
            {
                throw new InvalidOperationException($"Production model/list verification failed: {catalogVerification.FailureReason}");
            }

            Console.WriteLine($"MODEL_LIST_VERIFIED={string.Join(",", catalogVerification.ObservedModels)}");

            Task<ICodexAppServerClient> CreateClientAsync() => Task.FromResult<ICodexAppServerClient>(
                new CodexAppServerClient(runtime.ExecutablePath, paths.Codex.CodexHome, registry));

            var handoff = new CodexThreadHandoffService(CreateClientAsync);
            var fork = await handoff.ForkThreadAsync(
                sourceThreadId,
                profile.StableCodexProviderId,
                targetModel,
                targetProfileId: profile.Id,
                targetCatalogPath: active.ModelCatalogJson);
            forkId = fork.ForkedThreadId;

            if (!string.Equals(fork.ResponseModelProvider, profile.StableCodexProviderId, StringComparison.Ordinal) ||
                !string.Equals(fork.ResponseModel, targetModel, StringComparison.Ordinal) ||
                (!string.IsNullOrWhiteSpace(fork.ReadbackModelProvider) && !string.Equals(fork.ReadbackModelProvider, profile.StableCodexProviderId, StringComparison.Ordinal)) ||
                (!string.IsNullOrWhiteSpace(fork.ReadbackModel) && !string.Equals(fork.ReadbackModel, targetModel, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("Fork response or thread read-back did not preserve the exact target provider/model.");
            }

            Console.WriteLine("FORK_PROTOCOL_VERIFIED=YES");
            Console.WriteLine($"FORK_RESPONSE_THREAD_ID={fork.ForkedThreadId}");
            Console.WriteLine($"FORK_RESPONSE_PROVIDER={fork.ResponseModelProvider}");
            Console.WriteLine($"FORK_RESPONSE_MODEL={fork.ResponseModel}");
            Console.WriteLine($"READBACK_PROVIDER={fork.ReadbackModelProvider ?? "unavailable"}");
            Console.WriteLine($"READBACK_MODEL={fork.ReadbackModel ?? "unavailable"}");
            Console.WriteLine($"THREAD_PERSISTENT={(fork.ThreadReadVerified || fork.ThreadListVerified ? "YES" : "NO")}");
            Console.WriteLine($"THREAD_PATH_EXISTS={(fork.ReturnedPathExists ? "YES" : "NO_PATH_REPORTED")}");

            if (registry.GetOwnedProcessIds().Count != 0)
            {
                throw new InvalidOperationException("A harness-owned app-server remained running after the verified fork.");
            }

            if (processes.FindRunningCodexProcesses().Any(process => process.Kind == CodexProcessKind.DesktopApp))
            {
                throw new InvalidOperationException("A Codex Desktop process remained open before relaunch; deep-link qualification was not attempted.");
            }

            if (!processes.TryLaunchDesktop())
            {
                throw new InvalidOperationException("Codex Desktop could not be launched through its registered AppsFolder entry.");
            }

            if (!await WaitForDesktopReadyAsync(processes, TimeSpan.FromSeconds(30)))
            {
                throw new InvalidOperationException("Codex Desktop did not become ready within 30 seconds.");
            }
            desktopRestarted = true;

            var deepLinkAccepted = processes.TryOpenThreadDeepLink(fork.ForkedThreadId);
            Console.WriteLine($"DESKTOP_RESTARTED_AFTER_FORK={(desktopRestarted ? "YES" : "NO")}");
            Console.WriteLine($"CODEX_DEEP_LINK_DISPATCHED={(deepLinkAccepted ? "YES" : "NO")}");
            Console.WriteLine("DESKTOP_THREAD_VISIBLE=USER_OBSERVATION_REQUIRED");
            Console.WriteLine("Inspect the exact continuation in Desktop now. Confirm that the thread opens, its history renders, it can resume, and it appears in sidebar/search before recording Desktop visibility as PASS.");
            return deepLinkAccepted ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"CONTINUE_CHAT_QUALIFICATION_FAILED={ex.Message}");
            if (!string.IsNullOrWhiteSpace(forkId))
            {
                Console.Error.WriteLine($"PERSISTENT_THREAD_CREATED={forkId}; verify it with the app-server and consider cleanup through Codex Desktop if needed.");
            }
            return 1;
        }
        finally
        {
            await registry.TerminateAllOwnedProcessesAsync(TimeSpan.FromSeconds(3));
            if (desktopWasStopped && !desktopRestarted &&
                !processes.FindRunningCodexProcesses().Any(process => process.Kind == CodexProcessKind.DesktopApp))
            {
                _ = processes.TryLaunchDesktop();
            }
        }
    }

    private static AppSettings ReadSettings(PhysicalFileSystem fs, string path)
    {
        if (!fs.FileExists(path))
        {
            return new AppSettings();
        }

        return System.Text.Json.JsonSerializer.Deserialize<AppSettings>(fs.ReadAllText(path), SettingsJsonOptions) ?? new AppSettings();
    }

    private static async Task<bool> WaitForDesktopReadyAsync(CodexProcessManager processes, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (processes.FindRunningCodexProcesses().Any(process => process.Kind == CodexProcessKind.DesktopApp))
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }

        return false;
    }

    private static bool TryReadArguments(string[] args, out Guid targetProfileId, out string sourceThreadId)
    {
        targetProfileId = Guid.Empty;
        sourceThreadId = string.Empty;
        for (var index = 0; index < args.Length; index++)
        {
            if (string.Equals(args[index], "--target-profile", StringComparison.Ordinal) && index + 1 < args.Length)
            {
                if (!Guid.TryParse(args[++index], out targetProfileId))
                {
                    return false;
                }
            }
            else if (string.Equals(args[index], "--source-thread", StringComparison.Ordinal) && index + 1 < args.Length)
            {
                sourceThreadId = args[++index];
            }
            else
            {
                return false;
            }
        }

        return targetProfileId != Guid.Empty &&
            !string.IsNullOrWhiteSpace(sourceThreadId) &&
            sourceThreadId.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
    }
}

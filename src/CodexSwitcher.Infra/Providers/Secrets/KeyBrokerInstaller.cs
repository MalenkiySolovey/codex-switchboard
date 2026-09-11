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
using CodexSwitcher.Core.Providers.Contracts;
using System.Security.Cryptography;

namespace CodexSwitcher.Infra.Providers.Secrets;

/// <summary>
/// Installs and verifies the integrity of CodexSwitchboard.KeyBroker.exe in the dedicated
/// %LOCALAPPDATA%\CodexSwitchboard\broker\ directory.
/// </summary>
public sealed class KeyBrokerInstaller : IKeyBrokerInstaller
{
    private readonly AppPaths _paths;
    private readonly IFileSystem _fs;
    private readonly string? _defaultSourcePath;

    public KeyBrokerInstaller(AppPaths paths, IFileSystem fs, string? defaultSourcePath = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _fs = fs ?? throw new ArgumentNullException(nameof(fs));
        _defaultSourcePath = defaultSourcePath;
    }

    public string BrokerExecutablePath => _paths.BrokerExecutablePath;

    public string ResolveSourcePayloadPath(string? explicitSourcePath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitSourcePath) && _fs.FileExists(explicitSourcePath))
        {
            return explicitSourcePath;
        }

        if (!string.IsNullOrWhiteSpace(_defaultSourcePath) && _fs.FileExists(_defaultSourcePath))
        {
            return _defaultSourcePath;
        }

        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "CodexSwitchboard.KeyBroker.exe"),
            Path.Combine(baseDir, "broker", "CodexSwitchboard.KeyBroker.exe"),
            Path.Combine(baseDir, "..", "..", "..", "..", "src", "CodexSwitchboard.KeyBroker", "bin", "Debug", "net10.0-windows", "CodexSwitchboard.KeyBroker.exe"),
            Path.Combine(baseDir, "..", "..", "..", "..", "src", "CodexSwitchboard.KeyBroker", "bin", "Release", "net10.0-windows", "CodexSwitchboard.KeyBroker.exe"),
            Path.Combine(baseDir, "..", "..", "..", "..", "..", "src", "CodexSwitchboard.KeyBroker", "bin", "Debug", "net10.0-windows", "CodexSwitchboard.KeyBroker.exe"),
            Path.Combine(baseDir, "..", "..", "..", "..", "..", "src", "CodexSwitchboard.KeyBroker", "bin", "Release", "net10.0-windows", "CodexSwitchboard.KeyBroker.exe"),
            Path.Combine(baseDir, "..", "..", "..", "src", "CodexSwitchboard.KeyBroker", "bin", "Debug", "net10.0-windows", "CodexSwitchboard.KeyBroker.exe"),
        };

        foreach (var candidate in candidates)
        {
            try
            {
                var full = Path.GetFullPath(candidate);
                if (_fs.FileExists(full))
                {
                    return full;
                }
            }
            catch
            {
                // ignore parsing exceptions for invalid paths
            }
        }

        throw new FileNotFoundException(
            "Trusted CodexSwitchboard.KeyBroker.exe payload could not be located in application distribution. " +
            "Ensure the broker binary is packaged with Codex Switchboard.");
    }

    public bool IsInstalledAndValid()
    {
        if (!_fs.FileExists(BrokerExecutablePath))
        {
            return false;
        }

        try
        {
            var sourcePath = ResolveSourcePayloadPath();
            var sourceBytes = _fs.ReadAllBytes(sourcePath);
            var destBytes = _fs.ReadAllBytes(BrokerExecutablePath);

            var sourceHash = SHA256.HashData(sourceBytes);
            var destHash = SHA256.HashData(destBytes);

            return CryptographicOperations.FixedTimeEquals(sourceHash, destHash);
        }
        catch
        {
            return false;
        }
    }

    public string EnsureInstalled(string? explicitSourcePath = null)
    {
        var sourcePath = ResolveSourcePayloadPath(explicitSourcePath);
        var sourceBytes = _fs.ReadAllBytes(sourcePath);
        var sourceHash = SHA256.HashData(sourceBytes);

        _paths.EnsureDirectories();
        if (OperatingSystem.IsWindows())
        {
            DirectoryHardening.TryRestrictToCurrentUser(_paths.BrokerDir);
        }

        if (_fs.FileExists(BrokerExecutablePath))
        {
            var currentBytes = _fs.ReadAllBytes(BrokerExecutablePath);
            var currentHash = SHA256.HashData(currentBytes);
            if (CryptographicOperations.FixedTimeEquals(sourceHash, currentHash))
            {
                return BrokerExecutablePath;
            }
        }

        _fs.WriteAllBytesAtomic(BrokerExecutablePath, sourceBytes);
        return BrokerExecutablePath;
    }
}

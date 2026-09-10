using CodexSwitcher.Core.Abstractions;
using CodexSwitcher.Core.Security;
using CodexSwitcher.Infra.Io;
using CodexSwitcher.Infra.Security;

namespace CodexSwitcher.Infra.Codex;

/// <summary>
/// Manages the lifecycle of an isolated temporary CODEX_HOME directory for a single profile.
/// Ensures credentials are isolated, ACL-hardened, tracked for token mutation, and reliably wiped upon completion.
/// Never interacts with or modifies the user's real %USERPROFILE%\.codex directory.
/// </summary>
public sealed class ProfileSandbox : IAsyncDisposable, IDisposable
{
    private readonly IFileSystem _fs;
    private readonly string _workRoot;
    private bool _disposed;

    public string DirectoryPath { get; }
    public string AuthJsonPath => Path.Combine(DirectoryPath, "auth.json");
    public string ConfigTomlPath => Path.Combine(DirectoryPath, "config.toml");
    public string InitialFingerprint { get; }

    private ProfileSandbox(string workRoot, string directoryPath, string initialFingerprint, IFileSystem fs)
    {
        _workRoot = workRoot;
        DirectoryPath = directoryPath;
        InitialFingerprint = initialFingerprint;
        _fs = fs;
    }

    /// <summary>
    /// Creates and initializes an isolated CODEX_HOME sandbox with the provided decrypted auth.json.
    /// </summary>
    public static ProfileSandbox Create(string workRoot, Guid profileId, byte[] authJsonBytes, IFileSystem fs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workRoot);
        ArgumentNullException.ThrowIfNull(authJsonBytes);
        ArgumentNullException.ThrowIfNull(fs);

        fs.CreateDirectory(workRoot);
        DirectoryHardening.TryRestrictToCurrentUser(workRoot);

        var sandboxDir = Path.Combine(workRoot, $"usage-{profileId:N}-{Guid.NewGuid():N}");
        fs.CreateDirectory(sandboxDir);
        DirectoryHardening.TryRestrictToCurrentUser(sandboxDir);

        var authPath = Path.Combine(sandboxDir, "auth.json");
        var configPath = Path.Combine(sandboxDir, "config.toml");

        fs.WriteAllBytesAtomic(authPath, authJsonBytes);
        fs.WriteAllTextAtomic(configPath, "cli_auth_credentials_store = \"file\"\n");

        var initialFingerprint = Fingerprint.Compute(authJsonBytes);
        return new ProfileSandbox(workRoot, sandboxDir, initialFingerprint, fs);
    }

    /// <summary>
    /// Inspects the sandbox auth.json to determine if the CLI process mutated or rotated credentials.
    /// </summary>
    public (bool Mutated, byte[]? CurrentBytes, string? CurrentFingerprint) InspectMutation()
    {
        if (!_fs.FileExists(AuthJsonPath))
            return (false, null, null);

        try
        {
            var bytes = _fs.ReadAllBytes(AuthJsonPath);
            var currentFingerprint = Fingerprint.Compute(bytes);
            var mutated = !string.Equals(InitialFingerprint, currentFingerprint, StringComparison.OrdinalIgnoreCase);
            return (mutated, bytes, currentFingerprint);
        }
        catch (IOException)
        {
            return (false, null, null);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Cleanup();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private void Cleanup()
    {
        if (!Directory.Exists(DirectoryPath))
            return;

        // Safety verification: only delete directories strictly residing under the configured workRoot
        var fullDir = Path.GetFullPath(DirectoryPath);
        var fullRoot = Path.GetFullPath(_workRoot);
        if (!fullDir.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) || fullDir.Equals(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Perform bounded, non-blocking cleanup without stalling application exit
        TempCleanup.TryForceDelete(DirectoryPath);
    }
}

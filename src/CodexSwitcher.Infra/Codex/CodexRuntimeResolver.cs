using System.Diagnostics;
using System.Text.RegularExpressions;
using CodexSwitcher.Core.Abstractions;
using CodexSwitcher.Core.Models;

namespace CodexSwitcher.Infra.Codex;

/// <summary>
/// Resolves the active Codex runtime executable, enumerates candidates deterministically,
/// and detects capabilities via schema generation or RPC probe caching.
/// </summary>
public sealed class CodexRuntimeResolver : ICodexRuntimeResolver
{
    private static readonly Regex VersionPattern = new(@"(\d+\.\d+\.\d+(?:-[a-zA-Z0-9\.]+)?)", RegexOptions.Compiled);

    private readonly ICodexCapabilityCache _capabilityCache;
    private readonly Func<string, (string? Version, bool? IsSupported)>? _probeInspector;

    public CodexRuntimeResolver(
        ICodexCapabilityCache? capabilityCache = null,
        Func<string, (string? Version, bool? IsSupported)>? probeInspector = null)
    {
        _capabilityCache = capabilityCache ?? new CodexSwitcher.Core.Services.CodexCapabilityCache();
        _probeInspector = probeInspector;
    }

    public CodexRuntimeInfo ResolveCurrentRuntime(string? overridePath = null)
    {
        // Precedence 1: Explicit custom override path (if specified and valid)
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            var (ver, isSupp) = InspectCandidate(overridePath);
            var caps = BuildCapabilities(isSupp);
            var fi = new FileInfo(overridePath);
            var identity = CodexRuntimeInfo.ComputeIdentity(overridePath, fi.Length, fi.LastWriteTimeUtc, ver);
            return new CodexRuntimeInfo(overridePath, ver ?? "unknown", identity, caps);
        }

        // Precedence 2: Versioned official binaries under %LOCALAPPDATA%\OpenAI\Codex\bin\<hash>\codex.exe
        var hashedCandidates = EnumerateHashedCandidates();
        if (hashedCandidates.Count > 0)
        {
            var best = hashedCandidates[0];
            var (_, isSupp) = InspectCandidate(best.Path);
            var caps = BuildCapabilities(isSupp);
            var identity = CodexRuntimeInfo.ComputeIdentity(best.Path, best.FileSize, best.LastWriteTimeUtc, best.Version);
            return new CodexRuntimeInfo(best.Path, best.Version ?? "unknown", identity, caps);
        }

        // Precedence 3: Well-known official installer path %LOCALAPPDATA%\OpenAI\Codex\bin\codex.exe
        var wellKnown = GetWellKnownInstallerPath();
        if (!string.IsNullOrWhiteSpace(wellKnown) && File.Exists(wellKnown))
        {
            var (ver, isSupp) = InspectCandidate(wellKnown);
            var caps = BuildCapabilities(isSupp);
            var fi = new FileInfo(wellKnown);
            var identity = CodexRuntimeInfo.ComputeIdentity(wellKnown, fi.Length, fi.LastWriteTimeUtc, ver);
            return new CodexRuntimeInfo(wellKnown, ver ?? "unknown", identity, caps);
        }

        // Precedence 4: PATH entries
        var pathCandidate = FindExecutableOnPath();
        if (!string.IsNullOrWhiteSpace(pathCandidate))
        {
            var (ver, isSupp) = InspectCandidate(pathCandidate);
            var caps = BuildCapabilities(isSupp);
            var fi = new FileInfo(pathCandidate);
            var identity = CodexRuntimeInfo.ComputeIdentity(pathCandidate, fi.Length, fi.LastWriteTimeUtc, ver);
            return new CodexRuntimeInfo(pathCandidate, ver ?? "unknown", identity, caps);
        }

        // Fallback when nothing found
        return new CodexRuntimeInfo(
            string.Empty,
            "not_found",
            null,
            new CodexRuntimeCapabilities(CapabilityStatus.Unsupported, CapabilityStatus.Unsupported, CapabilityStatus.Unsupported, CapabilityStatus.Unsupported));
    }

    public IReadOnlyList<CodexRuntimeCandidate> EnumerateCandidates()
    {
        var candidates = new List<CodexRuntimeCandidate>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Hashed subdirectories
        foreach (var candidate in EnumerateHashedCandidates())
        {
            if (seenPaths.Add(candidate.Path))
                candidates.Add(candidate);
        }

        // 2. Well-known installer path
        var wellKnown = GetWellKnownInstallerPath();
        if (!string.IsNullOrWhiteSpace(wellKnown) && File.Exists(wellKnown) && seenPaths.Add(wellKnown))
        {
            var (ver, isSupp) = InspectCandidate(wellKnown);
            var fi = new FileInfo(wellKnown);
            candidates.Add(new CodexRuntimeCandidate(
                wellKnown,
                ver,
                fi.Length,
                fi.LastWriteTimeUtc,
                true,
                RuntimeCandidateSource.OfficialWellKnown));
        }

        // 3. PATH
        var pathExecutables = FindAllExecutablesOnPath();
        foreach (var p in pathExecutables)
        {
            if (seenPaths.Add(p))
            {
                var (ver, isSupp) = InspectCandidate(p);
                var fi = new FileInfo(p);
                candidates.Add(new CodexRuntimeCandidate(
                    p,
                    ver,
                    fi.Length,
                    fi.LastWriteTimeUtc,
                    true,
                    RuntimeCandidateSource.Path));
            }
        }

        return candidates;
    }

    public bool ValidateExecutable(string path, out string? version, out string? error)
    {
        version = null;
        error = null;

        if (string.IsNullOrWhiteSpace(path))
        {
            error = "Path is empty or null.";
            return false;
        }

        if (!File.Exists(path))
        {
            error = $"File not found: {path}";
            return false;
        }

        try
        {
            var (ver, _) = InspectCandidate(path);
            if (string.IsNullOrWhiteSpace(ver) || ver == "unknown")
            {
                error = "Could not extract Codex version from executable.";
                return false;
            }

            version = ver;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private List<CodexRuntimeCandidate> EnumerateHashedCandidates()
    {
        var list = new List<CodexRuntimeCandidate>();
        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var binDir = Path.Combine(localAppData, "OpenAI", "Codex", "bin");
            if (!Directory.Exists(binDir))
                return list;

            var subdirs = Directory.GetDirectories(binDir);
            foreach (var sub in subdirs)
            {
                var exe = Path.Combine(sub, "codex.exe");
                if (File.Exists(exe))
                {
                    var (ver, isSupp) = InspectCandidate(exe);
                    var fi = new FileInfo(exe);
                    list.Add(new CodexRuntimeCandidate(
                        exe,
                        ver,
                        fi.Length,
                        fi.LastWriteTimeUtc,
                        true,
                        RuntimeCandidateSource.OfficialHashDirectory));
                }
            }

            // Sort descending: highest version first, then newest write time
            list.Sort((a, b) =>
            {
                var va = TryParseSemVer(a.Version);
                var vb = TryParseSemVer(b.Version);
                if (va != null && vb != null)
                {
                    int cmp = vb.CompareTo(va);
                    if (cmp != 0) return cmp;
                }
                else if (va != null) return -1;
                else if (vb != null) return 1;

                return b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc);
            });
        }
        catch
        {
            // Never crash if directories don't exist or access is denied
        }

        return list;
    }

    private static string? GetWellKnownInstallerPath()
    {
        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "OpenAI", "Codex", "bin", "codex.exe");
        }
        catch
        {
            return null;
        }
    }

    private static string? FindExecutableOnPath()
    {
        var all = FindAllExecutablesOnPath();
        return all.Count > 0 ? all[0] : null;
    }

    private static List<string> FindAllExecutablesOnPath()
    {
        var results = new List<string>();
        try
        {
            var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            var dirs = pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            string[] names = ["codex.exe", "codex.cmd", "codex.bat", "codex.ps1", "codex"];

            foreach (var dir in dirs)
            {
                foreach (var name in names)
                {
                    try
                    {
                        var full = Path.Combine(dir, name);
                        if (File.Exists(full))
                        {
                            results.Add(full);
                        }
                    }
                    catch { }
                }
            }
        }
        catch { }

        return results;
    }

    private (string? Version, bool IsSupported) InspectCandidate(string exePath)
    {
        if (_probeInspector != null)
        {
            var (customVer, customSupp) = _probeInspector(exePath);
            return (customVer, customSupp ?? false);
        }

        var identity = GetExecutableIdentity(exePath);
        var cached = _capabilityCache.GetCapabilities(identity);

        string? version = null;
        bool isSupported = false;

        if (cached != null)
        {
            isSupported = cached.AccountUsageRead == CapabilityStatus.Supported;
        }

        // Try extracting version
        version = ProbeVersion(exePath);

        if (cached == null)
        {
            isSupported = ProbeSchemaSupport(exePath, version);
            _capabilityCache.SetCapabilities(identity, BuildCapabilities(isSupported));
        }

        return (version, isSupported);
    }

    private static string ProbeVersion(string exePath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = "--version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null) return "unknown";

            if (!process.WaitForExit(3000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return "unknown";
            }

            var output = process.StandardOutput.ReadToEnd();
            var match = VersionPattern.Match(output);
            if (match.Success)
                return match.Groups[1].Value;
        }
        catch { }

        // Fallback to FileVersionInfo if available
        try
        {
            var fvi = FileVersionInfo.GetVersionInfo(exePath);
            if (!string.IsNullOrWhiteSpace(fvi.ProductVersion))
            {
                var match = VersionPattern.Match(fvi.ProductVersion);
                if (match.Success)
                    return match.Groups[1].Value;
                return fvi.ProductVersion;
            }
        }
        catch { }

        return "unknown";
    }

    private static bool ProbeSchemaSupport(string exePath, string? version)
    {
        // Strategy 2: Schema generation
        string tempDir = Path.Combine(Path.GetTempPath(), "codex_schema_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDir);
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = $"app-server generate-json-schema --out \"{tempDir}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process != null && process.WaitForExit(4000) && process.ExitCode == 0)
            {
                var files = Directory.GetFiles(tempDir, "*.json", SearchOption.AllDirectories);
                if (files.Length > 0)
                {
                    foreach (var f in files)
                    {
                        var text = File.ReadAllText(f);
                        if (text.Contains("account/usage/read", StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                    // Schema generated cleanly and does not mention account/usage/read
                    return false;
                }
            }
        }
        catch { }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
            catch { }
        }

        // Fallback: heuristic version check if schema generation unavailable
        if (!string.IsNullOrWhiteSpace(version))
        {
            var semver = TryParseSemVer(version);
            if (semver != null)
            {
                return semver >= new Version(0, 140, 0);
            }
        }

        return false;
    }

    private static CodexRuntimeCapabilities BuildCapabilities(bool isActivitySupported) =>
        isActivitySupported
            ? CodexRuntimeCapabilities.ModernFull
            : CodexRuntimeCapabilities.LegacyUnsupportedUsage;

    public static string GetExecutableIdentity(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var fi = new FileInfo(path);
                return $"{path}|{fi.LastWriteTimeUtc.Ticks}|{fi.Length}";
            }
        }
        catch { }

        return $"{path}|unknown";
    }

    private static Version? TryParseSemVer(string? versionStr)
    {
        if (string.IsNullOrWhiteSpace(versionStr)) return null;
        var clean = versionStr.TrimStart('v');
        var dash = clean.IndexOf('-');
        if (dash > 0)
            clean = clean[..dash];

        return Version.TryParse(clean, out var v) ? v : null;
    }
}

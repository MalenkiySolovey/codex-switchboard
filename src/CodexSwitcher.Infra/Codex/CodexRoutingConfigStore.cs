using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CodexSwitcher.Core.Abstractions;

namespace CodexSwitcher.Infra.Codex;

/// <summary>
/// Production implementation of <see cref="ICodexRoutingConfigStore"/> providing narrow,
/// lossless mutations of config.toml for Switchboard provider routing.
/// Never mutates external/unmanaged provider tables, MCP servers, or user comments.
/// </summary>
public sealed partial class CodexRoutingConfigStore : ICodexRoutingConfigStore
{
    private readonly IFileSystem _fs;
    private readonly AppPaths _paths;

    public CodexRoutingConfigStore(IFileSystem fs, AppPaths paths)
    {
        _fs = fs ?? throw new ArgumentNullException(nameof(fs));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    [GeneratedRegex(@"^\s*model_provider\s*=\s*""(?<val>[^""]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex ModelProviderRegex();

    [GeneratedRegex(@"^\s*model\s*=\s*""(?<val>[^""]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex ModelRegex();

    [GeneratedRegex(@"^\s*\[")]
    private static partial Regex TableHeaderRegex();

    [GeneratedRegex(@"^\s*\[model_providers\.(?<id>[a-zA-Z0-9_-]+)\]\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ProviderTableHeaderRegex();

    public string ComputeFingerprint(string configTomlPath)
    {
        if (!_fs.FileExists(configTomlPath))
            return string.Empty;

        var bytes = _fs.ReadAllBytes(configTomlPath);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    public CodexRoutingState ReadRoutingState(string configTomlPath)
    {
        if (!_fs.FileExists(configTomlPath))
            return new CodexRoutingState(null, null, new Dictionary<string, CodexProviderBlock>(), string.Empty);

        var raw = _fs.ReadAllText(configTomlPath);
        var fingerprint = ComputeFingerprint(configTomlPath);
        var lines = raw.Split('\n');

        string? modelProvider = null;
        string? model = null;

        var firstTable = FirstTableIndex(lines);
        for (var i = 0; i < firstTable; i++)
        {
            var mProv = ModelProviderRegex().Match(lines[i]);
            if (mProv.Success) modelProvider = mProv.Groups["val"].Value;

            var mMod = ModelRegex().Match(lines[i]);
            if (mMod.Success) model = mMod.Groups["val"].Value;
        }

        var switchboardProviders = ParseSwitchboardProviders(lines);
        return new CodexRoutingState(modelProvider, model, switchboardProviders, fingerprint);
    }

    public string ApplySwitchboardRouting(
        string configTomlPath,
        CodexProviderBlock providerBlock,
        string model,
        string? expectedFingerprint = null)
    {
        ArgumentNullException.ThrowIfNull(providerBlock);
        if (!providerBlock.ProviderId.StartsWith("switchboard_", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Provider ID '{providerBlock.ProviderId}' is not a Switchboard-owned provider.", nameof(providerBlock));
        }

        EnsureCasAndBackup(configTomlPath, expectedFingerprint);

        var raw = _fs.FileExists(configTomlPath) ? _fs.ReadAllText(configTomlPath) : string.Empty;
        var newline = raw.Contains("\r\n") ? "\r\n" : "\n";
        var lines = raw.Length == 0 ? new List<string>() : new List<string>(raw.Split('\n').Select(l => l.TrimEnd('\r')));

        // 1. Update root model_provider & model
        SetRootKey(lines, "model_provider", providerBlock.ProviderId);
        if (!string.IsNullOrWhiteSpace(model))
        {
            SetRootKey(lines, "model", model);
        }

        // 2. Format provider block
        var blockLines = FormatProviderBlockLines(providerBlock);

        // 3. Find and replace or append provider block
        var (startIdx, endIdx) = FindProviderTableRange(lines, providerBlock.ProviderId);
        if (startIdx >= 0)
        {
            lines.RemoveRange(startIdx, endIdx - startIdx);
            lines.InsertRange(startIdx, blockLines);
        }
        else
        {
            if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1]))
            {
                lines.Add(string.Empty);
            }
            lines.AddRange(blockLines);
        }

        var updated = string.Join(newline, lines);
        if (raw.EndsWith(newline) && !updated.EndsWith(newline))
        {
            updated += newline;
        }

        _fs.WriteAllTextAtomic(configTomlPath, updated);
        return ComputeFingerprint(configTomlPath);
    }

    public string UpdateProviderRoute(
        string configTomlPath,
        string providerId,
        string newBaseUrl,
        string? expectedFingerprint = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(newBaseUrl);

        if (!providerId.StartsWith("switchboard_", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Provider ID '{providerId}' is not a Switchboard-owned provider.", nameof(providerId));
        }

        EnsureCasAndBackup(configTomlPath, expectedFingerprint);

        var raw = _fs.FileExists(configTomlPath) ? _fs.ReadAllText(configTomlPath) : string.Empty;
        var newline = raw.Contains("\r\n") ? "\r\n" : "\n";
        var lines = new List<string>(raw.Split('\n').Select(l => l.TrimEnd('\r')));

        var (startIdx, endIdx) = FindProviderTableRange(lines, providerId);
        if (startIdx < 0)
        {
            throw new InvalidOperationException($"Switchboard provider '{providerId}' not found in configuration.");
        }

        var updatedBaseUrl = false;
        var baseUrlRegex = new Regex(@"^\s*base_url\s*=.*$", RegexOptions.IgnoreCase);

        for (var i = startIdx; i < endIdx; i++)
        {
            // Do not enter subtables like .auth
            if (i > startIdx && TableHeaderRegex().IsMatch(lines[i]))
                break;

            if (baseUrlRegex.IsMatch(lines[i]))
            {
                lines[i] = $"base_url = \"{TomlEscape(newBaseUrl)}\"";
                updatedBaseUrl = true;
                break;
            }
        }

        if (!updatedBaseUrl)
        {
            // Insert after table header
            lines.Insert(startIdx + 1, $"base_url = \"{TomlEscape(newBaseUrl)}\"");
        }

        var updated = string.Join(newline, lines);
        if (raw.EndsWith(newline) && !updated.EndsWith(newline))
        {
            updated += newline;
        }

        _fs.WriteAllTextAtomic(configTomlPath, updated);
        return ComputeFingerprint(configTomlPath);
    }

    public string ReturnToOpenAi(
        string configTomlPath,
        string? model = null,
        string? expectedFingerprint = null)
    {
        EnsureCasAndBackup(configTomlPath, expectedFingerprint);

        var raw = _fs.FileExists(configTomlPath) ? _fs.ReadAllText(configTomlPath) : string.Empty;
        var newline = raw.Contains("\r\n") ? "\r\n" : "\n";
        var lines = raw.Length == 0 ? new List<string>() : new List<string>(raw.Split('\n').Select(l => l.TrimEnd('\r')));

        SetRootKey(lines, "model_provider", "openai");
        if (!string.IsNullOrWhiteSpace(model))
        {
            SetRootKey(lines, "model", model);
        }

        var updated = string.Join(newline, lines);
        if (raw.EndsWith(newline) && !updated.EndsWith(newline))
        {
            updated += newline;
        }

        _fs.WriteAllTextAtomic(configTomlPath, updated);
        return ComputeFingerprint(configTomlPath);
    }

    public void RestoreExactBytes(string configTomlPath, byte[] exactBytes)
    {
        ArgumentNullException.ThrowIfNull(exactBytes);
        _fs.WriteAllBytesAtomic(configTomlPath, exactBytes);
    }

    private void EnsureCasAndBackup(string configTomlPath, string? expectedFingerprint)
    {
        if (_fs.FileExists(configTomlPath))
        {
            var currentBytes = _fs.ReadAllBytes(configTomlPath);
            var currentFingerprint = Convert.ToHexString(SHA256.HashData(currentBytes)).ToLowerInvariant();

            if (!string.IsNullOrWhiteSpace(expectedFingerprint) &&
                !string.Equals(expectedFingerprint, currentFingerprint, StringComparison.OrdinalIgnoreCase))
            {
                throw new ConcurrentModificationException(
                    $"config.toml was modified concurrently. Expected fingerprint: {expectedFingerprint}, actual: {currentFingerprint}");
            }

            try
            {
                _paths.EnsureDirectories();
                var backupFile = Path.Combine(_paths.BackupsDir, $"config.toml.{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.bak");
                _fs.WriteAllBytesAtomic(backupFile, currentBytes);
            }
            catch
            {
                // Backup creation failure should not silently corrupt or crash, but best effort
            }
        }
    }

    private static int FirstTableIndex(IReadOnlyList<string> lines)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (TableHeaderRegex().IsMatch(lines[i]))
                return i;
        }
        return lines.Count;
    }

    private static void SetRootKey(List<string> lines, string key, string value)
    {
        var firstTable = FirstTableIndex(lines);
        var pattern = new Regex($@"^\s*{Regex.Escape(key)}\s*=.*$", RegexOptions.IgnoreCase);

        for (var i = 0; i < firstTable; i++)
        {
            if (pattern.IsMatch(lines[i]))
            {
                lines[i] = $"{key} = \"{TomlEscape(value)}\"";
                return;
            }
        }

        lines.Insert(firstTable, $"{key} = \"{TomlEscape(value)}\"");
    }

    private static (int StartIdx, int EndIdx) FindProviderTableRange(List<string> lines, string providerId)
    {
        var headerRegex = new Regex($@"^\s*\[model_providers\.[""']?{Regex.Escape(providerId)}[""']?\]\s*$", RegexOptions.IgnoreCase);
        var subtablePrefix = $"model_providers.{providerId}.";

        var startIdx = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            if (headerRegex.IsMatch(lines[i]))
            {
                startIdx = i;
                break;
            }
        }

        if (startIdx < 0) return (-1, -1);

        var endIdx = lines.Count;
        for (var j = startIdx + 1; j < lines.Count; j++)
        {
            if (TableHeaderRegex().IsMatch(lines[j]))
            {
                var trimmed = lines[j].Trim().TrimStart('[').TrimEnd(']').Trim();
                if (!trimmed.StartsWith(subtablePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    endIdx = j;
                    break;
                }
            }
        }

        return (startIdx, endIdx);
    }

    private static List<string> FormatProviderBlockLines(CodexProviderBlock b)
    {
        var escapedName = TomlEscape(b.Name);
        var escapedBaseUrl = TomlEscape(b.BaseUrl);
        var escapedWireApi = TomlEscape(b.WireApi);
        var escapedCmd = TomlEscape(b.BrokerCommand);

        var formattedArgs = string.Join(", ", b.BrokerArgs.Select(a => $"\"{TomlEscape(a)}\""));

        return new List<string>
        {
            $"[model_providers.{b.ProviderId}]",
            $"name = \"{escapedName}\"",
            $"base_url = \"{escapedBaseUrl}\"",
            $"wire_api = \"{escapedWireApi}\"",
            string.Empty,
            $"[model_providers.{b.ProviderId}.auth]",
            $"command = \"{escapedCmd}\"",
            $"args = [{formattedArgs}]",
            $"timeout_ms = {b.TimeoutMs}"
        };
    }

    public static string TomlEscape(string value)
    {
        if (value is null) return string.Empty;
        var sb = new StringBuilder(value.Length + 16);
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': sb.Append(@"\\"); break;
                case '"': sb.Append(@"\"""); break;
                case '\b': sb.Append(@"\b"); break;
                case '\f': sb.Append(@"\f"); break;
                case '\n': sb.Append(@"\n"); break;
                case '\r': sb.Append(@"\r"); break;
                case '\t': sb.Append(@"\t"); break;
                default:
                    if (char.IsControl(c))
                    {
                        sb.Append($@"\u{(int)c:X4}");
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
        return sb.ToString();
    }

    private static Dictionary<string, CodexProviderBlock> ParseSwitchboardProviders(IReadOnlyList<string> lines)
    {
        var map = new Dictionary<string, CodexProviderBlock>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < lines.Count; i++)
        {
            var match = ProviderTableHeaderRegex().Match(lines[i]);
            if (match.Success)
            {
                var id = match.Groups["id"].Value;
                if (!id.StartsWith("switchboard_", StringComparison.OrdinalIgnoreCase))
                    continue;

                string name = id;
                string baseUrl = string.Empty;
                string wireApi = "responses";
                string command = string.Empty;
                var args = new List<string>();
                int timeoutMs = 5000;

                var inAuth = false;
                for (var j = i + 1; j < lines.Count; j++)
                {
                    if (TableHeaderRegex().IsMatch(lines[j]))
                    {
                        var trimmedHeader = lines[j].Trim().TrimStart('[').TrimEnd(']').Trim();
                        if (trimmedHeader.Equals($"model_providers.{id}.auth", StringComparison.OrdinalIgnoreCase))
                        {
                            inAuth = true;
                            continue;
                        }
                        else
                        {
                            break;
                        }
                    }

                    var line = lines[j].Trim();
                    if (inAuth)
                    {
                        if (line.StartsWith("command", StringComparison.OrdinalIgnoreCase))
                        {
                            var eq = line.IndexOf('=');
                            if (eq >= 0) command = line[(eq + 1)..].Trim().Trim('"');
                        }
                        else if (line.StartsWith("timeout_ms", StringComparison.OrdinalIgnoreCase))
                        {
                            var eq = line.IndexOf('=');
                            if (eq >= 0 && int.TryParse(line[(eq + 1)..].Trim(), out var parsed)) timeoutMs = parsed;
                        }
                    }
                    else
                    {
                        if (line.StartsWith("name", StringComparison.OrdinalIgnoreCase))
                        {
                            var eq = line.IndexOf('=');
                            if (eq >= 0) name = line[(eq + 1)..].Trim().Trim('"');
                        }
                        else if (line.StartsWith("base_url", StringComparison.OrdinalIgnoreCase))
                        {
                            var eq = line.IndexOf('=');
                            if (eq >= 0) baseUrl = line[(eq + 1)..].Trim().Trim('"');
                        }
                        else if (line.StartsWith("wire_api", StringComparison.OrdinalIgnoreCase))
                        {
                            var eq = line.IndexOf('=');
                            if (eq >= 0) wireApi = line[(eq + 1)..].Trim().Trim('"');
                        }
                    }
                }

                map[id] = new CodexProviderBlock(id, name, baseUrl, wireApi, command, args, timeoutMs);
            }
        }

        return map;
    }
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexSwitcher.Infra;

namespace CodexSwitchboard.KeyBroker;

/// <summary>
/// Production Key Broker console executable for Codex CLI auth.command bearer delivery.
/// Strictly minimal:
/// - Never contacts any network endpoint
/// - Never touches config.toml or auth.json
/// - Protocol stdout: ONLY the raw API key + newline on success
/// - Stderr: sanitized failure messages only
/// - Zeroes mutable secret buffers immediately
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        Guid keyId;
        try
        {
            if (!TryParseKeyId(args, out keyId))
            {
                Console.Error.WriteLine("Error: Invalid arguments. Expected: --key-id <GUID>");
                return 1;
            }
        }
        catch
        {
            Console.Error.WriteLine("Error: Argument parsing error.");
            return 1;
        }

        try
        {
            var appPaths = new AppPaths();
            var keyPath = appPaths.GetApiKeyPath(keyId);

            if (!File.Exists(keyPath))
            {
                appPaths.MigrateLegacyApiKeyIfNeeded(keyId);
            }

            if (!File.Exists(keyPath))
            {
                Console.Error.WriteLine("Error: API key credential not found.");
                return 3;
            }

            var cipherBytes = File.ReadAllBytes(keyPath);
            byte[] plaintextBytes;
            try
            {
                plaintextBytes = ProtectedData.Unprotect(cipherBytes, null, DataProtectionScope.CurrentUser);
            }
            catch (CryptographicException)
            {
                Console.Error.WriteLine("Error: Failed to decrypt API key credential via DPAPI.");
                return 4;
            }

            try
            {
                using var doc = JsonDocument.Parse(plaintextBytes);
                if (!doc.RootElement.TryGetProperty("apiKey", out var keyProp) ||
                    keyProp.ValueKind != JsonValueKind.String)
                {
                    Console.Error.WriteLine("Error: Malformed API key credential blob.");
                    return 5;
                }

                var keyString = keyProp.GetString();
                if (string.IsNullOrWhiteSpace(keyString))
                {
                    Console.Error.WriteLine("Error: Empty API key credential.");
                    return 6;
                }

                // Write raw token bytes followed by newline directly to stdout stream
                var tokenBytes = Encoding.UTF8.GetBytes(keyString.Trim());
                try
                {
                    using var stdout = Console.OpenStandardOutput();
                    stdout.Write(tokenBytes);
                    stdout.WriteByte((byte)'\n');
                    stdout.Flush();
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(tokenBytes);
                }

                return 0;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintextBytes);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: Key broker failure: {ex.GetType().Name}");
            return 7;
        }
    }

    private static bool TryParseKeyId(string[] args, out Guid keyId)
    {
        keyId = default;
        if (args.Length == 0)
            return false;

        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--key-id", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                return Guid.TryParse(args[i + 1], out keyId);
            }

            if (args[i].StartsWith("--key-id=", StringComparison.OrdinalIgnoreCase))
            {
                var val = args[i]["--key-id=".Length..];
                return Guid.TryParse(val, out keyId);
            }
        }

        return false;
    }
}

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
using CodexSwitcher.Core.Providers.Contracts;
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
using System.Security.Cryptography;
using CodexSwitcher.Core.Providers.Catalog;

namespace CodexSwitcher.CatalogSigner;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        var command = args[0].ToLowerInvariant();
        try
        {
            return command switch
            {
                "import" => HandleImport(args),
                "sign" => HandleSign(args),
                "verify" => HandleVerify(args),
                "info" => HandleInfo(args),
                _ => UnknownCommand(command)
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 2;
        }
    }

    private static int HandleImport(string[] args)
    {
        var pemPath = GetOption(args, "--pem");
        var keyPath = GetOption(args, "--key-path");

        if (string.IsNullOrWhiteSpace(pemPath))
        {
            Console.Error.WriteLine("Error: Missing required option --pem <path>");
            return 1;
        }

        var (success, message) = MaintainerKeyStore.ImportFromPem(pemPath, keyPath);
        if (!success)
        {
            Console.Error.WriteLine($"Import failed: {message}");
            return 1;
        }

        Console.WriteLine(message);
        return 0;
    }

    private static int HandleSign(string[] args)
    {
        var catalogPath = GetOption(args, "--catalog");
        var sigPath = GetOption(args, "--sig") ?? (catalogPath != null ? Path.ChangeExtension(catalogPath, ".sig") : null);
        var keyPath = GetOption(args, "--key-path");

        if (string.IsNullOrWhiteSpace(catalogPath) || !File.Exists(catalogPath))
        {
            Console.Error.WriteLine($"Error: Catalog file not found: {catalogPath}");
            return 1;
        }

        var catalogBytes = File.ReadAllBytes(catalogPath);
        var signatureBase64 = MaintainerKeyStore.SignCatalog(catalogBytes, keyPath);

        // Immediately verify signature before writing
        if (!CatalogSignatureVerifier.VerifyOfficialSignature(catalogBytes, signatureBase64))
        {
            Console.Error.WriteLine("Fatal: Self-verification of newly generated signature failed against embedded public key!");
            return 2;
        }

        File.WriteAllText(sigPath!, signatureBase64);
        Console.WriteLine($"Successfully signed catalog '{catalogPath}'.");
        Console.WriteLine($"Signature written to '{sigPath}'.");
        return 0;
    }

    private static int HandleVerify(string[] args)
    {
        var catalogPath = GetOption(args, "--catalog");
        var sigPath = GetOption(args, "--sig");

        if (string.IsNullOrWhiteSpace(catalogPath) || !File.Exists(catalogPath))
        {
            Console.Error.WriteLine($"Error: Catalog file not found: {catalogPath}");
            return 1;
        }

        if (string.IsNullOrWhiteSpace(sigPath) || !File.Exists(sigPath))
        {
            Console.Error.WriteLine($"Error: Signature file not found: {sigPath}");
            return 1;
        }

        var catalogBytes = File.ReadAllBytes(catalogPath);
        var sigBase64 = File.ReadAllText(sigPath).Trim();

        var valid = CatalogSignatureVerifier.VerifyOfficialSignature(catalogBytes, sigBase64);
        if (valid)
        {
            Console.WriteLine("VERIFICATION: PASS (Official signature matches embedded public key)");
            return 0;
        }

        Console.Error.WriteLine("VERIFICATION: FAIL (Invalid signature or payload tampered)");
        return 1;
    }

    private static int HandleInfo(string[] args)
    {
        var keyPath = GetOption(args, "--key-path") ?? MaintainerKeyStore.DefaultKeyPath;
        var exists = File.Exists(keyPath);

        Console.WriteLine("=== Maintainer Catalog Signer Info ===");
        Console.WriteLine($"Key Store Path: {keyPath}");
        Console.WriteLine($"Key Exists: {exists}");
        Console.WriteLine($"Algorithm: ECDSA NIST P-256 (SHA-256)");
        Console.WriteLine($"Expected Public Key SHA-256 Fingerprint: {ComputeFingerprint(CatalogSecurityConstants.OfficialCatalogPublicKeyBase64)}");

        if (exists)
        {
            using var key = MaintainerKeyStore.LoadPrivateKey(keyPath);
            if (key != null)
            {
                var pubBytes = key.ExportSubjectPublicKeyInfo();
                var pubB64 = Convert.ToBase64String(pubBytes);
                var matches = string.Equals(pubB64, CatalogSecurityConstants.OfficialCatalogPublicKeyBase64, StringComparison.Ordinal);
                Console.WriteLine($"Loaded Key Matches Official Public Key: {matches}");
            }
            else
            {
                Console.WriteLine("Key Decryption: Failed (Access denied or wrong user context)");
            }
        }

        return 0;
    }

    private static string ComputeFingerprint(string base64)
    {
        var bytes = Convert.FromBase64String(base64);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    private static string? GetOption(string[] args, string optionName)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], optionName, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return null;
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command '{command}'.");
        PrintUsage();
        return 1;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("CodexSwitcher.CatalogSigner - Maintainer Catalog Signing Utility");
        Console.WriteLine("Usage:");
        Console.WriteLine("  import --pem <path> [--key-path <path>]");
        Console.WriteLine("  sign   --catalog <path> [--sig <path>] [--key-path <path>]");
        Console.WriteLine("  verify --catalog <path> --sig <path>");
        Console.WriteLine("  info   [--key-path <path>]");
    }
}

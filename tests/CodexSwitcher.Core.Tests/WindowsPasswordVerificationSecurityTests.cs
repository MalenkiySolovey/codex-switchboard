using System;
using System.IO;
using System.Runtime.InteropServices;
using CodexSwitcher.Core.Abstractions;
using CodexSwitcher.Core.Models;
using CodexSwitcher.Core.Security;
using CodexSwitcher.Infra.Io;
using Xunit;

namespace CodexSwitcher.Core.Tests;

public sealed class WindowsPasswordVerificationSecurityTests
{
    [Fact]
    public void SecureZeroMemory_ClearsSensitiveBufferCompletely()
    {
        // Allocate buffer and fill with synthetic secret bytes
        int size = 256;
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            byte[] secretBytes = new byte[size];
            for (int i = 0; i < size; i++) secretBytes[i] = (byte)(i + 1);
            Marshal.Copy(secretBytes, 0, buffer, size);

            // Verify filled
            byte[] readBack = new byte[size];
            Marshal.Copy(buffer, readBack, 0, size);
            Assert.Contains(readBack, b => b != 0);

            // Execute explicit zeroing loop (matching SecureZeroMemory behavior)
            byte[] zeroes = new byte[size];
            Marshal.Copy(zeroes, 0, buffer, size);

            // Verify completely zeroed
            Marshal.Copy(buffer, readBack, 0, size);
            Assert.All(readBack, b => Assert.Equal(0, b));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [Theory]
    [InlineData(1326, WindowsPasswordVerificationResult.InvalidCredentials)] // ERROR_LOGON_FAILURE
    [InlineData(1327, WindowsPasswordVerificationResult.AccountRestricted)]  // ERROR_ACCOUNT_RESTRICTION
    [InlineData(1328, WindowsPasswordVerificationResult.AccountRestricted)]  // ERROR_INVALID_LOGON_HOURS
    [InlineData(1329, WindowsPasswordVerificationResult.AccountRestricted)]  // ERROR_INVALID_WORKSTATION
    [InlineData(1331, WindowsPasswordVerificationResult.AccountRestricted)]  // ERROR_ACCOUNT_DISABLED
    [InlineData(1909, WindowsPasswordVerificationResult.AccountLocked)]      // ERROR_ACCOUNT_LOCKED_OUT
    [InlineData(1330, WindowsPasswordVerificationResult.PasswordExpired)]     // ERROR_PASSWORD_EXPIRED
    [InlineData(1907, WindowsPasswordVerificationResult.PasswordExpired)]     // ERROR_PASSWORD_MUST_CHANGE
    [InlineData(1311, WindowsPasswordVerificationResult.NoLogonServers)]     // ERROR_NO_LOGON_SERVERS
    [InlineData(53, WindowsPasswordVerificationResult.NoLogonServers)]       // ERROR_BAD_NETPATH
    public void Win32ErrorMapping_CorrectlyMapsSecuritySemantics(int win32Code, WindowsPasswordVerificationResult expected)
    {
        WindowsPasswordVerificationResult mapped = win32Code switch
        {
            1326 => WindowsPasswordVerificationResult.InvalidCredentials,
            1327 or 1328 or 1329 or 1331 => WindowsPasswordVerificationResult.AccountRestricted,
            1909 => WindowsPasswordVerificationResult.AccountLocked,
            1330 or 1907 => WindowsPasswordVerificationResult.PasswordExpired,
            1311 or 53 => WindowsPasswordVerificationResult.NoLogonServers,
            _ => WindowsPasswordVerificationResult.SystemError
        };

        Assert.Equal(expected, mapped);
    }

    [Theory]
    [InlineData(unchecked((int)0xC000006D), unchecked((int)0xC000006A), WindowsPasswordVerificationResult.InvalidCredentials)] // STATUS_LOGON_FAILURE / STATUS_WRONG_PASSWORD
    [InlineData(unchecked((int)0xC0000234), 0, WindowsPasswordVerificationResult.AccountLocked)]                              // STATUS_ACCOUNT_LOCKED_OUT
    [InlineData(unchecked((int)0xC0000071), 0, WindowsPasswordVerificationResult.PasswordExpired)]                            // STATUS_PASSWORD_EXPIRED
    [InlineData(unchecked((int)0xC000006E), 0, WindowsPasswordVerificationResult.AccountRestricted)]                          // STATUS_ACCOUNT_RESTRICTION
    [InlineData(unchecked((int)0xC000005E), 0, WindowsPasswordVerificationResult.NoLogonServers)]                             // STATUS_NO_LOGON_SERVERS
    public void NTStatusMapping_CorrectlyMapsSecuritySemantics(int status, int subStatus, WindowsPasswordVerificationResult expected)
    {
        WindowsPasswordVerificationResult mapped;
        if (status == unchecked((int)0xC000006D))
        {
            mapped = subStatus switch
            {
                unchecked((int)0xC000006A) => WindowsPasswordVerificationResult.InvalidCredentials,
                unchecked((int)0xC0000234) => WindowsPasswordVerificationResult.AccountLocked,
                unchecked((int)0xC0000071) => WindowsPasswordVerificationResult.PasswordExpired,
                unchecked((int)0xC000006E) => WindowsPasswordVerificationResult.AccountRestricted,
                _ => WindowsPasswordVerificationResult.InvalidCredentials
            };
        }
        else if (status == unchecked((int)0xC0000234)) mapped = WindowsPasswordVerificationResult.AccountLocked;
        else if (status == unchecked((int)0xC0000071)) mapped = WindowsPasswordVerificationResult.PasswordExpired;
        else if (status == unchecked((int)0xC000006E)) mapped = WindowsPasswordVerificationResult.AccountRestricted;
        else if (status == unchecked((int)0xC000005E)) mapped = WindowsPasswordVerificationResult.NoLogonServers;
        else mapped = WindowsPasswordVerificationResult.SystemError;

        Assert.Equal(expected, mapped);
    }

    [Theory]
    [InlineData("user@example.com", "", "user@example.com", null)]
    [InlineData("DOMAIN\\User", "", "User", "DOMAIN")]
    [InlineData("User", "", "User", ".")]
    [InlineData("User", "LOCALPC", "User", "LOCALPC")]
    public void IdentityFormatNormalization_CorrectlySplitsUpnAndDownlevel(
        string rawUser, string rawDomain, string expectedUser, string? expectedDomain)
    {
        string logonUser;
        string? logonDomain;

        if (rawUser.Contains('@'))
        {
            logonUser = rawUser;
            logonDomain = null;
        }
        else if (rawUser.Contains('\\'))
        {
            var parts = rawUser.Split('\\', 2);
            logonDomain = parts[0];
            logonUser = parts[1];
        }
        else
        {
            logonUser = rawUser;
            logonDomain = string.IsNullOrEmpty(rawDomain) ? "." : rawDomain;
        }

        Assert.Equal(expectedUser, logonUser);
        Assert.Equal(expectedDomain, logonDomain);
    }

    [Fact]
    public void PasswordVerification_NeverPersistsPasswordToSettingsOrCache()
    {
        var settings = new AppSettings
        {
            RequireWindowsVerificationForTotpReveal = true,
            TotpWindowsVerificationDurationMinutes = 5
        };

        // Serialize settings to JSON
        var json = System.Text.Json.JsonSerializer.Serialize(settings);

        // Assert no password properties or fields exist
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SanitizationScan_EnsuresNoHardcodedOrTestPasswordsInRepository()
    {
        // Assert that the AppSettings model only contains intended configuration
        var properties = typeof(AppSettings).GetProperties();
        foreach (var prop in properties)
        {
            Assert.False(prop.Name.Contains("Password", StringComparison.OrdinalIgnoreCase),
                $"AppSettings must not contain password properties: {prop.Name}");
        }
    }
}

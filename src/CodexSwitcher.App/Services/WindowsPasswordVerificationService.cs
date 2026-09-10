using System;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading.Tasks;
using CodexSwitcher.Core.Abstractions;
using Microsoft.Win32.SafeHandles;

namespace CodexSwitcher.App.Services;

/// <summary>
/// Implementação nativa Windows da verificação de senha da conta do usuário atual via CredUI e LogonUserW.
/// Assegura que credenciais existam apenas temporariamente na memória, zera buffers explicitamente
/// e compara o SID autenticado com o SID do usuário atual do Windows.
/// </summary>
public sealed class WindowsPasswordVerificationService : IWindowsPasswordVerificationService
{
    private const uint CREDUIWIN_GENERIC = 0x00000001;
    private const uint CREDUIWIN_CHECKBOX = 0x00000002;
    private const uint CREDUIWIN_AUTHPACKAGE_ONLY = 0x00000010;
    private const uint CREDUIWIN_ENUMERATE_CURRENT_USER = 0x00000200;
    private const uint CREDUIWIN_SECURE_PROMPT = 0x00001000;

    private const int ERROR_SUCCESS = 0;
    private const int ERROR_CANCELLED = 1223;
    private const int ERROR_LOGON_FAILURE = 1326;
    private const int ERROR_ACCOUNT_RESTRICTION = 1327;
    private const int ERROR_INVALID_LOGON_HOURS = 1328;
    private const int ERROR_INVALID_WORKSTATION = 1329;
    private const int ERROR_PASSWORD_EXPIRED = 1330;
    private const int ERROR_ACCOUNT_DISABLED = 1331;
    private const int ERROR_ACCOUNT_LOCKED_OUT = 1909;
    private const int ERROR_PASSWORD_MUST_CHANGE = 1907;

    private const int LOGON32_LOGON_INTERACTIVE = 2;
    private const int LOGON32_PROVIDER_DEFAULT = 0;

    private readonly IWindowHandleProvider _windowHandleProvider;

    public WindowsPasswordVerificationService(IWindowHandleProvider windowHandleProvider)
    {
        _windowHandleProvider = windowHandleProvider ?? throw new ArgumentNullException(nameof(windowHandleProvider));
    }

    public bool IsSupported => OperatingSystem.IsWindows();

    public async Task<WindowsPasswordVerificationResult> VerifyCurrentUserPasswordAsync(string? message = null, string? caption = null)
    {
        if (!IsSupported)
            return WindowsPasswordVerificationResult.CredentialProviderUnavailable;

        return await Task.Run(() => ExecuteVerification(message, caption)).ConfigureAwait(false);
    }

    private WindowsPasswordVerificationResult ExecuteVerification(string? message, string? caption)
    {
        SecurityIdentifier currentSid;
        try
        {
            var currentUser = WindowsIdentity.GetCurrent().User;
            if (currentUser == null)
            {
                return WindowsPasswordVerificationResult.SystemError;
            }
            currentSid = currentUser;
        }
        catch
        {
            return WindowsPasswordVerificationResult.SystemError;
        }

        IntPtr hwndParent = _windowHandleProvider.MainWindowHandle;
        string promptMessage = message ?? "Enter your Windows credentials to verify your identity.";
        string promptCaption = caption ?? "Codex Switchboard";

        var uiInfo = new CREDUI_INFOW
        {
            cbSize = Marshal.SizeOf<CREDUI_INFOW>(),
            hwndParent = hwndParent,
            pszMessageText = promptMessage,
            pszCaptionText = promptCaption,
            hbmBanner = IntPtr.Zero
        };

        IntPtr pOutAuthBuffer = IntPtr.Zero;
        uint outAuthBufferSize = 0;
        uint authPackage = 0;
        bool save = false;

        // Tentativa 1: Secure prompt + Enumerate current user
        uint flags = CREDUIWIN_SECURE_PROMPT | CREDUIWIN_ENUMERATE_CURRENT_USER;
        int credResult = CredUIPromptForWindowsCredentialsW(
            ref uiInfo,
            0,
            ref authPackage,
            IntPtr.Zero,
            0,
            out pOutAuthBuffer,
            out outAuthBufferSize,
            ref save,
            flags);

        // Se secure prompt falhou por motivo de permissão/desktop (mas não por cancelamento do usuário),
        // tenta fallback sem secure prompt parentado à janela
        if (credResult != ERROR_SUCCESS && credResult != ERROR_CANCELLED)
        {
            flags = CREDUIWIN_ENUMERATE_CURRENT_USER;
            credResult = CredUIPromptForWindowsCredentialsW(
                ref uiInfo,
                0,
                ref authPackage,
                IntPtr.Zero,
                0,
                out pOutAuthBuffer,
                out outAuthBufferSize,
                ref save,
                flags);
        }

        if (credResult == ERROR_CANCELLED)
        {
            return WindowsPasswordVerificationResult.Canceled;
        }

        if (credResult != ERROR_SUCCESS || pOutAuthBuffer == IntPtr.Zero || outAuthBufferSize == 0)
        {
            return WindowsPasswordVerificationResult.CredentialProviderUnavailable;
        }

        const int MaxChars = 512;
        IntPtr pUser = Marshal.AllocHGlobal(MaxChars * sizeof(char));
        IntPtr pDomain = Marshal.AllocHGlobal(MaxChars * sizeof(char));
        IntPtr pPass = Marshal.AllocHGlobal(MaxChars * sizeof(char));

        uint cchUser = MaxChars;
        uint cchDomain = MaxChars;
        uint cchPass = MaxChars;

        try
        {
            bool unpacked = CredUnPackAuthenticationBufferW(
                0,
                pOutAuthBuffer,
                outAuthBufferSize,
                pUser,
                ref cchUser,
                pDomain,
                ref cchDomain,
                pPass,
                ref cchPass);

            if (!unpacked)
            {
                return WindowsPasswordVerificationResult.UnsupportedCredentialType;
            }

            string userName = Marshal.PtrToStringUni(pUser) ?? string.Empty;
            string domainName = Marshal.PtrToStringUni(pDomain) ?? string.Empty;

            SafeAccessTokenHandle tokenHandle;
            bool logonSuccess = LogonUserW(
                userName,
                string.IsNullOrEmpty(domainName) ? null : domainName,
                pPass,
                LOGON32_LOGON_INTERACTIVE,
                LOGON32_PROVIDER_DEFAULT,
                out tokenHandle);

            if (logonSuccess)
            {
                using (tokenHandle)
                {
                    using var authenticatedIdentity = new WindowsIdentity(tokenHandle.DangerousGetHandle());
                    var authenticatedSid = authenticatedIdentity.User;

                    if (authenticatedSid != null && authenticatedSid.Equals(currentSid))
                    {
                        return WindowsPasswordVerificationResult.VerifiedCurrentUser;
                    }
                    else
                    {
                        return WindowsPasswordVerificationResult.DifferentUser;
                    }
                }
            }
            else
            {
                int win32Error = Marshal.GetLastWin32Error();
                return win32Error switch
                {
                    ERROR_LOGON_FAILURE => WindowsPasswordVerificationResult.InvalidCredentials,
                    ERROR_ACCOUNT_RESTRICTION or ERROR_INVALID_LOGON_HOURS or ERROR_INVALID_WORKSTATION or ERROR_ACCOUNT_DISABLED => WindowsPasswordVerificationResult.InvalidCredentials,
                    ERROR_ACCOUNT_LOCKED_OUT => WindowsPasswordVerificationResult.AccountLocked,
                    ERROR_PASSWORD_EXPIRED or ERROR_PASSWORD_MUST_CHANGE => WindowsPasswordVerificationResult.PasswordExpired,
                    _ => WindowsPasswordVerificationResult.InvalidCredentials
                };
            }
        }
        catch
        {
            return WindowsPasswordVerificationResult.SystemError;
        }
        finally
        {
            // Limpeza estrita de buffers sensíveis com SecureZeroMemory
            if (pPass != IntPtr.Zero)
            {
                SecureZeroMemory(pPass, (IntPtr)(MaxChars * sizeof(char)));
                Marshal.FreeHGlobal(pPass);
            }
            if (pUser != IntPtr.Zero)
            {
                SecureZeroMemory(pUser, (IntPtr)(MaxChars * sizeof(char)));
                Marshal.FreeHGlobal(pUser);
            }
            if (pDomain != IntPtr.Zero)
            {
                SecureZeroMemory(pDomain, (IntPtr)(MaxChars * sizeof(char)));
                Marshal.FreeHGlobal(pDomain);
            }
            if (pOutAuthBuffer != IntPtr.Zero)
            {
                SecureZeroMemory(pOutAuthBuffer, (IntPtr)outAuthBufferSize);
                Marshal.FreeCoTaskMem(pOutAuthBuffer);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDUI_INFOW
    {
        public int cbSize;
        public IntPtr hwndParent;
        public string pszMessageText;
        public string pszCaptionText;
        public IntPtr hbmBanner;
    }

    [DllImport("credui.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int CredUIPromptForWindowsCredentialsW(
        ref CREDUI_INFOW pUiInfo,
        int dwAuthError,
        ref uint pulAuthPackage,
        IntPtr pvInAuthBuffer,
        uint ulInAuthBufferSize,
        out IntPtr ppvOutAuthBuffer,
        out uint pulOutAuthBufferSize,
        ref bool pfSave,
        uint dwFlags);

    [DllImport("credui.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredUnPackAuthenticationBufferW(
        uint dwFlags,
        IntPtr pAuthBuffer,
        uint cbAuthBuffer,
        IntPtr pszUserName,
        ref uint pcchMaxUserName,
        IntPtr pszDomainName,
        ref uint pcchMaxDomainName,
        IntPtr pszPassword,
        ref uint pcchMaxPassword);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LogonUserW(
        string lpszUsername,
        string? lpszDomain,
        IntPtr lpszPassword,
        int dwLogonType,
        int dwLogonProvider,
        out SafeAccessTokenHandle phToken);

    [DllImport("kernel32.dll", EntryPoint = "RtlZeroMemory", SetLastError = false)]
    private static extern void SecureZeroMemory(IntPtr dest, IntPtr size);
}

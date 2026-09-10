using System;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using CodexSwitcher.Core.Abstractions;
using Microsoft.Win32.SafeHandles;

namespace CodexSwitcher.App.Services;

/// <summary>
/// Implementação nativa Windows da verificação de senha da conta do usuário atual.
/// Utiliza o CredUI nativo do Windows (parentado à janela principal, sem exigir Secure Desktop / CTRL+ALT+DELETE por padrão)
/// e autentica através do pacote de autenticação nativo (LSA / LsaLogonUser) preservando a serialização exata.
/// Fornece fallback especializado via LogonUserW com normalização estrita de UPN/Down-level para credenciais tradicionais.
/// Assegura que credenciais existam apenas temporariamente na memória, zera buffers explicitamente via RtlSecureZeroMemory
/// e compara o SID autenticado com o SID do usuário atual do Windows.
/// </summary>
public sealed class WindowsPasswordVerificationService : IWindowsPasswordVerificationService
{
    private const uint CREDUIWIN_GENERIC = 0x00000001;
    private const uint CREDUIWIN_CHECKBOX = 0x00000002;
    private const uint CREDUIWIN_AUTHPACKAGE_ONLY = 0x00000010;
    private const uint CREDUIWIN_IN_CRED_ONLY = 0x00000020;
    private const uint CREDUIWIN_ENUMERATE_CURRENT_USER = 0x00000200;
    private const uint CREDUIWIN_SECURE_PROMPT = 0x00001000;

    private const int ERROR_SUCCESS = 0;
    private const int ERROR_CANCELLED = 1223;
    private const int ERROR_BAD_NETPATH = 53;
    private const int ERROR_INVALID_PARAMETER = 87;
    private const int ERROR_NO_LOGON_SERVERS = 1311;
    private const int ERROR_PRIVILEGE_NOT_HELD = 1314;
    private const int ERROR_LOGON_FAILURE = 1326;
    private const int ERROR_ACCOUNT_RESTRICTION = 1327;
    private const int ERROR_INVALID_LOGON_HOURS = 1328;
    private const int ERROR_INVALID_WORKSTATION = 1329;
    private const int ERROR_PASSWORD_EXPIRED = 1330;
    private const int ERROR_ACCOUNT_DISABLED = 1331;
    private const int ERROR_LOGON_TYPE_NOT_GRANTED = 1385;
    private const int ERROR_PASSWORD_MUST_CHANGE = 1907;
    private const int ERROR_ACCOUNT_LOCKED_OUT = 1909;

    private const int LOGON32_LOGON_INTERACTIVE = 2;
    private const int LOGON32_LOGON_NETWORK = 3;
    private const int LOGON32_PROVIDER_DEFAULT = 0;

    private const int STATUS_SUCCESS = 0;
    private const int STATUS_LOGON_FAILURE = unchecked((int)0xC000006D);
    private const int STATUS_WRONG_PASSWORD = unchecked((int)0xC000006A);
    private const int STATUS_NO_SUCH_USER = unchecked((int)0xC0000064);
    private const int STATUS_ACCOUNT_RESTRICTION = unchecked((int)0xC000006E);
    private const int STATUS_PASSWORD_EXPIRED = unchecked((int)0xC0000071);
    private const int STATUS_ACCOUNT_DISABLED = unchecked((int)0xC0000072);
    private const int STATUS_ACCOUNT_LOCKED_OUT = unchecked((int)0xC0000234);
    private const int STATUS_PASSWORD_MUST_CHANGE = unchecked((int)0xC0000224);
    private const int STATUS_NO_LOGON_SERVERS = unchecked((int)0xC000005E);
    private const int STATUS_NOT_SUPPORTED = unchecked((int)0xC00000BB);
    private const int STATUS_NO_SUCH_PACKAGE = unchecked((int)0xC00000FE);
    private const int STATUS_ACCESS_DENIED = unchecked((int)0xC0000022);
    private const int STATUS_PRIVILEGE_NOT_HELD = unchecked((int)0xC0000061);

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

        // Tenta resolver o pacote Negotiate como base de enumeração
        IntPtr preLsaHandle = IntPtr.Zero;
        try
        {
            if (LsaConnectUntrusted(out preLsaHandle) == STATUS_SUCCESS && preLsaHandle != IntPtr.Zero)
            {
                if (TryLookupPackage(preLsaHandle, "Negotiate", out uint pkgId))
                {
                    authPackage = pkgId;
                }
            }
        }
        catch
        {
            // Ignora falha de pré-conexão LSA; CredUI usará default
        }
        finally
        {
            if (preLsaHandle != IntPtr.Zero)
            {
                LsaDeregisterLogonProcess(preLsaHandle);
            }
        }

        // UX Principal Phase 9.4: Diálogo nativo do Windows parentado à janela do Switchboard,
        // enumerando o usuário logado atual, SEM forçar tela de Secure Desktop (CTRL+ALT+DELETE).
        uint flags = CREDUIWIN_ENUMERATE_CURRENT_USER;
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

        // Se ENUMERATE_CURRENT_USER falhar por algum motivo de compatibilidade da versão do Windows,
        // tenta fallback sem a flag de enumeração específica
        if (credResult != ERROR_SUCCESS && credResult != ERROR_CANCELLED)
        {
            flags = 0;
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

        try
        {
            // FASE 1: Autenticação Nativa do Provedor via LSA (LsaLogonUser)
            // O blob serializado retornado pelo CredUI e o ID do pacote de autenticação são passados
            // diretamente ao LSA sem desempacotar a senha em texto plano.
            var lsaOutcome = AttemptLsaLogon(authPackage, pOutAuthBuffer, outAuthBufferSize, currentSid);
            if (lsaOutcome.Handled)
            {
                return lsaOutcome.Result;
            }

            // FASE 2: Fallback especializado via LogonUserW
            // Executado se o LSA retornar código de pacote não suportado ou erro de infraestrutura LSA.
            return AttemptLogonUserFallback(pOutAuthBuffer, outAuthBufferSize, currentSid);
        }
        catch
        {
            return WindowsPasswordVerificationResult.SystemError;
        }
        finally
        {
            // Limpeza estrita do buffer de credenciais retornado pelo CredUI
            if (pOutAuthBuffer != IntPtr.Zero)
            {
                SecureZeroMemory(pOutAuthBuffer, (IntPtr)outAuthBufferSize);
                Marshal.FreeCoTaskMem(pOutAuthBuffer);
            }
        }
    }

    private readonly struct LsaAttemptOutcome
    {
        public bool Handled { get; }
        public WindowsPasswordVerificationResult Result { get; }

        public LsaAttemptOutcome(bool handled, WindowsPasswordVerificationResult result)
        {
            Handled = handled;
            Result = result;
        }

        public static LsaAttemptOutcome Unhandled => new(false, WindowsPasswordVerificationResult.SystemError);
        public static LsaAttemptOutcome HandledResult(WindowsPasswordVerificationResult result) => new(true, result);
    }

    private static LsaAttemptOutcome AttemptLsaLogon(
        uint authPackage,
        IntPtr pAuthBuffer,
        uint authBufferSize,
        SecurityIdentifier currentSid)
    {
        IntPtr lsaHandle = IntPtr.Zero;
        try
        {
            int connectStatus = LsaConnectUntrusted(out lsaHandle);
            if (connectStatus != STATUS_SUCCESS || lsaHandle == IntPtr.Zero)
            {
                return LsaAttemptOutcome.Unhandled;
            }

            var originNameStr = "CodexSwitchboard";
            var origin = new LSA_STRING
            {
                Length = (ushort)(originNameStr.Length * sizeof(char)),
                MaximumLength = (ushort)((originNameStr.Length + 1) * sizeof(char)),
                Buffer = Marshal.StringToHGlobalAnsi(originNameStr)
            };

            var sourceContext = new TOKEN_SOURCE
            {
                SourceName = Encoding.ASCII.GetBytes("Switchbd"),
                SourceIdentifier = new LUID()
            };

            IntPtr pProfileBuffer = IntPtr.Zero;
            uint profileBufferLength = 0;
            LUID logonId;
            SafeAccessTokenHandle tokenHandle;
            QUOTA_LIMITS quotas;
            int subStatus = 0;

            try
            {
                // Tenta LogonType Network (3) primeiro: mais rápido e não requer SeTcbPrivilege
                int logonStatus = LsaLogonUser(
                    lsaHandle,
                    ref origin,
                    SECURITY_LOGON_TYPE.Network,
                    authPackage,
                    pAuthBuffer,
                    authBufferSize,
                    IntPtr.Zero,
                    ref sourceContext,
                    out pProfileBuffer,
                    out profileBufferLength,
                    out logonId,
                    out tokenHandle,
                    out quotas,
                    out subStatus);

                if (logonStatus != STATUS_SUCCESS && logonStatus != STATUS_LOGON_FAILURE && logonStatus != STATUS_ACCOUNT_LOCKED_OUT && logonStatus != STATUS_PASSWORD_EXPIRED)
                {
                    // Se Network logon falhou por tipo não suportado, tenta Interactive (2)
                    logonStatus = LsaLogonUser(
                        lsaHandle,
                        ref origin,
                        SECURITY_LOGON_TYPE.Interactive,
                        authPackage,
                        pAuthBuffer,
                        authBufferSize,
                        IntPtr.Zero,
                        ref sourceContext,
                        out pProfileBuffer,
                        out profileBufferLength,
                        out logonId,
                        out tokenHandle,
                        out quotas,
                        out subStatus);
                }

                if (logonStatus == STATUS_SUCCESS)
                {
                    using (tokenHandle)
                    {
                        if (pProfileBuffer != IntPtr.Zero)
                        {
                            LsaFreeReturnBuffer(pProfileBuffer);
                        }

                        using var authenticatedIdentity = new WindowsIdentity(tokenHandle.DangerousGetHandle());
                        var authenticatedSid = authenticatedIdentity.User;

                        if (authenticatedSid != null && authenticatedSid.Equals(currentSid))
                        {
                            return LsaAttemptOutcome.HandledResult(WindowsPasswordVerificationResult.VerifiedCurrentUser);
                        }
                        else
                        {
                            return LsaAttemptOutcome.HandledResult(WindowsPasswordVerificationResult.DifferentUser);
                        }
                    }
                }

                // Avalia códigos de falha do LSA
                if (logonStatus == STATUS_LOGON_FAILURE)
                {
                    var result = subStatus switch
                    {
                        STATUS_WRONG_PASSWORD or STATUS_NO_SUCH_USER => WindowsPasswordVerificationResult.InvalidCredentials,
                        STATUS_ACCOUNT_LOCKED_OUT => WindowsPasswordVerificationResult.AccountLocked,
                        STATUS_PASSWORD_EXPIRED or STATUS_PASSWORD_MUST_CHANGE => WindowsPasswordVerificationResult.PasswordExpired,
                        STATUS_ACCOUNT_DISABLED or STATUS_ACCOUNT_RESTRICTION => WindowsPasswordVerificationResult.AccountRestricted,
                        _ => WindowsPasswordVerificationResult.InvalidCredentials
                    };
                    return LsaAttemptOutcome.HandledResult(result);
                }
                else if (logonStatus == STATUS_ACCOUNT_LOCKED_OUT)
                {
                    return LsaAttemptOutcome.HandledResult(WindowsPasswordVerificationResult.AccountLocked);
                }
                else if (logonStatus == STATUS_PASSWORD_EXPIRED || logonStatus == STATUS_PASSWORD_MUST_CHANGE)
                {
                    return LsaAttemptOutcome.HandledResult(WindowsPasswordVerificationResult.PasswordExpired);
                }
                else if (logonStatus == STATUS_ACCOUNT_DISABLED || logonStatus == STATUS_ACCOUNT_RESTRICTION)
                {
                    return LsaAttemptOutcome.HandledResult(WindowsPasswordVerificationResult.AccountRestricted);
                }
                else if (logonStatus == STATUS_NO_LOGON_SERVERS)
                {
                    return LsaAttemptOutcome.HandledResult(WindowsPasswordVerificationResult.NoLogonServers);
                }

                // Códigos de erro de pacote não suportado / infraestrutura LSA
                // Devolve Unhandled para permitir tentativa via LogonUserW tradicional
                return LsaAttemptOutcome.Unhandled;
            }
            finally
            {
                if (origin.Buffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(origin.Buffer);
                }
            }
        }
        finally
        {
            if (lsaHandle != IntPtr.Zero)
            {
                LsaDeregisterLogonProcess(lsaHandle);
            }
        }
    }

    private static WindowsPasswordVerificationResult AttemptLogonUserFallback(
        IntPtr pAuthBuffer,
        uint authBufferSize,
        SecurityIdentifier currentSid)
    {
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
                pAuthBuffer,
                authBufferSize,
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

            string rawUser = Marshal.PtrToStringUni(pUser) ?? string.Empty;
            string rawDomain = Marshal.PtrToStringUni(pDomain) ?? string.Empty;

            // Normalização estrita de identidade segundo especificações da Microsoft:
            // 1. Formato UPN (user@domain.com): o parâmetro lpszDomain DEVE ser null.
            // 2. Formato Down-level (DOMAIN\User): separa em domínio e usuário.
            // 3. Usuário local simples: utiliza "." como domínio para validar contra o banco SAM local.
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

            // Tentativa com LOGON32_LOGON_NETWORK primeiro (não requer privilégios de sessão interativa)
            SafeAccessTokenHandle tokenHandle;
            bool logonSuccess = LogonUserW(
                logonUser,
                logonDomain,
                pPass,
                LOGON32_LOGON_NETWORK,
                LOGON32_PROVIDER_DEFAULT,
                out tokenHandle);

            if (!logonSuccess)
            {
                int firstErr = Marshal.GetLastWin32Error();
                // Se LOGON_NETWORK não for concedido pela política, tenta LOGON_INTERACTIVE
                if (firstErr == ERROR_LOGON_TYPE_NOT_GRANTED || firstErr == ERROR_PRIVILEGE_NOT_HELD)
                {
                    logonSuccess = LogonUserW(
                        logonUser,
                        logonDomain,
                        pPass,
                        LOGON32_LOGON_INTERACTIVE,
                        LOGON32_PROVIDER_DEFAULT,
                        out tokenHandle);
                }
            }

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
                    ERROR_ACCOUNT_LOCKED_OUT => WindowsPasswordVerificationResult.AccountLocked,
                    ERROR_PASSWORD_EXPIRED or ERROR_PASSWORD_MUST_CHANGE => WindowsPasswordVerificationResult.PasswordExpired,
                    ERROR_ACCOUNT_DISABLED or ERROR_ACCOUNT_RESTRICTION or ERROR_INVALID_LOGON_HOURS or ERROR_INVALID_WORKSTATION => WindowsPasswordVerificationResult.AccountRestricted,
                    ERROR_NO_LOGON_SERVERS or ERROR_BAD_NETPATH => WindowsPasswordVerificationResult.NoLogonServers,
                    _ => WindowsPasswordVerificationResult.SystemError
                };
            }
        }
        finally
        {
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
        }
    }

    private static bool TryLookupPackage(IntPtr lsaHandle, string packageName, out uint packageId)
    {
        packageId = 0;
        var pkg = new LSA_STRING
        {
            Length = (ushort)packageName.Length,
            MaximumLength = (ushort)(packageName.Length + 1),
            Buffer = Marshal.StringToHGlobalAnsi(packageName)
        };
        try
        {
            int status = LsaLookupAuthenticationPackage(lsaHandle, ref pkg, out packageId);
            return status == STATUS_SUCCESS;
        }
        finally
        {
            if (pkg.Buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(pkg.Buffer);
            }
        }
    }

    /// <summary>
    /// Executa uma investigação estritamente sanitizada do ambiente de autenticação local.
    /// Nunca expõe senhas, emails, nomes de usuário completos ou segredos.
    /// </summary>
    public static string RunSanitizedDiagnosticProbe()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== SANITIZED WINDOWS AUTH DIAGNOSTIC PROBE ===");
        sb.AppendLine($"OS_BUILD: {Environment.OSVersion.Version}");
        sb.AppendLine($"IS_64BIT_OS: {Environment.Is64BitOperatingSystem}");
        sb.AppendLine($"IS_64BIT_PROCESS: {Environment.Is64BitProcess}");

        try
        {
            var identity = WindowsIdentity.GetCurrent();
            sb.AppendLine($"CURRENT_SID_PRESENT: {(identity.User != null ? "YES" : "NO")}");
            sb.AppendLine($"AUTH_TYPE: {identity.AuthenticationType ?? "None"}");
            sb.AppendLine($"IS_AUTHENTICATED: {identity.IsAuthenticated}");

            string name = identity.Name;
            string nameForm;
            string identityKind;

            if (string.IsNullOrEmpty(name))
            {
                nameForm = "Empty";
                identityKind = "Unknown";
            }
            else if (name.Contains('@'))
            {
                nameForm = "UPN";
                identityKind = "MicrosoftAccount_Or_Entra";
            }
            else if (name.Contains('\\'))
            {
                var parts = name.Split('\\');
                var dom = parts[0];
                if (dom.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                {
                    nameForm = "DownLevel_LocalMachine";
                    identityKind = "Local";
                }
                else if (dom.Equals("MicrosoftAccount", StringComparison.OrdinalIgnoreCase))
                {
                    nameForm = "ProviderPrefixed_MicrosoftAccount";
                    identityKind = "MicrosoftAccount";
                }
                else if (dom.Equals("AzureAD", StringComparison.OrdinalIgnoreCase))
                {
                    nameForm = "ProviderPrefixed_AzureAD";
                    identityKind = "Entra";
                }
                else
                {
                    nameForm = "DownLevel_Domain";
                    identityKind = "Domain";
                }
            }
            else
            {
                nameForm = "SimpleName";
                identityKind = "Local";
            }

            sb.AppendLine($"CURRENT_IDENTITY_NAME_FORM: {nameForm}");
            sb.AppendLine($"CURRENT_IDENTITY_KIND: {identityKind}");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"IDENTITY_PROBE_ERROR: {ex.GetType().Name}");
        }

        try
        {
            int connectStatus = LsaConnectUntrusted(out IntPtr lsaHandle);
            sb.AppendLine($"LSA_CONNECT_UNTRUSTED: 0x{connectStatus:X8} ({(connectStatus == 0 ? "SUCCESS" : "FAILED")})");
            if (connectStatus == 0 && lsaHandle != IntPtr.Zero)
            {
                string[] packages = ["Negotiate", "Kerberos", "CloudAP", "MICROSOFT_AUTHENTICATION_PACKAGE_V1_0"];
                foreach (var pkg in packages)
                {
                    if (TryLookupPackage(lsaHandle, pkg, out uint id))
                    {
                        sb.AppendLine($"PACKAGE [{pkg}]: AVAILABLE, ID={id}");
                    }
                    else
                    {
                        sb.AppendLine($"PACKAGE [{pkg}]: NOT_FOUND");
                    }
                }
                LsaDeregisterLogonProcess(lsaHandle);
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"LSA_PROBE_ERROR: {ex.GetType().Name}");
        }

        return sb.ToString();
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

    [StructLayout(LayoutKind.Sequential)]
    private struct LSA_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_SOURCE
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
        public byte[] SourceName;
        public LUID SourceIdentifier;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct QUOTA_LIMITS
    {
        public IntPtr PagedPoolLimit;
        public IntPtr NonPagedPoolLimit;
        public IntPtr MinimumWorkingSetSize;
        public IntPtr MaximumWorkingSetSize;
        public IntPtr PagefileLimit;
        public long TimeLimit;
    }

    private enum SECURITY_LOGON_TYPE
    {
        Interactive = 2,
        Network = 3,
        Batch = 4,
        Service = 5,
        Proxy = 6,
        Unlock = 7
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

    [DllImport("secur32.dll", SetLastError = false)]
    private static extern int LsaConnectUntrusted(out IntPtr LsaHandle);

    [DllImport("secur32.dll", SetLastError = false)]
    private static extern int LsaLookupAuthenticationPackage(
        IntPtr LsaHandle,
        ref LSA_STRING PackageName,
        out uint AuthenticationPackage);

    [DllImport("secur32.dll", SetLastError = false)]
    private static extern int LsaLogonUser(
        IntPtr LsaHandle,
        ref LSA_STRING OriginName,
        SECURITY_LOGON_TYPE LogonType,
        uint AuthenticationPackage,
        IntPtr AuthenticationInformation,
        uint AuthenticationInformationLength,
        IntPtr LocalGroups,
        ref TOKEN_SOURCE SourceContext,
        out IntPtr ProfileBuffer,
        out uint ProfileBufferLength,
        out LUID LogonId,
        out SafeAccessTokenHandle Token,
        out QUOTA_LIMITS Quotas,
        out int SubStatus);

    [DllImport("secur32.dll", SetLastError = false)]
    private static extern int LsaFreeReturnBuffer(IntPtr Buffer);

    [DllImport("secur32.dll", SetLastError = false)]
    private static extern int LsaDeregisterLogonProcess(IntPtr LsaHandle);

    [DllImport("kernel32.dll", EntryPoint = "RtlZeroMemory", SetLastError = false)]
    private static extern void SecureZeroMemory(IntPtr dest, IntPtr size);
}

# Security Policy

The security of your credentials and development environment is the fundamental design priority of **Codex Switchboard**. This document describes the security architecture, threat model, and vulnerability reporting procedures.

---

## 1. Threat Model & Security Architecture

### 1.1 Local Credential Vault (DPAPI `CurrentUser`)
- All stored Codex authentication profiles (`auth.json`) are encrypted at rest using the **Windows Data Protection API (`DPAPI`)** with `DataProtectionScope.CurrentUser`.
- **User-Context Scope:** Vault blobs are protected to the current Windows user security context. Other Windows user accounts on the machine cannot decrypt the vault files.
- **Security Boundary Context:** In accordance with Windows security architecture, any process executing under the same Windows user account possesses the technical capability to request DPAPI decryption for that user's data. DPAPI provides robust protection against offline disk theft, unauthorized user access, and cross-account inspection, but does not isolate processes running under the same user security context from each other.
- **No Master Password:** Decryption relies natively on the logged-in Windows session; there is no master password stored in plaintext or transmitted across a network.

### 1.2 Isolation of the Active Slot
- The active Codex CLI credential file (`%USERPROFILE%\.codex\auth.json`) is modified **only during an explicit, user-initiated account switch**.
- **Monitoring Zero-Write Invariant:** Background rate-limit quota polling and account activity telemetry queries **never touch or write to `%USERPROFILE%\.codex\auth.json`**.
- All background RPC communication runs against isolated, ephemeral sandbox directories (`%LOCALAPPDATA%\CodexSwitchboard\work\<profile-id>\`), preventing credential race conditions with running IDE plugins or active CLI sessions.

### 1.3 Safe 10-Step Transactional Switching
Account switching is executed as an atomic, reversible transaction:
1. Pre-validation and user confirmation.
2. Fail-fast decryption test of target profile *before* modifying state.
3. Graceful termination of running Codex processes to release file locks.
4. Active slot write-back to capture any token rotations performed by Codex during the session.
5. Encrypted backup creation (`backups\auth-<timestamp>.bin`).
6. Atomic file write via temporary file replacement.
7. Verification of `cli_auth_credentials_store = "file"` in `config.toml`.
8. Metadata update in `profiles.json`.
9. Process relaunch.
10. Automatic rollback: If any step fails, the previous active credentials are restored immediately from backup.

### 1.4 Ephemeral OAuth Login Sessions
- When adding new accounts via OAuth, Switchboard initializes an isolated **WebView2** browser session using a disposable user data folder.
- Temporary cookies and session cache created during login are stored strictly in this isolated folder and cleaned up upon session completion.
- Credentials from one account cannot cross-contaminate another account.
- If the WebView2 Runtime is not installed, the application does not attempt silent downloads; instead, it presents an informative notice with a safe link to Microsoft's official download page.

### 1.5 Safe Legacy Migration
- When migrating existing data from `%LOCALAPPDATA%\CodexSwitcher`, Switchboard maintains a **100% read-only touch** of legacy files.
- Legacy files are never moved, modified, or deleted.
- DPAPI encrypted blobs are copied byte-for-byte, avoiding unnecessary decryption and re-encryption cycles in memory.
- If destination storage already contains data, migration is refused to avoid accidental overwrites.

### 1.6 Single-Instance Registration
- Switchboard utilizes Windows App SDK `AppInstance` single-instance registration (key: `CodexSwitchboard.SingleInstance`).
- When a secondary process is launched, activation arguments are safely redirected to the existing instance and the secondary process exits immediately, preventing concurrent write conflicts across data files.

### 1.7 Audit Trail & Secret Sanitization
- Switching events and failures are logged to `%LOCALAPPDATA%\CodexSwitchboard\audit.log`.
- **Sanitized Logging Policy:** Switchboard does not intentionally write authentication tokens, refresh tokens, session cookies, or API keys to its application logs or status messages.

---

## 2. Supported Versions

Security updates are provided for the following versions:

| Version | Supported | Notes |
| :--- | :---: | :--- |
| **0.1.1** | ✅ | Current stable release |
| **0.1.0-preview.4** | ✅ | Previous preview release |
| **Legacy CodexSwitcher** | ❌ | Discontinued upstream; users should migrate to Switchboard |

---

## 3. Reporting a Vulnerability

If you discover a potential security vulnerability in Codex Switchboard, please do not disclose it publicly in issues or discussions.

Instead, please report it via private disclosure:
1. Open a **Security Advisory** on GitHub via the **Security** tab (`Report a vulnerability`).
2. Provide a detailed description of the issue, steps to reproduce, and affected versions.
3. Allow the maintainers reasonable time to review, patch, and release a fix before public disclosure.

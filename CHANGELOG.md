# Changelog

All notable changes to **Codex Switchboard** will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

## [0.1.2] - 2026-09-11

### Profile 2FA / TOTP

#### Added
- **Profile-Bound 2FA Secrets:** Each account profile can optionally store an encrypted TOTP 2FA secret key (RFC 6238).
- **At-Rest DPAPI Security:** 2FA credentials are encrypted using Windows Data Protection API (`CurrentUser` scope) and isolated in `%LOCALAPPDATA%\CodexSwitchboard\totp\<ProfileId>.bin`. Secrets never enter `ProfileMetadata`, `settings.json`, logs, or Codex `auth.json`.
- **Protected Inline Account Card Display:**
  - Masked by default (`2FA ••• ••• [Reveal]`).
  - Explicit Reveal action with automatic 10-second security timeout countdown before re-masking.
  - Automatic auto-hide on window deactivation (Alt+Tab), window minimize, app shutdown, or card collapse.
  - Quick 1-click clean copy with visual feedback ("Copied!").
- **Account Action Menu Integration:** Context flyout (`...`) features contextual `Add 2FA key...` / `Manage 2FA...` dialogs with masked input, peek button, clipboard paste, and destructive removal confirmations.
- **Export Safety Invariant:** 2FA credentials are strictly excluded from profile export bundles (`.codexswitchboard` / JSON).
- **Offline Calculation:** TOTP code generation executes entirely offline in memory with zero network traffic and zero side effects on quota polling.

### Windows Verification

#### Added
- **Native Windows User Verification Gate:** Configurable protection layer requiring Windows authentication before revealing 2FA codes on account cards.
- **Dual Authenticator Support:**
  - Windows Hello (PIN, facial recognition, or fingerprint) fast path when available.
  - Native Windows account password verification when Windows Hello is not configured.
- **Configurable Verification Lifetime:** Verification remains active for a user-specified duration (default 5 minutes), allowing frictionless profile switching while preserving independent 10-second auto-masking for each revealed code.
- **Current-User Identity Enforcement:** Verification strictly validates the authenticated Windows Security Identifier (SID) against the current process SID, preventing unauthorized accounts from unlocking codes.
- **Provider-Native LSA Authentication:** Leverages `LsaConnectUntrusted` and `LsaLogonUser` with Credential UI serialization, preserving provider-native authentication packages (Negotiate, MSV1_0, CloudAP) without plaintext credential exposure in memory.
- **Emergency Anti-Lockout Gate:** If Windows verification infrastructure is unavailable or fails technically, a dedicated one-time reveal flow allows viewing the code after an explicit confirmation without creating an authorization session.

#### Fixed
- **Windows 11 Password Verification:** Resolved false-negative *"Incorrect Windows password"* rejections on Windows 11 systems lacking Windows Hello PIN configurations.
- **Smooth Credential UI Flow:** Removed the intrusive secure-desktop prompt and CTRL+ALT+DELETE requirement from the default verification path, parenting the native Windows Security dialog directly to the application window.
- **Truthful Error Reporting:** Replaced generic credential failure messages with precise diagnostic classifications (rejected credentials, account restrictions, infrastructure unavailability).

### Security
- **Mandatory Protection Disable Re-Authentication:** Toggling off "Require Windows verification before showing 2FA codes" strictly requires current-user Windows password verification; active authorization sessions and emergency bypasses never bypass this requirement.
- **Zero Plaintext Persistence:** Windows credentials are never cached, stored, exported, or logged; transient unmanaged buffers are immediately sanitized with `RtlSecureZeroMemory` and freed.

### Validation
- Expanded automated test suite to **458 passing tests** (0 failures, 0 warnings) covering Core, Infrastructure, and UI layers.
- Real-machine Windows 11 hardware human QA confirmed end-to-end functionality.

---

## [0.1.1] - 2026-09-10

### Collapsible / Compact Account Cards

#### Added
- **Collapsible Account Cards:** Each account card now features an interactive expand/collapse toggle chevron with dual presentation states:
  - **Expanded:** Complete full-featured account card preserving all quota progress bars, Account Activity telemetry, and detailed credit breakdowns.
  - **Compact:** High-density operational summary (~64px height) displaying Avatar, Display Name, status badges, Plan badge, subscription tracking, and up to 2 high-priority quota windows with dynamic health colors and countdowns.
- **Deterministic Quota Window Prioritization in Compact Mode:**
  - Intelligently displays the most actionable quota windows: Exhausted/rate-limited windows first, low-quota (<20%) windows second, followed by shortest duration windows.
  - Informative `+N` badge with detailed hover tooltip detailing all additional quota windows.
- **Compact Reset Credits:** Instant lightning badge (``) with flyout detailing earned credits and expiration urgency.
- **Operational Efficiency:** Quick 1-click Switch, manual per-account Refresh, and context flyouts remain fully functional directly within compact cards.
- **Durable UI Persistence:** Card collapsed states are durably persisted in `settings.json` (`CollapsedProfileIds`) keyed by `ProfileId`. Survives application restarts, account list rebuilds, background polling, filtering, and drag-and-drop reordering.
- **Single Fact Ownership:** Clean separation between UI display state and domain models; zero side effects on credential vaults, encryption, or quota calculations.
- **Expanded Test Suite:** 22 new unit tests covering persistence round-tripping, backward compatibility, corrupted settings recovery, deterministic quota selection, and truthful states, bringing the automated test suite to **356 passing tests**.

---

## [0.1.0-preview.4] - 2026-09-10

### Initial Preview Release Candidate

#### Added
- **Unified Architecture:** Combined atomic credential switching (`codex-switcher`) and real-time quota telemetry (`codex-monitor`) into a high-performance, native Windows desktop application.
- **Fluent WinUI 3 User Interface:**
  - Modern desktop interface featuring Windows 11 Mica backdrop and dark/light system theme adaptation.
  - Interactive multi-account card list with health status indicators, drag-and-drop sort ordering, and one-click active slot activation.
  - Dynamic rate-limit visual indicators: progress bars tracking server-reported quota windows (including standard 5-hour and 7-day windows) with color-coded warning thresholds (Green $\ge 60\%$, Amber $20\% - 59\%$, Red $< 20\%$) and reset countdowns.
- **Account Activity Telemetry (Compatible Codex Runtimes):**
  - Integrated token activity metrics via Codex app-server `account/usage/read`.
  - Real-time display of server-reported lifetime tokens, peak daily tokens, longest turn duration, current streak, and longest streak days.
  - In-app capability inspection: gracefully detects runtime support and renders diagnostic status without breaking quota polling on older versions.
- **Multi-Account Sandbox Isolation:**
  - Headless `codex app-server --listen stdio://` child processes execute within isolated working directories (`CODEX_HOME`).
  - Zero-write invariant: active developer workspace `%USERPROFILE%\.codex\auth.json` is never touched or modified during background polling.
- **Safe Legacy Data Migration:**
  - Automatic detection of legacy `CodexSwitcher` installations (`%LOCALAPPDATA%\CodexSwitcher`).
  - 1-click migration service with 100% read-only touch of legacy files, preserving raw DPAPI ciphertext bytes and verifying destination safety before copy.
  - Decoupled active destination root (`CODEXSWITCHBOARD_HOME` or `%LOCALAPPDATA%\CodexSwitchboard`) from legacy migration sources.
- **Runtime Diagnostics & Settings:**
  - New Settings & About dialog displaying resolved Codex CLI path, version, and RPC capability status.
  - Dynamic runtime executable path override allowing users to target custom `codex.exe` binaries without modifying system `PATH` or restarting the app.
- **Export and Import Interoperability:**
  - Supports new `.codexswitchboard` export package format alongside backward-compatible `.codexswitcher` and `.json` imports.
- **Rate Limit Reset Credits & Quota Intelligence:**
  - Surfaces server-provided rate-limit reset credits indicating quick restoration opportunities.
  - Subscription plan detection (Plus, Team, Enterprise, Free) displayed with badges on account cards.
  - Daily token breakdown and interactive activity bar chart in Account Activity.
  - Per-account manual refresh and global "Refresh All" action.
- **Automated Verification:**
  - 334 passing automated offline tests covering DPAPI encryption, CAS concurrency, atomic rollback, capability caching, data root isolation, quota reset timestamps, and release sanitization.

#### Security & Privacy
- **DPAPI CurrentUser Vault:** Credentials encrypted at rest using machine- and user-context Windows DPAPI.
- **Zero Developer Telemetry:** Switchboard initiates zero outgoing network connections to developer analytics or tracking providers.
- **Sanitized Logging:** Audit trails strictly exclude access tokens, refresh tokens, and session secrets.
- **Single-Instance Enforcement:** Uses Windows App SDK `AppInstance` single-instance registration (`CodexSwitchboard.SingleInstance`) to redirect secondary activations.

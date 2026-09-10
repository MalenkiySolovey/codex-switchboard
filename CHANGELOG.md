# Changelog

All notable changes to **Codex Switchboard** will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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

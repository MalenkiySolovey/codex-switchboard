# Codex Switchboard

**English** | [Русский](./README_RU.md)

> Fast, native Windows desktop manager and real-time rate-limit quota dashboard for OpenAI Codex.  
> Switch accounts with one click and monitor usage quotas without re-authenticating.

![Platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011%20x64-0078D6?logo=windows)
![.NET](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet)
![UI](https://img.shields.io/badge/UI-WinUI%203%20Fluent-2E9BFF)
![Tests](https://img.shields.io/badge/tests-334%20passing-3DDC84)
![Architecture](https://img.shields.io/badge/architecture-x64-blue)
![Release](https://img.shields.io/badge/preview-v0.1.0--preview.1-orange)

---

## Overview

**Codex Switchboard** is a native Windows desktop application built with WinUI 3 and .NET 10 that unifies multi-account credential management and real-time rate-limit quota monitoring into a single, high-performance dashboard.

By default, the OpenAI Codex CLI stores authentication credentials in a single local file (`%USERPROFILE%\.codex\auth.json`). Developers maintaining separate personal, work, client, or team accounts are forced to repeatedly log in through the browser, interrupting active coding workflows. Furthermore, keeping track of remaining requests, dynamic quota reset countdowns, and reset credits across multiple accounts historically required manual CLI probes or ad-hoc scripts.

Codex Switchboard solves both challenges:
1. **Instant Atomic Account Switching:** Safely swaps credentials in `%USERPROFILE%\.codex\auth.json` in milliseconds with automatic session token write-back, pre-flight decryption checks, and transactional rollback.
2. **Real-Time Quota Telemetry:** Continuously tracks rate-limit windows (e.g. 5-hour and 7-day windows), usage percentages, remaining reset credits, and Account Activity across all saved accounts via isolated background sandboxes.

---

## Key Features

### Instant Atomic Account Switching
- **One-Click Switch:** Swap the active Codex credentials instantly from the account card list.
- **10-Step Reversible Transaction:** Executes a rigorous transactional sequence to guarantee credential safety:
  1. *Confirmation & pre-flight checks.*
  2. *Fail-fast decryption test* of target credentials before modifying any active files.
  3. *Process discovery & graceful shutdown* of running Codex processes to prevent file lock errors.
  4. *Anti-race verification* ensuring no concurrent mutations occurred.
  5. *Active slot token write-back* capturing tokens refreshed by the CLI during active sessions.
  6. *Encrypted timestamped backup* of the previous active slot.
  7. *Atomic file swap* via temporary replacement.
  8. *Configuration verification* (`cli_auth_credentials_store = "file"` in `config.toml`).
  9. *Metadata state update* in `profiles.json`.
  10. *Process relaunch* to resume developer workflow.
- **Automatic Rollback:** If any step fails during the swap, the previous credentials are restored immediately from backup.
- **Token Write-Back Protection:** Captures rotated session tokens so switching accounts never invalidates freshly renewed OAuth sessions.

### Live Rate-Limit Quota Monitoring
- **Dynamic Quota Windows:** Supports arbitrary server-reported rate-limit windows, including standard 5-hour and 7-day quota windows, as well as single-window and custom duration limits.
- **Color-Coded Status Thresholds:**
  - **Normal ($\ge 60\%$ remaining):** Green indicator representing ample quota headroom.
  - **Warning ($20\% - 59\%$ remaining):** Amber indicator warning of impending rate limits.
  - **Critical ($< 20\%$ remaining):** Red indicator signaling near-exhaustion.
- **Precision Reset Countdowns:** Displays human-readable remaining time countdowns (e.g., `2h 15m` or `3d 18h`) until window restoration.
- **Reset Credits Display:** Surfaces server-provided reset credits when available, indicating fast quota recovery opportunities.
- **Cache-First Startup:** Cached quota snapshots are rendered instantaneously upon launch and refreshed asynchronously in the background.
- **Granular Controls:** Supports individual account refresh as well as a global **Refresh All** action.

### Plan & Subscription Intelligence
- **Automatic Tier Detection:** Identifies account subscription tiers (Plus, Team, Enterprise, Free) directly from credential claims and app-server responses.
- **Visual Plan Badges:** Clearly displays plan type on each account card for quick context when choosing which account to activate.

### Account Activity Telemetry (Compatible Runtimes)
- **Activity & Streak Metrics:** Displays server-reported lifetime token consumption, peak daily usage, longest turn duration, current streak, and longest streak days.
- **Daily Usage Breakdown:** Visual representation of token usage distributions over recent days.
- **Runtime Capability Detection:** Probes the installed Codex app-server for RPC method support (`account/rateLimits/read`, `account/usage/read`). If running on an older runtime lacking activity methods, Switchboard displays clean diagnostic badges while keeping quota monitoring fully functional.

### Isolated Multi-Account Sandboxes
- **Zero-Write Invariant:** Background quota polling and telemetry queries never modify or lock `%USERPROFILE%\.codex\auth.json`.
- **Sandbox Isolation:** Spawns headless `codex app-server --listen stdio://` child processes within dedicated, isolated working directories (`CODEX_HOME`), completely decoupled from your primary coding environment.
- **Process Lifecycle Management:** Automatic timeout guards and graceful shutdown ensure no orphaned background processes remain.

### Safe Legacy Data Migration
- **Zero-Friction Upgrade:** Automatically detects existing profiles from legacy `%LOCALAPPDATA%\CodexSwitcher`.
- **100% Read-Only Touch:** Legacy files are never moved, modified, or deleted.
- **Ciphertext Byte Preservation:** Raw DPAPI encrypted blobs are copied byte-for-byte, eliminating unnecessary in-memory decryption cycles.
- **Destination Guard:** Prevents accidental overwrites by refusing migration if destination storage already contains configured accounts.

### Runtime Settings & Diagnostics
- **Custom Executable Override:** Target a custom `codex.exe` binary without altering system `PATH` or restarting the application.
- **Capability Inspector:** In-app diagnostics display detected executable path, runtime version, and supported RPC features.

---

## Runtime Compatibility Matrix

Codex Switchboard interfaces with the official OpenAI Codex CLI/App-Server via JSON-RPC 2.0 over `stdio`. Dynamic capability detection tests installed runtimes automatically.

| Codex CLI Version | Rate Limits (`account/rateLimits/read`) | Account Activity (`account/usage/read`) | Empirical Verification Status |
| :--- | :---: | :---: | :--- |
| **`0.130.0-alpha.5`** | ✅ Supported | ❌ Unsupported | Verified legacy runtime (Rate limits and switching fully operational; activity shows unsupported badge) |
| **`0.153.4`** | ✅ Supported | ✅ Supported | Verified modern runtime (Full feature set verified with live empirical rate limits and token telemetry) |

*Note: For any other installed version, Switchboard dynamically queries method availability at runtime.*

---

## Architecture Overview

Codex Switchboard follows a clean layered architecture with clear separation of concerns:

```
┌────────────────────────────────────────────────────────┐
│                   CodexSwitcher.App                    │
│      (WinUI 3 / XAML / MVVM / Windows App SDK 1.8)     │
│   • Views & Dialogs (Accounts, Settings, Login, Usage) │
│   • ViewModels & Observable State                      │
│   • WinUI Dispatcher & Theme Adapters                  │
└───────────────────────────┬────────────────────────────┘
                            │ depends on
┌───────────────────────────▼────────────────────────────┐
│                  CodexSwitcher.Core                    │
│             (Domain Logic & Abstractions)              │
│   • Profile & Switch Coordinator (10-step transaction) │
│   • Usage & Polling Coordinators                       │
│   • Capability Detection & Caching                     │
│   • Domain Models, DTOs, Formatters                    │
└───────────────────────────┬────────────────────────────┘
                            │ depends on
┌───────────────────────────▼────────────────────────────┐
│                  CodexSwitcher.Infra                   │
│        (Operating System & Process Integration)        │
│   • Windows DPAPI CurrentUser Vault Storage            │
│   • Headless Codex App-Server JSON-RPC Transport       │
│   • Profile Sandboxing & Ephemeral CODEX_HOME          │
│   • Process Lifecycle & Lock Management                │
└────────────────────────────────────────────────────────┘
```

- **`CodexSwitcher.App`:** WinUI 3 presentation layer utilizing Windows 11 Fluent Design, Mica backdrop, custom converters, and `CommunityToolkit.Mvvm`.
- **`CodexSwitcher.Core`:** Pure business logic and orchestration services, fully testable without UI or Windows SDK dependencies.
- **`CodexSwitcher.Infra`:** System integration layer handling process execution, DPAPI cryptography, JSON-RPC communication, and file system transactions.

---

## Storage & File Layout

All configuration, credentials, and cache files reside strictly on your local computer:

| Storage Path | Purpose | Environment Variable |
| :--- | :--- | :--- |
| `%LOCALAPPDATA%\CodexSwitchboard\` | Primary application data root (vault, profiles, cache, logs) | `CODEXSWITCHBOARD_HOME` |
| `%LOCALAPPDATA%\CodexSwitcher\` | Legacy switcher root (read-only migration source only) | `CODEXSWITCHER_HOME` |
| `%USERPROFILE%\.codex\` | Active Codex CLI directory (`auth.json`, `config.toml`) | `CODEX_HOME` |

### Internal Data Structure
```
%LOCALAPPDATA%\CodexSwitchboard├── profiles.json          # Account metadata (emails, nicknames, display order, plan types)
├── settings.json          # Preferences and custom executable override paths
├── usage-cache.json       # Cached rate limit and activity snapshots for instant UI render
├── audit.log              # Local log of switching transactions (no credentials logged)
├── vault\                 # DPAPI CurrentUser encrypted account credentials (<guid>.bin)
├── backups\               # Encrypted rotational backups of active slot before swaps
└── work\                  # Ephemeral isolated sandbox directories for quota polling
```

---

## Security & Privacy Model

- **Local DPAPI Vault:** Credentials (`auth.json`) are encrypted at rest using the Windows Data Protection API (`DPAPI`) with `DataProtectionScope.CurrentUser`. Keys are derived from the logged-in Windows session; no master passwords are ever stored or transmitted.
- **Zero Developer Telemetry:** Codex Switchboard contains **no telemetry libraries, no analytics trackers, no advertising SDKs, and no cloud server dependencies**.
- **Network Boundaries:**
  - **OAuth Additions:** The embedded WebView2 browser connects directly to OpenAI's official authentication servers (`auth.openai.com`). Session cookies are stored in a disposable sandbox folder.
  - **Quota Inquiries:** The locally spawned official `codex.exe` executable connects directly to OpenAI API endpoints to retrieve quota and telemetry data according to OpenAI's privacy policy.
  - **No Intermediaries:** No third-party servers ever touch your credentials or telemetry data.
- **Single-Instance Enforcement:** Uses Windows App SDK `AppInstance` registration (`CodexSwitchboard.SingleInstance`) to safely redirect activation arguments to the primary instance and avoid concurrent write races.
- **Least Privilege:** Does not require administrative privileges.

---

## System Requirements

- **Operating System:** Windows 10 (version 2004 / Build 19041 or newer) or Windows 11 (x64).
- **Codex CLI:** OpenAI Codex installed and available on `PATH` (or configured via Custom Executable Path in Settings).
- **WebView2 Evergreen Runtime:** Required only for browser-based OAuth account additions (pre-installed on Windows 11; if missing on Windows 10/Server, Switchboard displays a direct Microsoft download link while manual account imports remain available).
- **.NET 10 SDK:** Required only for building from source.

---

## Building from Source

### Prerequisites
Install the [.NET 10 SDK (x64)](https://dotnet.microsoft.com/download/dotnet/10.0).

### 1. Clone & Build
```powershell
# Clone the repository
git clone https://github.com/MalenkiySolovey/codex-switchboard.git
cd codex-switchboard

# Restore NuGet dependencies
dotnet restore CodexSwitcher.slnx

# Build solution in Release configuration
dotnet build CodexSwitcher.slnx -c Release

# Run the complete offline test suite (334 tests)
dotnet test CodexSwitcher.slnx -c Release --no-build
```

### 2. Package Local Release Candidate
Execute the packaging script to build, test, sanitize, and produce an unpackaged release archive:
```powershell
powershell -ExecutionPolicy Bypass -File scripts/build-release.ps1
```
Output artifact: `dist/CodexSwitchboard-0.1.0-preview.1-win-x64.zip` and its accompanying `dist/SHA256SUMS.txt`.

---

## Upstream Attribution & Acknowledgments

Codex Switchboard is built upon foundations established by open-source and community projects:

- **[codex-switcher](https://github.com/unkdevv/codex-switcher)** by [unkdevv](https://github.com/unkdevv) (MIT License):
  - Provided the foundational WinUI 3 application architecture, DPAPI vault storage model, 10-step atomic switching transaction, and OAuth session concepts.
- **[codex-monitor](https://github.com/NeMoSova19/codex-monitor.git)** by [NeMoSova19](https://github.com/NeMoSova19):
  - Provided foundational research into multi-account process sandboxing, stdio JSON-RPC 2.0 transport lifecycle, rate-limit quota parsing, and threshold visualization models.

---

## License & Legal Notice

- **License:** Distributed under the terms of the [MIT License](./LICENSE).
- **Trademarks:** "OpenAI", "ChatGPT", and "Codex" are registered trademarks of OpenAI, Inc. "Windows", "WinUI", and "Microsoft" are registered trademarks of Microsoft Corporation.
- **Disclaimer:** Codex Switchboard is an independent open-source project and is not affiliated with, endorsed by, or sponsored by OpenAI or Microsoft.

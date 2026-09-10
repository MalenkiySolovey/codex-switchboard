# Third-Party Notices and Attribution

Codex Switchboard incorporates code, architectural patterns, and dependencies from third-party open-source and proprietary projects. This document records all required notices, attributions, and licensing terms.

---

## 1. Upstream Architectural Projects

### codex-switcher

- **Upstream Repository:** `https://github.com/unkdevv/codex-switcher`
- **Original Author:** unkdevv
- **Original License:** MIT License
- **Contributions to Codex Switchboard:**
  - Base WinUI 3 / .NET 10 solution architecture.
  - Windows DPAPI `CurrentUser` local credential vault (`VaultService`, `DpapiSecretProtector`).
  - 10-step atomic switching transaction with encrypted backup and automatic rollback (`SwitchService`).
  - Active profile reconciliation engine (`ReconciliationService`).
  - Ephemeral WebView2 OAuth login session flow (`BrowserTabSession`).
  - RFC 6238 TOTP two-factor code generator (`TotpPanel`).

#### MIT License Text (codex-switcher)

```
MIT License

Copyright (c) 2026 unkdevv

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
### codex-monitor

- **Upstream Repository:** `https://github.com/NeMoSova19/codex-monitor.git`
- **Original Author:** NeMoSova19
- **Licensing & Redistribution Status:** Private / Unlicensed Upstream (`PRIVATE_UPSTREAM_PUBLICATION_RIGHTS = USER_CONFIRMATION_REQUIRED`)
- **Contributions to Codex Switchboard:**
  - Multi-account process isolation pattern (`ADR-0001: Separate App-Server Process per Account`).
  - Stdio JSON-RPC 2.0 app-server interaction lifecycle (`src/codex_monitor/rpc_transport.py`).
  - Quota read (`account/rateLimits/read`) and account activity telemetry parsing algorithms (`src/codex_monitor/parser.py`).
  - Remaining percentage color threshold mapping and countdown formatting (`src/codex_monitor/models.py`, `src/codex_monitor/web/dashboard.js`).

---

## 2. NuGet Dependencies

The following open-source NuGet packages are utilized by Codex Switchboard:

| Package | License | Author / Organization | URL |
| :--- | :--- | :--- | :--- |
| **Microsoft.WindowsAppSDK** | MIT | Microsoft Corporation | https://github.com/microsoft/WindowsAppSDK |
| **Microsoft.Windows.SDK.BuildTools** | Proprietary SDK License | Microsoft Corporation | https://www.nuget.org/packages/Microsoft.Windows.SDK.BuildTools |
| **Microsoft.Web.WebView2** | Microsoft Software License | Microsoft Corporation | https://developer.microsoft.com/en-us/microsoft-edge/webview2/ |
| **CommunityToolkit.Mvvm** | MIT | .NET Foundation and Contributors | https://github.com/CommunityToolkit/dotnet |
| **Tomlyn** | BSD-2-Clause | Alexandre Mutel | https://github.com/xoofx/Tomlyn |
| **xunit** | Apache-2.0 | .NET Foundation and Contributors | https://github.com/xunit/xunit |
| **xunit.runner.visualstudio** | Apache-2.0 | .NET Foundation and Contributors | https://github.com/xunit/visualstudio.xunit |
| **Microsoft.NET.Test.Sdk** | Microsoft Software License | Microsoft Corporation | https://github.com/microsoft/vstest |

---

## 3. Trademarks

- "OpenAI", "ChatGPT", and "Codex" are registered trademarks of OpenAI, Inc.
- "Windows", "WinUI", and "Microsoft" are registered trademarks of Microsoft Corporation.
- Codex Switchboard is an independent project and is not affiliated with, sponsored by, or endorsed by OpenAI or Microsoft.

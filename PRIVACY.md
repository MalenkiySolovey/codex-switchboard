# Privacy Policy

**Codex Switchboard** is designed with a strict privacy-first philosophy. Your credentials, usage data, and tokens are your own.

---

## 1. Zero Developer Telemetry & Zero Analytics

- **No Developer Telemetry:** Codex Switchboard does not bundle any developer telemetry SDKs, analytics packages, error trackers, or user instrumentation.
- **No Remote Switchboard Backend:** The Switchboard project does not operate external backend servers, cloud synchronization services, or remote account databases.
- **Local Diagnostic Logs:** Local audit logs (`audit.log`) and cached rate-limit snapshots remain strictly on your local storage.

---

## 2. Local Data Storage

All data managed by Codex Switchboard is stored exclusively on your local device under:
`%LOCALAPPDATA%\CodexSwitchboard\`

This directory contains:
- `profiles.json`: Local profile metadata (user-provided nicknames, account email addresses, and timestamps).
- `vault\`: Account credentials encrypted using Windows DPAPI `CurrentUser`.
- `backups\`: DPAPI-encrypted rotational backups created before account swaps.
- `usage-cache.json`: Local cache of rate limits and activity summaries for fast startup.
- `settings.json`: Local UI preferences and executable override paths.
- `audit.log`: Local timestamped log of switching operations (tokens are excluded).

---

## 3. External Network Interactions

Codex Switchboard does not provide its own cloud services. However, normal application functionality involves external network connections conducted by official components:

1. **Embedded WebView2 Browser (Account Addition):**
   - When you click "Add Account via OAuth", an embedded WebView2 browser opens OpenAI's official authentication page (`auth.openai.com`).
   - Network communication occurs directly between the WebView2 browser engine and OpenAI's authentication infrastructure.
2. **Official Codex CLI Runtime (Monitoring & Switching):**
   - Switchboard launches the locally installed official OpenAI Codex binary (`codex app-server --listen stdio://`) in headless mode.
   - Communication between Switchboard and the Codex binary occurs strictly over local inter-process pipes (stdin/stdout).
   - The official Codex binary independently establishes network connections to OpenAI API endpoints to validate credentials, refresh tokens, and fetch rate-limit quota metrics, in accordance with OpenAI's privacy policy and terms.

---

## 4. Data Control & Removal

You retain complete control over your local data:
- **Individual Account Deletion:** Removing an account in the Switchboard interface immediately deletes its metadata from `profiles.json` and purges its encrypted vault blob (`.bin`) from disk. (Note: Removing an account locally removes your local stored credentials, but does not modify or delete your account on OpenAI's servers).
- **Complete Local Purge:** To permanently remove all Switchboard data from your system, delete the `%LOCALAPPDATA%\CodexSwitchboard` directory.

---

## 5. Contact & Inquiries

For questions regarding privacy practices, open an issue on the project's GitHub repository.

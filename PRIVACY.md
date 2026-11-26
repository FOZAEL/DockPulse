# DockPulse Privacy Policy

DockPulse runs 100% locally on your Windows device. DockPulse itself does not make any network requests, and it does not collect, store, transmit, or share any personal, credential, or telemetry data. The one exception is optional: if you have the Antigravity CLI installed, DockPulse runs it to read your Antigravity limits, and that CLI contacts Google as it normally does (see below).

## Local Data Sources Read

- **Wireless Batteries:** Local Windows Bluetooth / BLE, GATT Battery Service, Apple Continuity BLE advertisements, Logitech HID++, and XInput controller state.
- **Network Usage:** Local Windows Data Usage (SRUM) statistics via `Windows.Networking.Connectivity`.
- **Prayer Times:** Computed offline using astronomical equations and locally saved region/method preferences (`prayer.cfg`).
- **Today Checklist & Pomodoro Timer:** Stored locally in your DockPulse settings folder (`today.json`, `pomodoro.json`, and `wireless.cfg` for the pinned Bluetooth device).
- **AI Usage Limits (DockPulse makes no network calls and reads no OAuth tokens or credential files):**
  - **Claude:** Reads `%USERPROFILE%\.claude\dockpulse-usage.json`. DockPulse never modifies `%USERPROFILE%\.claude\settings.json`; Claude usage data appears only after you manually configure Claude Code's `statusLine` script to write that cache file.
  - **Codex:** Reads the newest local `%USERPROFILE%\.codex\sessions\YYYY\MM\DD\rollout-*.jsonl` session logs for `rate_limits` events.
  - **Antigravity:** If the Antigravity CLI (`agy`) is installed, DockPulse runs `agy -p /usage --output-format json` at most every 5 minutes (and when you choose Refresh) and reads the printed limits. `agy` fetches your quota from Google with your existing `agy` sign-in, exactly as when you type `/usage` yourself; DockPulse never sees or stores your `agy` credentials. If `agy` is not installed, nothing is run.

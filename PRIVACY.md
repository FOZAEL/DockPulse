# DockPulse Privacy Policy

DockPulse runs 100% locally on your Windows device. It does not make any network requests, and it does not collect, store, transmit, or share any personal, credential, or telemetry data.

## Local Data Sources Read

- **Wireless Batteries:** Local Windows Bluetooth / BLE, GATT Battery Service, Apple Continuity BLE advertisements, Logitech HID++, and XInput controller state.
- **Network Usage:** Local Windows Data Usage (SRUM) statistics via `Windows.Networking.Connectivity`.
- **Prayer Times:** Computed offline using astronomical equations and locally saved region/method preferences (`prayer.cfg`).
- **Today Checklist & Pomodoro Timer:** Stored locally in your DockPulse settings folder (`today.json`, `pomodoro.json`, and `wireless.cfg` for the pinned Bluetooth device).
- **AI Usage Limits (strictly local, zero network calls, no OAuth tokens or credential files read):**
  - **Claude:** Reads `%USERPROFILE%\.claude\dockpulse-usage.json`. DockPulse never modifies `%USERPROFILE%\.claude\settings.json`; Claude usage data appears only after you manually configure Claude Code's `statusLine` script to write that cache file.
  - **Codex:** Reads the newest local `%USERPROFILE%\.codex\sessions\YYYY\MM\DD\rollout-*.jsonl` session logs for `rate_limits` events.
  - **Antigravity:** No local file is read (displays a reminder to check `/usage` in `agy`).

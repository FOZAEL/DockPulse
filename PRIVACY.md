# DockPulse Privacy Policy

DockPulse runs 100% locally on your Windows device. DockPulse itself does not make any network requests, and it does not collect, store, transmit, or share any personal, credential, or telemetry data. The one exception is optional: if you have the Claude Code or Antigravity CLI installed, DockPulse runs it to read your usage limits, and that CLI contacts Anthropic or Google as it normally does (see below).

## Local Data Sources Read

- **Wireless Batteries:** Local Windows Bluetooth / BLE, GATT Battery Service, Apple Continuity BLE advertisements, Logitech HID++, and XInput controller state.
- **Network Usage:** Local Windows Data Usage (SRUM) statistics via `Windows.Networking.Connectivity`.
- **Live Network Speed:** While the Network Usage page or Network Dashboard is open, DockPulse reads your network adapters' byte counters every 2 seconds (`System.Net.NetworkInformation`) to draw the last 60 seconds of download/upload speed. It stops when the page is closed, and the speeds are kept in memory only, never saved or sent anywhere.
- **Prayer Times:** Computed offline using astronomical equations and locally saved region/method preferences (`prayer.cfg`).
- **Today Checklist & Pomodoro Timer:** Stored locally in your DockPulse settings folder (`today.json`, `pomodoro.json`, and `wireless.cfg` for the pinned Bluetooth device).
- **Working Folder:** The folder path you choose, saved locally in `workfolder.cfg`. DockPulse only opens it in File Explorer, VS Code or a terminal when you click; it never reads the folder's contents.
- **AI Usage Limits (DockPulse makes no network calls and reads no OAuth tokens or credential files):**
  - **Claude:** If the Claude Code CLI (`claude`) is installed, DockPulse runs `claude -p /usage --output-format json --no-session-persistence` at most every 5 minutes (and when you choose Refresh) and reads the printed limits. `/usage` is a local command: it sends no prompt and saves no session; `claude` fetches your plan limits from Anthropic with your existing sign-in, and DockPulse never sees or stores your credentials. DockPulse also reads `%USERPROFILE%\.claude\dockpulse-usage.json` if you set up the optional status-line script, and uses whichever is newer. DockPulse never modifies `%USERPROFILE%\.claude\settings.json`.
  - **Codex:** Reads the newest local `%USERPROFILE%\.codex\sessions\YYYY\MM\DD\rollout-*.jsonl` session logs for `rate_limits` events.
  - **Antigravity:** If the Antigravity CLI (`agy`) is installed, DockPulse runs `agy -p /usage --output-format json` at most every 5 minutes (and when you choose Refresh) and reads the printed limits. `agy` fetches your quota from Google with your existing `agy` sign-in, exactly as when you type `/usage` yourself; DockPulse never sees or stores your `agy` credentials. If `agy` is not installed, nothing is run.
  - **Cursor:** Link only. DockPulse shows a button that opens your usage page on cursor.com in your browser; it reads no Cursor files, settings or sign-in data.
  - **Dock choice:** The provider you choose to show on the Dock (or "Tightest limit (auto)") is saved locally in `aiusage.cfg`.
  - **Logos:** The Claude, Antigravity, Codex and Cursor logos are image files bundled inside DockPulse, used only to identify each service; nothing is downloaded.

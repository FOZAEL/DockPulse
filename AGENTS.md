# Agent rules for DockPulse

## UI changes: update the HTML preview first

`ui-preview/index.html` is a no-build mock of DockPulse's UI: the three Dock bands and every Command Palette page, with fake data. The owner reviews UI changes there, in a browser, before any C# changes.

When a task changes anything the user sees, follow these steps. That includes a Dock band's title, subtitle or icon; a page's items, titles, subtitles, tags, icons, placeholder text, context menu (`MoreCommands`) or details pane; the look or text of charts in `NetworkChartRenderer.cs`; and any new band or page.

1. Update `ui-preview/index.html` first so it shows the new UI.
   - Keep texts, tags and icon glyphs identical to what the C# will produce (same `\uXXXX` codes as the `IconInfo` values).
   - Add or adjust sample data in the `MOCK DATA` section of the script.
   - If the change only shows in some states (low battery, no devices, no traffic, Shia method), add or update a Scenario for it.
2. Tell the owner to open `ui-preview/index.html` and wait for their approval before changing the C#, unless they asked for both in one go.
3. Implement the C# so it matches the approved preview.
4. If you change UI text in C# for any other reason (a fix or a refactor), update the preview in the same change so the two never drift.

Keep the preview a single self-contained HTML file: no build step, no external scripts, no network requests.

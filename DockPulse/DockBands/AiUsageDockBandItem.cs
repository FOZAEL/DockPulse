using System;
using System.Threading;
using DockPulse.Commands;
using DockPulse.Pages;
using DockPulse.Services;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace DockPulse.DockBands;

internal sealed partial class AiUsageDockBandItem : ListItem, IDisposable
{
    private readonly AiUsageService _service;
    private readonly AiDockSettings _dockSettings;
    private readonly AiUsagePage _page;
    private readonly AiDockPickerPage _pickerPage;
    private readonly Timer _timer;
    private readonly object _snapshotLock = new();
    private AiUsageSnapshot? _lastSnapshot;
    private int _isRefreshing;

    public AiUsageDockBandItem(
        AiUsageService service,
        AiDockSettings dockSettings,
        AiUsagePage page,
        AiDockPickerPage pickerPage)
        : base(page)
    {
        _service = service;
        _dockSettings = dockSettings;
        _page = page;
        _pickerPage = pickerPage;

        Title = "AI Usage";
        Subtitle = "No data";
        Icon = new IconInfo("\uE99A");

        MoreCommands =
        [
            new CommandContextItem(_pickerPage)
            {
                Title = "Choose Dock Provider...",
                Icon = new IconInfo("\uE718"),
            },
            new CommandContextItem(new ActionCommand("Refresh AI Usage", RefreshNow, new IconInfo("\uE72C"))),
            new CommandContextItem(new OpenUriCommand("Open Claude Usage", "https://claude.ai/settings/usage", new IconInfo("\uE774"))),
            new CommandContextItem(new OpenUriCommand("Open Codex Usage", "https://chatgpt.com/codex/settings/usage", new IconInfo("\uE774"))),
            new CommandContextItem(new OpenUriCommand("Open Cursor Usage", AiDockSettings.CursorUrl, new IconInfo("\uE774"))),
        ];

        // Antigravity/Claude CLIs run in the background; redraw as soon as a read finishes or Dock setting changes
        _service.Updated += Refresh;
        _dockSettings.Changed += OnDockSettingChanged;

        _timer = new Timer(_ => Refresh(), null, TimeSpan.Zero, TimeSpan.FromSeconds(60));
    }

    public void Refresh() => Refresh(forceRefresh: false);

    /// <summary>Manual refresh: also re-runs `claude -p /usage` and `agy -p /usage` instead of waiting for their 5-minute interval.</summary>
    public void RefreshNow() => Refresh(forceRefresh: true);

    private void OnDockSettingChanged()
    {
        AiUsageSnapshot? snapshot;
        lock (_snapshotLock)
        {
            snapshot = _lastSnapshot;
        }

        if (snapshot is not null)
        {
            ApplyDockDisplay(snapshot);
        }
        else
        {
            Refresh(forceRefresh: false);
        }
    }

    private void Refresh(bool forceRefresh)
    {
        if (Interlocked.Exchange(ref _isRefreshing, 1) == 1)
        {
            return;
        }

        try
        {
            var snapshot = _service.GetUsageSnapshot(forceRefresh);
            lock (_snapshotLock)
            {
                _lastSnapshot = snapshot;
            }

            _page.UpdateSnapshot(snapshot);
            _pickerPage.UpdateSnapshot(snapshot);
            ApplyDockDisplay(snapshot);
        }
        catch
        {
            Title = "AI Usage";
            Subtitle = "No data";
            Icon = new IconInfo("\uE99A");
        }
        finally
        {
            Interlocked.Exchange(ref _isRefreshing, 0);
        }
    }

    private void ApplyDockDisplay(AiUsageSnapshot snapshot)
    {
        var display = AiDockSettings.ResolveDockDisplay(snapshot, _dockSettings.DockMode, DateTimeOffset.UtcNow);
        Title = display.Title;
        Subtitle = display.Subtitle;
        Icon = display.Icon;
    }

    public void Dispose()
    {
        _service.Updated -= Refresh;
        _dockSettings.Changed -= OnDockSettingChanged;
        _timer.Dispose();
    }
}

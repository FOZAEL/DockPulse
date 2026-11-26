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
    private readonly AiUsagePage _page;
    private readonly Timer _timer;
    private int _isRefreshing;

    public AiUsageDockBandItem(AiUsageService service, AiUsagePage page)
        : base(page)
    {
        _service = service;
        _page = page;

        Title = "AI Usage";
        Subtitle = "No data";
        Icon = new IconInfo("\uE99A");

        MoreCommands =
        [
            new CommandContextItem(new ActionCommand("Refresh AI Usage", RefreshNow, new IconInfo("\uE72C"))),
            new CommandContextItem(new OpenUriCommand("Open Claude Usage", "https://claude.ai/settings/usage", new IconInfo("\uE774"))),
            new CommandContextItem(new OpenUriCommand("Open Codex Usage", "https://chatgpt.com/codex/settings/usage", new IconInfo("\uE774"))),
        ];

        // Antigravity is read in the background; redraw as soon as that read finishes
        _service.Updated += Refresh;

        _timer = new Timer(_ => Refresh(), null, TimeSpan.Zero, TimeSpan.FromSeconds(60));
    }

    public void Refresh() => Refresh(forceRefresh: false);

    /// <summary>Manual refresh: also re-runs `agy -p /usage` instead of waiting for its 5-minute interval.</summary>
    public void RefreshNow() => Refresh(forceRefresh: true);

    private void Refresh(bool forceRefresh)
    {
        if (Interlocked.Exchange(ref _isRefreshing, 1) == 1)
        {
            return;
        }

        try
        {
            var snapshot = _service.GetUsageSnapshot(forceRefresh);
            _page.UpdateSnapshot(snapshot);

            if (snapshot.Tightest is null)
            {
                Title = "AI Usage";
                Subtitle = "No data";
                Icon = new IconInfo("\uE99A");
            }
            else
            {
                var tight = snapshot.Tightest;
                Title = $"{tight.ProviderName} {tight.RemainingPercent}%";
                Subtitle = string.Equals(tight.ResetShort, "Reset", StringComparison.Ordinal)
                    ? $"{tight.WindowShort} • Reset"
                    : $"{tight.WindowShort} • resets {tight.ResetShort}";
                Icon = new IconInfo(tight.RemainingPercent == 0 ? "\uE7BA" : "\uE99A");
            }
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

    public void Dispose()
    {
        _service.Updated -= Refresh;
        _timer.Dispose();
    }
}

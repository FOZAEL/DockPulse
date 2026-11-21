using System;
using System.Threading;
using System.Threading.Tasks;
using DockPulse.Commands;
using DockPulse.Pages;
using DockPulse.Services;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace DockPulse.DockBands;

internal sealed partial class NetworkUsageDockBandItem : ListItem, IDisposable
{
    private readonly NetworkUsageService _service;
    private readonly NetworkUsagePage _page;
    private readonly NetworkUsageGraphPage _graphPage;
    private readonly Timer _timer;
    private int _isRefreshing;

    public NetworkUsageDockBandItem(
        NetworkUsageService service,
        NetworkUsagePage page,
        NetworkUsageGraphPage graphPage)
        : base(page)
    {
        _service = service;
        _page = page;
        _graphPage = graphPage;

        Title = "-- GB";
        Subtitle = "Mo --";
        Icon = new IconInfo("\uE774");

        MoreCommands =
        [
            new CommandContextItem(_graphPage) { Title = "View Usage Graphs" },
            new CommandContextItem(new ActionCommand("Refresh Network Usage", () => _ = RefreshAsync(), new IconInfo("\uE72C"))),
            new CommandContextItem(new OpenUriCommand("Data Usage Settings", "ms-settings:datausage", new IconInfo("\uE713"))),
        ];

        // Immediate poll, then every 2 minutes (Windows SRUM updates periodically)
        _timer = new Timer(_ => _ = RefreshAsync(), null, TimeSpan.Zero, TimeSpan.FromMinutes(2));
    }

    public async Task RefreshAsync()
    {
        if (Interlocked.Exchange(ref _isRefreshing, 1) == 1)
        {
            return;
        }

        try
        {
            var snapshot = await _service.GetUsageAsync().ConfigureAwait(false);
            _page.UpdateSnapshot(snapshot);
            await _graphPage.UpdateSnapshotAsync(snapshot).ConfigureAwait(false);

            Title = NetworkUsageService.FormatBytes(snapshot.TotalToday);
            Subtitle = $"Mo {NetworkUsageService.FormatBytes(snapshot.TotalMonth)}";
            Icon = new IconInfo("\uE774");
        }
        catch
        {
            Title = "N/A";
            Subtitle = string.Empty;
        }
        finally
        {
            Interlocked.Exchange(ref _isRefreshing, 0);
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
        _graphPage.Dispose();
    }
}

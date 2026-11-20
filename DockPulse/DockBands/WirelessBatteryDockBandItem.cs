using System;
using System.Threading;
using System.Threading.Tasks;
using DockPulse.Commands;
using DockPulse.Pages;
using DockPulse.Services;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace DockPulse.DockBands;

internal sealed partial class WirelessBatteryDockBandItem : ListItem, IDisposable
{
    private readonly WirelessBatteryService _service;
    private readonly WirelessDevicesPage _page;
    private readonly Timer _timer;
    private int _isRefreshing;

    public WirelessBatteryDockBandItem(WirelessBatteryService service, WirelessDevicesPage page)
        : base(page)
    {
        _service = service;
        _page = page;

        Title = "--%";
        Subtitle = "Wireless";
        Icon = new IconInfo("\uE702");

        MoreCommands =
        [
            new CommandContextItem(new ActionCommand("Refresh Batteries", () => _ = RefreshAsync(forceUncached: true), new IconInfo("\uE72C"))),
            new CommandContextItem(new OpenUriCommand("Bluetooth Settings", "ms-settings:bluetooth", new IconInfo("\uE713"))),
        ];

        _timer = new Timer(_ => _ = RefreshAsync(forceUncached: false), null, TimeSpan.Zero, TimeSpan.FromSeconds(60));
    }

    public async Task RefreshAsync(bool forceUncached)
    {
        if (Interlocked.Exchange(ref _isRefreshing, 1) == 1)
        {
            return;
        }

        try
        {
            var devices = await _service.GetDevicesAsync(forceUncached).ConfigureAwait(false);
            _page.UpdateDevices(devices);

            if (devices.Count == 0)
            {
                Title = "No BT";
                Subtitle = "Wireless";
                Icon = new IconInfo("\uE702");
            }
            else
            {
                var primary = devices[0];
                string extra = devices.Count > 1 ? $" (+{devices.Count - 1})" : string.Empty;

                Title = $"{primary.BatteryPercent}%{extra}";
                Subtitle = Truncate(primary.Name, 16);
                Icon = new IconInfo(WirelessBatteryService.GetBatteryGlyph(primary.BatteryPercent, primary.IsWiredOrCharging));
            }
        }
        catch
        {
            Title = "N/A";
            Subtitle = "Wireless";
        }
        finally
        {
            Interlocked.Exchange(ref _isRefreshing, 0);
        }
    }

    private static string Truncate(string value, int maxLen) =>
        value.Length <= maxLen ? value : $"{value[..(maxLen - 1)]}…";

    public void Dispose()
    {
        _timer.Dispose();
    }
}
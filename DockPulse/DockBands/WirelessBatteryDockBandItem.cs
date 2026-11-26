using System;
using System.Collections.Generic;
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
    private readonly object _stateLock = new();
    private IReadOnlyList<WirelessDeviceBattery> _lastDevices = [];
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

        _service.PinChanged += OnPinChanged;

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
            lock (_stateLock)
            {
                _lastDevices = devices;
            }

            _page.UpdateDevices(devices);
            ApplyDevices(devices);
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

    private void OnPinChanged()
    {
        IReadOnlyList<WirelessDeviceBattery> snapshot;
        lock (_stateLock)
        {
            snapshot = _lastDevices;
        }

        ApplyDevices(snapshot);
    }

    private void ApplyDevices(IReadOnlyList<WirelessDeviceBattery> devices)
    {
        var selection = _service.ResolveSelection(devices);
        if (selection.Active is null)
        {
            Title = "No BT";
            Subtitle = "Wireless";
            Icon = new IconInfo("\uE702");
            return;
        }

        var primary = selection.Active;
        Title = $"{primary.BatteryPercent}%";
        Subtitle = selection.PinnedMissing
            ? $"Fallback: {Truncate(primary.Name, 13)}"
            : Truncate(primary.Name, 18);
        Icon = new IconInfo(WirelessBatteryService.GetBatteryGlyph(primary.BatteryPercent, primary.IsWiredOrCharging));
    }

    private static string Truncate(string value, int maxLen) =>
        value.Length <= maxLen ? value : $"{value[..(maxLen - 1)]}…";

    public void Dispose()
    {
        _service.PinChanged -= OnPinChanged;
        _timer.Dispose();
        _service.Dispose();
    }
}
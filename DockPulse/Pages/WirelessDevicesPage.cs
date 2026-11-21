using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DockPulse.Commands;
using DockPulse.Services;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace DockPulse.Pages;

internal sealed partial class WirelessDevicesPage : ListPage
{
    private readonly WirelessBatteryService _service;
    private IReadOnlyList<WirelessDeviceBattery> _devices = [];

    public Action? OnManualRefresh { get; set; }

    public WirelessDevicesPage(WirelessBatteryService service)
    {
        _service = service;

        Id = "com.dockpulse.page.wireless";
        Name = "Wireless Devices";
        Title = "Wireless Device Batteries";
        Icon = new IconInfo("\uE702");
        PlaceholderText = "Filter wireless devices...";
    }

    public void UpdateDevices(IReadOnlyList<WirelessDeviceBattery> devices)
    {
        _devices = devices;
        RaiseItemsChanged(_devices.Count + 2);
    }

    public override IListItem[] GetItems()
    {
        var items = new List<IListItem>();

        if (_devices.Count == 0)
        {
            items.Add(new ListItem(new OpenUriCommand("Open Bluetooth Settings", "ms-settings:bluetooth", new IconInfo("\uE702")))
            {
                Title = "No wireless devices with battery reporting found",
                Subtitle = "Connect a Bluetooth/BLE device, Logitech LIGHTSPEED/Bolt/Unifying device, or Xbox controller",
                Icon = new IconInfo("\uE702"),
            });
        }
        else
        {
            foreach (var device in _devices)
            {
                var tags = new List<ITag>
                {
                    new Tag($"{device.BatteryPercent}%"),
                };

                if (device.IsWiredOrCharging)
                {
                    tags.Add(new Tag("Wired / Charging"));
                }
                else if (device.BatteryPercent <= 20)
                {
                    tags.Add(new Tag("Low Battery"));
                }

                string subtitle = !string.IsNullOrWhiteSpace(device.DetailText)
                    ? $"{device.ConnectionType} • {device.DetailText}"
                    : $"{device.ConnectionType} • Battery: {device.BatteryPercent}%";

                items.Add(new ListItem(new OpenUriCommand("Open Bluetooth Settings", "ms-settings:bluetooth", new IconInfo("\uE702")))
                {
                    Title = device.Name,
                    Subtitle = subtitle,
                    Icon = new IconInfo(WirelessBatteryService.GetDeviceGlyph(device.Kind)),
                    Tags = [.. tags],
                    MoreCommands =
                    [
                        new CommandContextItem(new ActionCommand("Refresh Battery Levels", TriggerRefresh, new IconInfo("\uE72C"))),
                    ],
                });
            }
        }

        items.Add(new ListItem(new ActionCommand("Refresh Now", TriggerRefresh, new IconInfo("\uE72C")))
        {
            Title = "Refresh Wireless Battery Levels",
            Subtitle = "Force an immediate Bluetooth, GATT, Logitech HID++, and XInput scan",
            Icon = new IconInfo("\uE72C"),
        });

        items.Add(new ListItem(new OpenUriCommand("Open Bluetooth Settings", "ms-settings:bluetooth", new IconInfo("\uE713")))
        {
            Title = "Open Windows Bluetooth & Devices Settings",
            Subtitle = "Manage paired devices in Windows Settings",
            Icon = new IconInfo("\uE713"),
        });

        return [.. items];
    }

    private void TriggerRefresh()
    {
        if (OnManualRefresh is not null)
        {
            OnManualRefresh();
            return;
        }

        _ = RefreshAsync();
    }

    public async Task RefreshAsync(bool forceUncached = true)
    {
        IsLoading = true;
        try
        {
            var latest = await _service.GetDevicesAsync(forceUncached).ConfigureAwait(false);
            UpdateDevices(latest);
        }
        finally
        {
            IsLoading = false;
        }
    }
}
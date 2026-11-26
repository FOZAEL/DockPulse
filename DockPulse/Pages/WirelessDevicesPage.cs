using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DockPulse.Commands;
using DockPulse.Helpers;
using DockPulse.Services;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace DockPulse.Pages;

internal sealed partial class WirelessDevicesPage : ListPage
{
    private readonly WirelessBatteryService _service;
    private readonly object _devicesLock = new();
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

        _service.PinChanged += OnPinChanged;
    }

    private void OnPinChanged()
    {
        int count;
        lock (_devicesLock)
        {
            count = _devices.Count;
        }

        RaiseItemsChanged(count + 3);
    }

    public void UpdateDevices(IReadOnlyList<WirelessDeviceBattery> devices)
    {
        lock (_devicesLock)
        {
            _devices = devices;
        }

        RaiseItemsChanged(devices.Count + 3);
    }

    public override IListItem[] GetItems()
    {
        IReadOnlyList<WirelessDeviceBattery> devices;
        lock (_devicesLock)
        {
            devices = _devices;
        }

        var selection = _service.ResolveSelection(devices);
        var items = new List<IListItem>();

        if (devices.Count == 0)
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
            if (selection.PinnedMissing && selection.Active is not null)
            {
                var fallback = selection.Active;
                string pinnedLabel = selection.PinnedName ?? selection.PinnedId ?? "Pinned device";

                items.Add(new ListItem(new ActionCommand(
                    $"Pin {fallback.Name} Instead",
                    () =>
                    {
                        _service.PinDevice(fallback.Id, fallback.Name);
                        DockPulseHelpers.ShowInlineToast($"Dock now shows {fallback.Name}");
                    },
                    new IconInfo("\uE718")))
                {
                    Section = "Dock Selection",
                    Icon = new IconInfo("\uE7BA"),
                    Title = $"{pinnedLabel} (Disconnected)",
                    Subtitle = $"Pinned for Dock • Falling back to {fallback.Name}",
                    Tags =
                    [
                        DockPulseHelpers.CreateTag("Pinned", TagTone.Warn),
                        DockPulseHelpers.CreateTag("Offline", TagTone.Danger),
                    ],
                    MoreCommands =
                    [
                        new CommandContextItem(new ActionCommand("Refresh Battery Levels", TriggerRefresh, new IconInfo("\uE72C"))),
                        new CommandContextItem(new OpenUriCommand("Open Bluetooth Settings", "ms-settings:bluetooth", new IconInfo("\uE713"))),
                    ],
                });
            }

            foreach (var device in devices)
            {
                bool isPinned = string.Equals(device.Id, selection.PinnedId, StringComparison.OrdinalIgnoreCase);
                bool isFallback = selection.PinnedMissing &&
                                  selection.Active is not null &&
                                  string.Equals(device.Id, selection.Active.Id, StringComparison.OrdinalIgnoreCase);

                var tags = new List<ITag>
                {
                    DockPulseHelpers.CreateTag($"{device.BatteryPercent}%"),
                };

                if (isPinned && !selection.PinnedMissing)
                {
                    tags.Add(DockPulseHelpers.CreateTag("Shown in Dock", TagTone.Accent));
                }
                else if (isFallback)
                {
                    tags.Add(DockPulseHelpers.CreateTag("Fallback in Dock", TagTone.Warn));
                }

                if (device.IsWiredOrCharging)
                {
                    tags.Add(DockPulseHelpers.CreateTag("Wired / Charging", TagTone.Ok));
                }
                else if (device.BatteryPercent <= 20)
                {
                    tags.Add(DockPulseHelpers.CreateTag("Low Battery", TagTone.Danger));
                }

                string subtitle = !string.IsNullOrWhiteSpace(device.DetailText)
                    ? $"{device.ConnectionType} • {device.DetailText}"
                    : $"{device.ConnectionType} • Battery: {device.BatteryPercent}%";

                string deviceId = device.Id;
                string deviceName = device.Name;
                bool alreadyPinned = isPinned;

                items.Add(new ListItem(new ActionCommand(
                    alreadyPinned ? "Shown in Dock" : "Show in Dock",
                    () =>
                    {
                        _service.PinDevice(deviceId, deviceName);
                        DockPulseHelpers.ShowInlineToast(alreadyPinned
                            ? $"{deviceName} is already shown in Dock"
                            : $"Dock now shows {deviceName}");
                    },
                    new IconInfo("\uE718")))
                {
                    Section = selection.PinnedMissing ? "Connected Devices" : string.Empty,
                    Title = device.Name,
                    Subtitle = subtitle,
                    Icon = new IconInfo(WirelessBatteryService.GetDeviceGlyph(device.Kind)),
                    Tags = [.. tags],
                    MoreCommands =
                    [
                        new CommandContextItem(new OpenUriCommand("Open Bluetooth Settings", "ms-settings:bluetooth", new IconInfo("\uE713"))),
                        new CommandContextItem(new ActionCommand("Refresh Battery Levels", TriggerRefresh, new IconInfo("\uE72C"))),
                    ],
                });
            }
        }

        items.Add(new ListItem(new ActionCommand("Refresh Now", TriggerRefresh, new IconInfo("\uE72C")))
        {
            Section = selection.PinnedMissing ? "Actions" : string.Empty,
            Title = "Refresh Wireless Battery Levels",
            Subtitle = "Force an immediate Bluetooth, GATT, Logitech HID++, and XInput scan",
            Icon = new IconInfo("\uE72C"),
        });

        items.Add(new ListItem(new OpenUriCommand("Open Bluetooth Settings", "ms-settings:bluetooth", new IconInfo("\uE713")))
        {
            Section = selection.PinnedMissing ? "Actions" : string.Empty,
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
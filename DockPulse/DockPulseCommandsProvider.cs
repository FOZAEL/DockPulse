using DockPulse.DockBands;
using DockPulse.Pages;
using DockPulse.Services;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace DockPulse;

public partial class DockPulseCommandsProvider : CommandProvider
{
    private readonly WirelessBatteryService _wirelessService;
    private readonly NetworkUsageService _networkService;

    private readonly WirelessDevicesPage _wirelessPage;
    private readonly NetworkUsageGraphPage _networkGraphPage;
    private readonly NetworkUsagePage _networkPage;

    private readonly WirelessBatteryDockBandItem _wirelessDockItem;
    private readonly NetworkUsageDockBandItem _networkDockItem;

    private readonly ICommandItem[] _topLevelCommands;
    private readonly ICommandItem[] _dockBands;

    public DockPulseCommandsProvider()
    {
        Id = "com.dockpulse.extension";
        DisplayName = "DockPulse";
        Icon = new IconInfo("\uE774");

        _wirelessService = new WirelessBatteryService();
        _networkService = new NetworkUsageService();

        _wirelessPage = new WirelessDevicesPage(_wirelessService);
        _networkGraphPage = new NetworkUsageGraphPage(_networkService);
        _networkPage = new NetworkUsagePage(_networkService, _networkGraphPage);

        _wirelessDockItem = new WirelessBatteryDockBandItem(_wirelessService, _wirelessPage);
        _networkDockItem = new NetworkUsageDockBandItem(_networkService, _networkPage, _networkGraphPage);

        _wirelessPage.OnManualRefresh = () => _ = _wirelessDockItem.RefreshAsync(forceUncached: true);
        _networkPage.OnManualRefresh = () => _ = _networkDockItem.RefreshAsync();
        _networkGraphPage.OnManualRefresh = () => _ = _networkDockItem.RefreshAsync();

        _topLevelCommands =
        [
            new CommandItem(_wirelessPage)
            {
                Title = "Wireless Device Batteries",
                Subtitle = "View battery levels of Bluetooth, BLE, Logitech Wireless (LIGHTSPEED/Bolt/Unifying), and Xbox devices",
                Icon = new IconInfo("\uE702"),
            },
            new CommandItem(_networkPage)
            {
                Title = "Network Usage (Today & This Month)",
                Subtitle = "View network data consumption and usage graphs for today (since midnight) and this month",
                Icon = new IconInfo("\uE774"),
            },
        ];

        _dockBands =
        [
            new WrappedDockItem(
                [_wirelessDockItem],
                "com.dockpulse.band.wireless",
                "Wireless Battery"),
            new WrappedDockItem(
                [_networkDockItem],
                "com.dockpulse.band.network",
                "Network Usage"),
        ];
    }

    public override ICommandItem[] TopLevelCommands() => _topLevelCommands;

    public override ICommandItem[]? GetDockBands() => _dockBands;

    public override ICommandItem? GetCommandItem(string id)
    {
        foreach (var item in _dockBands)
        {
            if (item.Command is ICommand cmd && cmd.Id == id)
            {
                return item;
            }
        }

        foreach (var item in _topLevelCommands)
        {
            if (item.Command is ICommand cmd && cmd.Id == id)
            {
                return item;
            }
        }

        if (_networkGraphPage.Id == id)
        {
            return new CommandItem(_networkGraphPage)
            {
                Title = "Network Usage Graphs",
                Subtitle = "Today (Hourly) & This Month (Daily) visual usage charts",
                Icon = new IconInfo("\uE9D2"),
            };
        }

        return null;
    }
}

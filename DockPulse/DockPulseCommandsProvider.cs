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
    private readonly PrayerTimesService _prayerService;

    private readonly WirelessDevicesPage _wirelessPage;
    private readonly NetworkUsageGraphPage _networkGraphPage;
    private readonly NetworkUsagePage _networkPage;
    private readonly PrayerRegionPickerPage _prayerRegionPage;
    private readonly PrayerMethodPickerPage _prayerMethodPage;
    private readonly PrayerTimesPage _prayerPage;

    private readonly WirelessBatteryDockBandItem _wirelessDockItem;
    private readonly NetworkUsageDockBandItem _networkDockItem;
    private readonly PrayerTimesDockBandItem _prayerDockItem;

    private readonly ICommandItem[] _topLevelCommands;
    private readonly ICommandItem[] _dockBands;

    public DockPulseCommandsProvider()
    {
        Id = "com.dockpulse.extension";
        DisplayName = "DockPulse";
        Icon = IconHelpers.FromRelativePath("Assets\\StoreLogo.png");

        _wirelessService = new WirelessBatteryService();
        _networkService = new NetworkUsageService();
        _prayerService = new PrayerTimesService();

        _wirelessPage = new WirelessDevicesPage(_wirelessService);
        _networkGraphPage = new NetworkUsageGraphPage(_networkService);
        _networkPage = new NetworkUsagePage(_networkService, _networkGraphPage);
        _networkGraphPage.AttachAdapterListPage(_networkPage);

        _prayerRegionPage = new PrayerRegionPickerPage(_prayerService);
        _prayerMethodPage = new PrayerMethodPickerPage(_prayerService);
        _prayerPage = new PrayerTimesPage(_prayerService, _prayerRegionPage, _prayerMethodPage);

        _wirelessDockItem = new WirelessBatteryDockBandItem(_wirelessService, _wirelessPage);
        _networkDockItem = new NetworkUsageDockBandItem(_networkService, _networkPage, _networkGraphPage);
        _prayerDockItem = new PrayerTimesDockBandItem(_prayerService, _prayerPage, _prayerRegionPage, _prayerMethodPage);

        _wirelessPage.OnManualRefresh = () => _ = _wirelessDockItem.RefreshAsync(forceUncached: true);
        _networkPage.OnManualRefresh = () => _ = _networkDockItem.RefreshAsync();
        _networkGraphPage.OnManualRefresh = () => _ = _networkDockItem.RefreshAsync();
        _prayerPage.OnManualRefresh = () => _prayerDockItem.Refresh();

        _topLevelCommands =
        [
            new CommandItem(_wirelessPage)
            {
                Title = "Wireless Device Batteries",
                Subtitle = "View battery levels of Bluetooth, BLE, AirPods, Logitech LIGHTSPEED/Bolt/Unifying, and Xbox devices",
                Icon = new IconInfo("\uE702"),
            },
            new CommandItem(_networkGraphPage)
            {
                Title = "Network Usage (Today & This Month)",
                Subtitle = "View hourly & monthly network data graphs and per-adapter usage breakdown",
                Icon = new IconInfo("\uE774"),
            },
            new CommandItem(_prayerPage)
            {
                Title = "Prayer Times (Sunni & Shia)",
                Subtitle = "Countdown to next prayer time with 60+ selectable regions, Sunni & Shia methods, and Asr schools",
                Icon = new IconInfo("\uE708"),
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
            new WrappedDockItem(
                [_prayerDockItem],
                "com.dockpulse.band.prayer",
                "Next Prayer Time"),
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

        if (_networkPage.Id == id)
        {
            return new CommandItem(_networkPage)
            {
                Title = "Network Adapter Details",
                Subtitle = "Filter per-adapter network usage and daily graphs",
                Icon = new IconInfo("\uE839"),
            };
        }

        if (_prayerRegionPage.Id == id)
        {
            return new CommandItem(_prayerRegionPage)
            {
                Title = "Select Prayer Region / City",
                Subtitle = "Choose city for Sunni & Shia prayer times",
                Icon = new IconInfo("\uE707"),
            };
        }

        return null;
    }
}

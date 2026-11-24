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
        _networkPage = new NetworkUsagePage(_networkService);

        _prayerRegionPage = new PrayerRegionPickerPage(_prayerService);
        _prayerMethodPage = new PrayerMethodPickerPage(_prayerService);
        _prayerPage = new PrayerTimesPage(_prayerService, _prayerRegionPage, _prayerMethodPage);

        _wirelessDockItem = new WirelessBatteryDockBandItem(_wirelessService, _wirelessPage);
        _networkDockItem = new NetworkUsageDockBandItem(_networkService, _networkPage);
        _prayerDockItem = new PrayerTimesDockBandItem(_prayerService, _prayerPage, _prayerRegionPage, _prayerMethodPage);

        _wirelessPage.OnManualRefresh = () => _ = _wirelessDockItem.RefreshAsync(forceUncached: true);
        _networkPage.OnManualRefresh = () => _ = _networkDockItem.RefreshAsync();
        _prayerPage.OnManualRefresh = () => _prayerDockItem.Refresh();

        _topLevelCommands =
        [
            new CommandItem(_wirelessPage)
            {
                Title = "Wireless Device Batteries",
                Subtitle = "View battery levels of Bluetooth, BLE, AirPods, Logitech LIGHTSPEED/Bolt/Unifying, and Xbox devices",
                Icon = new IconInfo("\uE702"),
            },
            new CommandItem(_networkPage)
            {
                Title = "Network Usage (Today & This Month)",
                Subtitle = "View network data consumption and usage graphs for today (since midnight) and this month",
                Icon = new IconInfo("\uE774"),
            },
            new CommandItem(_prayerPage)
            {
                Title = "Prayer Times",
                Subtitle = "Countdown to next prayer time with selectable regions, calculation methods, and notifications",
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

        if (_prayerRegionPage.Id == id)
        {
            return new CommandItem(_prayerRegionPage)
            {
                Title = "Select Region & Timezone",
                Subtitle = "Choose city and timezone for prayer times",
                Icon = new IconInfo("\uE707"),
            };
        }

        if (_prayerMethodPage.Id == id)
        {
            return new CommandItem(_prayerMethodPage)
            {
                Title = "Prayer Calculation Method",
                Subtitle = "Choose prayer calculation convention and Asr school",
                Icon = new IconInfo("\uE909"),
            };
        }

        return null;
    }
}

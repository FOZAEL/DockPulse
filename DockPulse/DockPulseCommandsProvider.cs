using DockPulse.Commands;
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
    private readonly LocationService _locationService;
    private readonly CalendarService _calendarService;
    private readonly FootballService _footballService;
    private readonly PrayerTimesService _prayerService;
    private readonly AiUsageService _aiUsageService;
    private readonly AiDockSettings _aiDockSettings;
    private readonly TodayService _todayService;
    private readonly PomodoroService _pomodoroService;

    private readonly WirelessDevicesPage _wirelessPage;
    private readonly NetworkDashboardPage _networkDashboardPage;
    private readonly NetworkUsagePage _networkPage;
    private readonly LocationPage _locationPage;
    private readonly CalendarSettingsPage _calendarSettingsPage;
    private readonly CalendarPage _calendarPage;
    private readonly FootballAddTeamPage _footballAddPage;
    private readonly FootballPage _footballPage;
    private readonly PrayerRegionPickerPage _prayerRegionPage;
    private readonly PrayerMethodPickerPage _prayerMethodPage;
    private readonly PrayerTimesPage _prayerPage;
    private readonly AiDashboardPage _aiDashboardPage;
    private readonly AiDockPickerPage _aiDockPickerPage;
    private readonly AiUsagePage _aiUsagePage;
    private readonly TodayAddItemPage _todayAddPage;
    private readonly TodaySettingsPage _todaySettingsPage;
    private readonly TodayPage _todayPage;
    private readonly PomodoroSettingsPage _pomodoroSettingsPage;
    private readonly PomodoroStatsPage _pomodoroStatsPage;
    private readonly PomodoroPage _pomodoroPage;

    private readonly WirelessBatteryDockBandItem _wirelessDockItem;
    private readonly NetworkUsageDockBandItem _networkDockItem;
    private readonly LocationDockBandItem _locationDockItem;
    private readonly CalendarDockBandItem _calendarDockItem;
    private readonly FootballDockBandItem _footballDockItem;
    private readonly PrayerTimesDockBandItem _prayerDockItem;
    private readonly AiUsageDockBandItem _aiUsageDockItem;
    private readonly TodayDockBandItem _todayDockItem;
    private readonly PomodoroDockBandItem _pomodoroDockItem;
    private readonly PomodoroToggleDockItem _pomodoroToggleDockItem;

    private readonly WorkingFolderService _workFolderService;
    private readonly WorkingFolderSettingsPage _workFolderSettingsPage;
    private readonly OpenWorkingFolderCommand _openWorkFolderCommand;
    private readonly WorkingFolderDockBandItem _workFolderDockItem;

    private readonly ICommandItem[] _topLevelCommands;
    private readonly ICommandItem[] _dockBands;

    public DockPulseCommandsProvider()
    {
        Id = "com.dockpulse.extension";
        DisplayName = "DockPulse";
        Icon = IconHelpers.FromRelativePath("Assets\\StoreLogo.png");

        _wirelessService = new WirelessBatteryService();
        _networkService = new NetworkUsageService();
        _locationService = new LocationService();
        _calendarService = new CalendarService();
        _footballService = new FootballService();
        _prayerService = new PrayerTimesService();
        _aiUsageService = new AiUsageService();
        _aiDockSettings = new AiDockSettings();
        _todayService = new TodayService();
        _pomodoroService = new PomodoroService();

        _wirelessPage = new WirelessDevicesPage(_wirelessService);
        _networkDashboardPage = new NetworkDashboardPage();
        _networkPage = new NetworkUsagePage(_networkService, _networkDashboardPage);
        _locationPage = new LocationPage(_locationService);
        _calendarSettingsPage = new CalendarSettingsPage(_calendarService);
        _calendarPage = new CalendarPage(_calendarService, _calendarSettingsPage);
        _footballAddPage = new FootballAddTeamPage(_footballService);
        _footballPage = new FootballPage(_footballService, _footballAddPage);

        _prayerRegionPage = new PrayerRegionPickerPage(_prayerService);
        _prayerMethodPage = new PrayerMethodPickerPage(_prayerService);
        _prayerPage = new PrayerTimesPage(_prayerService, _prayerRegionPage, _prayerMethodPage);

        _aiDashboardPage = new AiDashboardPage(_aiUsageService, _aiDockSettings);
        _aiDockPickerPage = new AiDockPickerPage(_aiUsageService, _aiDockSettings);
        _aiUsagePage = new AiUsagePage(_aiUsageService, _aiDockSettings, _aiDashboardPage, _aiDockPickerPage);

        _todayAddPage = new TodayAddItemPage(_todayService);
        _todaySettingsPage = new TodaySettingsPage(_todayService);
        _todayPage = new TodayPage(_todayService, _todayAddPage, _todaySettingsPage);

        _pomodoroSettingsPage = new PomodoroSettingsPage(_pomodoroService);
        _pomodoroStatsPage = new PomodoroStatsPage(_pomodoroService, _pomodoroSettingsPage);
        _pomodoroPage = new PomodoroPage(_pomodoroService, _pomodoroStatsPage, _pomodoroSettingsPage);

        _wirelessDockItem = new WirelessBatteryDockBandItem(_wirelessService, _wirelessPage);
        _networkDockItem = new NetworkUsageDockBandItem(_networkService, _networkPage);
        _locationDockItem = new LocationDockBandItem(_locationService, _locationPage);
        _calendarDockItem = new CalendarDockBandItem(_calendarService, _calendarPage, _calendarSettingsPage);
        _footballDockItem = new FootballDockBandItem(_footballService, _footballPage, _footballAddPage);
        _prayerDockItem = new PrayerTimesDockBandItem(_prayerService, _prayerPage, _prayerRegionPage, _prayerMethodPage);
        _aiUsageDockItem = new AiUsageDockBandItem(_aiUsageService, _aiDockSettings, _aiUsagePage, _aiDockPickerPage);
        _todayDockItem = new TodayDockBandItem(_todayService, _todayPage, _todayAddPage, _todaySettingsPage);
        _pomodoroDockItem = new PomodoroDockBandItem(_pomodoroService, _pomodoroPage, _pomodoroStatsPage, _pomodoroSettingsPage);
        _pomodoroToggleDockItem = new PomodoroToggleDockItem(_pomodoroService, _pomodoroDockItem);

        _workFolderService = new WorkingFolderService();
        _workFolderSettingsPage = new WorkingFolderSettingsPage(_workFolderService);
        _openWorkFolderCommand = new OpenWorkingFolderCommand(_workFolderService);
        _workFolderDockItem = new WorkingFolderDockBandItem(_workFolderService, _workFolderSettingsPage, _openWorkFolderCommand);

        _wirelessPage.OnManualRefresh = () => _ = _wirelessDockItem.RefreshAsync(forceUncached: true);
        _networkPage.OnManualRefresh = () => _ = _networkDockItem.RefreshAsync();
        _networkDashboardPage.OnManualRefresh = () => _ = _networkDockItem.RefreshAsync();
        _prayerPage.OnManualRefresh = () => _prayerDockItem.Refresh();
        _aiUsagePage.OnManualRefresh = () => _aiUsageDockItem.RefreshNow();
        _aiDashboardPage.OnManualRefresh = () => _aiUsageDockItem.RefreshNow();

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
            new CommandItem(_locationPage)
            {
                Title = "My Location (Public IP)",
                Subtitle = "City, country and network provider of your current public IP, including when you're on a VPN",
                Icon = new IconInfo("\uE81D"),
            },
            new CommandItem(_footballPage)
            {
                Title = "Football",
                Subtitle = "Live scores and next matches for your teams: European leagues, MLS and Saudi Pro League",
                Icon = new IconInfo("\uE734"),
            },
            new CommandItem(_calendarPage)
            {
                Title = "Calendar",
                Subtitle = "Today's date, week number and a month calendar",
                Icon = new IconInfo("\uE787"),
            },
            new CommandItem(_prayerPage)
            {
                Title = "Prayer Times",
                Subtitle = "Countdown to next prayer time with selectable regions, calculation methods, and notifications",
                Icon = new IconInfo("\uE708"),
            },
            new CommandItem(_aiUsagePage)
            {
                Title = "AI Usage Limits",
                Subtitle = "5-hour session and weekly remaining limits for Claude, Antigravity, and Codex",
                Icon = new IconInfo("\uE99A"),
            },
            new CommandItem(_todayPage)
            {
                Title = "Today — Daily Checklist",
                Subtitle = "Daily habits, water counter, coffee reminder, and midnight-resetting tasks",
                Icon = new IconInfo("\uE9D5"),
            },
            new CommandItem(_pomodoroPage)
            {
                Title = "Pomodoro Timer",
                Subtitle = "Focus timer ring, breaks, session stats, and quick Dock controls",
                Icon = new IconInfo("\uE916"),
            },
            new CommandItem(_openWorkFolderCommand)
            {
                Title = "Open Working Folder",
                Subtitle = "Open your working folder in File Explorer with one click",
                Icon = new IconInfo("\uE8B7"),
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
                [_locationDockItem],
                "com.dockpulse.band.location",
                "My Location"),
            new WrappedDockItem(
                [_calendarDockItem],
                "com.dockpulse.band.calendar",
                "Calendar"),
            new WrappedDockItem(
                [_footballDockItem],
                "com.dockpulse.band.football",
                "Football"),
            new WrappedDockItem(
                [_prayerDockItem],
                "com.dockpulse.band.prayer",
                "Next Prayer Time"),
            new WrappedDockItem(
                [_aiUsageDockItem],
                "com.dockpulse.band.aiusage",
                "AI Usage Limits"),
            new WrappedDockItem(
                [_todayDockItem],
                "com.dockpulse.band.today",
                "Today — Daily Checklist"),
            new WrappedDockItem(
                [_pomodoroDockItem, _pomodoroToggleDockItem],
                "com.dockpulse.band.pomodoro",
                "Pomodoro"),
            new WrappedDockItem(
                [_workFolderDockItem],
                "com.dockpulse.band.workfolder",
                "Working Folder"),
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

        if (_networkDashboardPage.Id == id)
        {
            return new CommandItem(_networkDashboardPage)
            {
                Title = "Network Dashboard",
                Subtitle = "Today's hourly usage and this month's daily usage",
                Icon = new IconInfo("\uE774"),
            };
        }

        if (_aiDashboardPage.Id == id)
        {
            return new CommandItem(_aiDashboardPage)
            {
                Title = "AI Usage Dashboard",
                Subtitle = "Compare remaining limits for Claude, Antigravity, and Codex side by side",
                Icon = new IconInfo("\uE9E9"),
            };
        }

        if (_aiDockPickerPage.Id == id)
        {
            return new CommandItem(_aiDockPickerPage)
            {
                Title = "Choose What the AI Usage Dock Shows",
                Subtitle = "Pin a specific AI provider or show the tightest limit automatically",
                Icon = new IconInfo("\uE718"),
            };
        }

        if (_footballAddPage.Id == id)
        {
            return new CommandItem(_footballAddPage)
            {
                Title = "Add Football Team",
                Subtitle = "European leagues, MLS and Saudi Pro League",
                Icon = new IconInfo("\uE710"),
            };
        }

        if (_calendarPage.Id == id)
        {
            return new CommandItem(_calendarPage)
            {
                Title = "Calendar",
                Subtitle = "Today's date, week number and a month calendar",
                Icon = new IconInfo("\uE787"),
            };
        }

        if (_calendarSettingsPage.Id == id)
        {
            return new CommandItem(_calendarSettingsPage)
            {
                Title = "Calendar Settings",
                Subtitle = "Main calendar, Persian month names and Qamari date",
                Icon = new IconInfo("\uE713"),
            };
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

        if (_todayAddPage.Id == id)
        {
            return new CommandItem(_todayAddPage)
            {
                Title = "Add Daily Checklist Item",
                Subtitle = "Add a habit or task that resets daily at midnight",
                Icon = new IconInfo("\uE710"),
            };
        }

        if (_todaySettingsPage.Id == id)
        {
            return new CommandItem(_todaySettingsPage)
            {
                Title = "Today — Daily Settings",
                Subtitle = "Configure coffee reminder time and daily water goal",
                Icon = new IconInfo("\uE713"),
            };
        }

        if (_pomodoroStatsPage.Id == id)
        {
            return new CommandItem(_pomodoroStatsPage)
            {
                Title = "Pomodoro — Stats",
                Subtitle = "Today and last 7 days completed focus sessions",
                Icon = new IconInfo("\uE9D2"),
            };
        }

        if (_pomodoroSettingsPage.Id == id)
        {
            return new CommandItem(_pomodoroSettingsPage)
            {
                Title = "Pomodoro Settings",
                Subtitle = "Configure focus, short break, long break, and cycle length",
                Icon = new IconInfo("\uE713"),
            };
        }

        if (_workFolderSettingsPage.Id == id)
        {
            return new CommandItem(_workFolderSettingsPage)
            {
                Title = "Change Working Folder",
                Subtitle = "Choose the folder the Working Folder band opens",
                Icon = new IconInfo("\uE70F"),
            };
        }

        return null;
    }

    internal void DisposeResources()
    {
        _wirelessDockItem.Dispose();
        _networkDockItem.Dispose();
        _networkPage.Dispose();
        _networkDashboardPage.Dispose();
        _locationDockItem.Dispose();
        _calendarDockItem.Dispose();
        _footballDockItem.Dispose();
        _footballPage.Dispose();
        _footballAddPage.Dispose();
        _footballService.Dispose();
        _calendarPage.Dispose();
        _calendarSettingsPage.Dispose();
        _calendarService.Dispose();
        _locationPage.Dispose();
        _locationService.Dispose();
        _prayerDockItem.Dispose();
        _aiUsageDockItem.Dispose();
        _aiUsagePage.Dispose();
        _aiDashboardPage.Dispose();
        _aiDockPickerPage.Dispose();
        _todayDockItem.Dispose();
        _pomodoroToggleDockItem.Dispose();
        _pomodoroDockItem.Dispose();
        _pomodoroPage.Dispose();
        _pomodoroStatsPage.Dispose();
        _pomodoroService.Dispose();
        _workFolderDockItem.Dispose();
    }
}

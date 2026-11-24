using System;
using System.Threading;
using DockPulse.Commands;
using DockPulse.Pages;
using DockPulse.Services;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace DockPulse.DockBands;

internal sealed partial class PrayerTimesDockBandItem : ListItem, IDisposable
{
    private readonly PrayerTimesService _service;
    private readonly PrayerTimesPage _page;
    private readonly PrayerRegionPickerPage _regionPickerPage;
    private readonly PrayerMethodPickerPage _methodPickerPage;
    private readonly Timer _timer;

    public PrayerTimesDockBandItem(
        PrayerTimesService service,
        PrayerTimesPage page,
        PrayerRegionPickerPage regionPickerPage,
        PrayerMethodPickerPage methodPickerPage)
        : base(page)
    {
        _service = service;
        _page = page;
        _regionPickerPage = regionPickerPage;
        _methodPickerPage = methodPickerPage;

        Title = "Prayer";
        Subtitle = _service.SelectedRegion.City;
        Icon = new IconInfo("\uE708");
        MoreCommands = BuildMoreCommands(_service.NotificationsEnabled);

        _service.ConfigurationChanged += Refresh;

        // Immediate calculation, then refresh every 30s to keep minute countdown & toast notifications accurate
        _timer = new Timer(_ => Refresh(), null, TimeSpan.Zero, TimeSpan.FromSeconds(30));
    }

    public void Refresh()
    {
        try
        {
            var snapshot = _service.GetCurrentSchedule();
            _service.CheckAndSendNotification(snapshot);
            _ = _page.UpdateSnapshotAsync(snapshot);

            Title = snapshot.FormatCountdownShort();
            Subtitle = snapshot.FormatDockSubtitle();
            Icon = new IconInfo(PrayerTimesService.GetPrayerGlyph(snapshot.CardPrayerName));
            MoreCommands = BuildMoreCommands(snapshot.NotificationsEnabled);
        }
        catch
        {
            Title = "Prayer";
            Subtitle = "N/A";
        }
    }

    private IContextItem[] BuildMoreCommands(bool notificationsEnabled) =>
    [
        new CommandContextItem(_regionPickerPage)
        {
            Title = "Change Region & Timezone",
            Icon = new IconInfo("\uE707"),
        },
        new CommandContextItem(_methodPickerPage)
        {
            Title = "Change Calculation Method",
            Icon = new IconInfo("\uE909"),
        },
        new CommandContextItem(new ActionCommand(
            $"Prayer Notifications ({(notificationsEnabled ? "On" : "Off")})",
            () => _service.ToggleNotifications(),
            new IconInfo(notificationsEnabled ? "\uEA8F" : "\uE7ED"))),
        new CommandContextItem(new ActionCommand(
            "Refresh Prayer Times",
            Refresh,
            new IconInfo("\uE72C"))),
    ];

    public void Dispose()
    {
        _service.ConfigurationChanged -= Refresh;
        _timer.Dispose();
        _page.Dispose();
    }
}

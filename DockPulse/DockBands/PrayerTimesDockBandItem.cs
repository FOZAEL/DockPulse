using System;
using System.Threading;
using DockPulse.Commands;
using DockPulse.Pages;
using DockPulse.Services;
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

        MoreCommands =
        [
            new CommandContextItem(_regionPickerPage) { Title = "Change Region / City" },
            new CommandContextItem(new ActionCommand("Toggle Asr School (Hanafi / Standard)", () => _service.ToggleAsrSchool(), new IconInfo("\uE8AB"))),
            new CommandContextItem(_methodPickerPage) { Title = "Change Calculation Method" },
            new CommandContextItem(new ActionCommand("Refresh Prayer Times", Refresh, new IconInfo("\uE72C"))),
        ];

        _service.ConfigurationChanged += Refresh;

        // Immediate calculation, then refresh every 30s to keep minute countdown accurate
        _timer = new Timer(_ => Refresh(), null, TimeSpan.Zero, TimeSpan.FromSeconds(30));
    }

    public void Refresh()
    {
        try
        {
            var snapshot = _service.GetCurrentSchedule();
            _page.UpdateSnapshot(snapshot);

            Title = snapshot.FormatCountdownShort();
            Subtitle = snapshot.FormatDockSubtitle();
            Icon = new IconInfo(PrayerTimesService.GetPrayerGlyph(snapshot.NextPrayerName));
        }
        catch
        {
            Title = "Prayer";
            Subtitle = "N/A";
        }
    }

    public void Dispose()
    {
        _service.ConfigurationChanged -= Refresh;
        _timer.Dispose();
    }
}

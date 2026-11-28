using System;
using System.Globalization;
using DockPulse.Pages;
using DockPulse.Services;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace DockPulse.DockBands;

/// <summary>Today's date and ISO week on the Dock; a click opens the Calendar card.</summary>
internal sealed partial class CalendarDockBandItem : ListItem, IDisposable
{
    private readonly CalendarService _service;
    private readonly CalendarSettingsPage _settingsPage;

    public CalendarDockBandItem(CalendarService service, CalendarPage page, CalendarSettingsPage settingsPage)
        : base(page)
    {
        _service = service;
        _settingsPage = settingsPage;
        Icon = new IconInfo("\uE787");

        _service.Changed += Refresh;
        Refresh();
    }

    public void Refresh()
    {
        var today = DateTime.Today;
        Title = today.ToString("ddd, MMM d", CultureInfo.InvariantCulture);
        Subtitle = $"Week {CalendarService.IsoWeek(today)}";
        MoreCommands = CalendarPage.BuildMoreCommands(_settingsPage, today);
    }

    public void Dispose() => _service.Changed -= Refresh;
}

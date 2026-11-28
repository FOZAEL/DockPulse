using System;
using System.Collections.Generic;
using System.Linq;
using DockPulse.Commands;
using DockPulse.Helpers;
using DockPulse.Services;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace DockPulse.Pages;

internal sealed partial class CalendarSettingsPage : ListPage, IDisposable
{
    private readonly CalendarService _service;

    public CalendarSettingsPage(CalendarService service)
    {
        _service = service;
        Id = "com.dockpulse.page.calendar.settings";
        Name = "Calendar Settings";
        Title = "Calendar Settings";
        Icon = new IconInfo("\uE713");
        PlaceholderText = "Filter settings...";
        _service.Changed += OnChanged;
    }

    public override IListItem[] GetItems()
    {
        var today = DateTime.Today;
        var main = _service.Main;
        var names = _service.PersianNames;
        int adjust = _service.QamariAdjust;
        var items = new List<IListItem>();

        foreach (var kind in new[] { CalendarKind.Gregorian, CalendarKind.Persian, CalendarKind.Qamari })
        {
            var target = kind;
            items.Add(new ListItem(new ActionCommand(
                "Use as Main Calendar",
                () =>
                {
                    _service.SetMain(target);
                    DockPulseHelpers.ShowInlineToast($"Main calendar: {CalendarService.Label(target)}");
                },
                new IconInfo("\uE73E")))
            {
                Section = "Main calendar",
                Title = CalendarService.Label(kind),
                Subtitle = $"Today: {_service.Format(kind, today)}",
                Icon = new IconInfo("\uE787"),
                Tags = Selected(main == kind),
            });
        }

        var persianToday = _service.DateIn(CalendarKind.Persian, today);
        foreach (var (choice, months, region) in new[]
        {
            (PersianMonthNames.Afghan, CalendarService.AfghanMonths, "Afghanistan (Dari)"),
            (PersianMonthNames.Iranian, CalendarService.IranianMonths, "Iran (Farsi)"),
        })
        {
            var target = choice;
            items.Add(new ListItem(new ActionCommand(
                "Use These Names",
                () =>
                {
                    _service.SetPersianNames(target);
                    DockPulseHelpers.ShowInlineToast($"Persian months: {string.Join(", ", months.Take(3))}...");
                },
                new IconInfo("\uE73E")))
            {
                Section = "Persian month names",
                Title = string.Join(", ", months.Take(3)) + "...",
                Subtitle = $"{region}  •  today: {persianToday.Day} {months[persianToday.Month - 1]} {persianToday.Year}",
                Icon = new IconInfo("\uE787"),
                Tags = Selected(names == choice),
            });
        }

        foreach (var (days, title, toast) in new[]
        {
            (-1, "One day earlier", "Qamari date: one day earlier"),
            (0, "Umm al-Qura calendar (no change)", "Qamari date: Umm al-Qura"),
            (1, "One day later", "Qamari date: one day later"),
        })
        {
            int target = days;
            items.Add(new ListItem(new ActionCommand(
                "Use This",
                () =>
                {
                    _service.SetQamariAdjust(target);
                    DockPulseHelpers.ShowInlineToast(toast);
                },
                new IconInfo("\uE73E")))
            {
                Section = "Qamari date",
                Title = title,
                Subtitle = $"Today: {CalendarService.Format(CalendarKind.Qamari, today, names, days)}",
                Icon = new IconInfo("\uE708"),
                Tags = Selected(adjust == days),
            });
        }

        items.Add(new ListItem(new NoOpCommand())
        {
            Section = "Qamari date",
            Title = "Why adjust the Qamari date?",
            Subtitle = "Months start with the moon sighting, so where you live the date can be a day off from Umm al-Qura",
            Icon = new IconInfo("\uE946"),
        });

        return [.. items];
    }

    private static ITag[] Selected(bool isSelected) =>
        isSelected ? [DockPulseHelpers.CreateTag("Selected", TagTone.Accent)] : [];

    private void OnChanged() => RaiseItemsChanged();

    public void Dispose() => _service.Changed -= OnChanged;
}

using System;
using System.Collections.Generic;
using DockPulse.Commands;
using DockPulse.Services;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace DockPulse.Pages;

internal sealed partial class PrayerMethodPickerPage : ListPage
{
    private readonly PrayerTimesService _service;

    public PrayerMethodPickerPage(PrayerTimesService service)
    {
        _service = service;

        Id = "com.dockpulse.page.prayer.method";
        Name = "Calculation Method";
        Title = "Sunni Prayer Calculation Method";
        Icon = new IconInfo("\uE909");
        PlaceholderText = "Filter calculation methods...";

        _service.ConfigurationChanged += () => RaiseItemsChanged(PrayerTimesService.Methods.Count);
    }

    public override IListItem[] GetItems()
    {
        var current = _service.SelectedMethod;
        var items = new List<IListItem>(PrayerTimesService.Methods.Count);

        foreach (var method in PrayerTimesService.Methods)
        {
            bool isSelected = string.Equals(method.Id, current.Id, StringComparison.OrdinalIgnoreCase);
            string ishaDesc = method.IshaMinutesAfterMaghrib > 0
                ? $"{method.IshaMinutesAfterMaghrib:0} min after Maghrib"
                : $"{method.IshaAngle:0.#}°";

            var tags = new List<ITag>();
            if (isSelected)
            {
                tags.Add(new Tag("Selected"));
            }

            string targetMethodId = method.Id;
            items.Add(new ListItem(new ActionCommand(
                "Select Method",
                () => _service.SelectMethod(targetMethodId),
                new IconInfo("\uE73E"),
                result: CommandResult.GoBack()))
            {
                Title = method.Name,
                Subtitle = $"Fajr: {method.FajrAngle:0.#}°  •  Isha: {ishaDesc}",
                Icon = new IconInfo(isSelected ? "\uE73E" : "\uE909"),
                Tags = [.. tags],
            });
        }

        return [.. items];
    }
}

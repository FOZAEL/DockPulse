using System;
using System.Collections.Generic;
using DockPulse.Commands;
using DockPulse.Services;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace DockPulse.Pages;

internal sealed partial class PrayerRegionPickerPage : ListPage
{
    private readonly PrayerTimesService _service;

    public PrayerRegionPickerPage(PrayerTimesService service)
    {
        _service = service;

        Id = "com.dockpulse.page.prayer.region";
        Name = "Select Region";
        Title = "Select Region / City (Prayer Times)";
        Icon = new IconInfo("\uE707");
        PlaceholderText = "Search city or country (e.g. Kabul, Tehran, Najaf, Dubai, Makkah, London)...";

        _service.ConfigurationChanged += () => RaiseItemsChanged(PrayerTimesService.Regions.Count);
    }

    public override IListItem[] GetItems()
    {
        var current = _service.SelectedRegion;
        var items = new List<IListItem>(PrayerTimesService.Regions.Count);

        foreach (var region in PrayerTimesService.Regions)
        {
            bool isSelected = string.Equals(region.Id, current.Id, StringComparison.OrdinalIgnoreCase);
            string asrLabel = region.DefaultAsrSchool == AsrJuristicSchool.Hanafi ? "Hanafi" : "Standard";

            var tags = new List<ITag>
            {
                new Tag(region.Country),
            };

            if (isSelected)
            {
                tags.Insert(0, new Tag("Selected"));
            }

            string targetRegionId = region.Id;
            items.Add(new ListItem(new ActionCommand(
                "Select Region",
                () => _service.SelectRegion(targetRegionId),
                new IconInfo("\uE73E"),
                result: CommandResult.GoBack()))
            {
                Title = region.DisplayName,
                Subtitle = $"Default: {asrLabel} Asr • {region.Latitude:0.00}°, {region.Longitude:0.00}°",
                Icon = new IconInfo(isSelected ? "\uE73E" : "\uE707"),
                Tags = [.. tags],
            });
        }

        return [.. items];
    }
}

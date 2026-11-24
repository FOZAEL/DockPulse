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
        Title = "Select Region & Timezone";
        Icon = new IconInfo("\uE707");
        PlaceholderText = "Search city, country, or timezone (e.g. Kabul, UTC+04:30, Dubai, London)...";

        _service.ConfigurationChanged += () => RaiseItemsChanged(PrayerTimesService.Regions.Count);
    }

    public override IListItem[] GetItems()
    {
        var current = _service.SelectedRegion;
        var items = new List<IListItem>(PrayerTimesService.Regions.Count);

        foreach (var region in PrayerTimesService.Regions)
        {
            bool isSelected = string.Equals(region.Id, current.Id, StringComparison.OrdinalIgnoreCase);
            string tzLabel = PrayerTimesService.GetRegionUtcOffsetLabel(region);
            string asrLabel = region.DefaultAsrSchool == AsrJuristicSchool.Hanafi ? "Hanafi" : "Standard";

            ITag[] tags = isSelected
                ? [new Tag("Selected"), new Tag(tzLabel), new Tag(region.Country)]
                : [new Tag(tzLabel), new Tag(region.Country)];

            string targetRegionId = region.Id;
            items.Add(new ListItem(new ActionCommand(
                "Select Region",
                () => _service.SelectRegion(targetRegionId),
                new IconInfo("\uE73E"),
                result: CommandResult.GoBack()))
            {
                Title = region.DisplayName,
                Subtitle = $"{tzLabel}  •  Default: {asrLabel} Asr  •  {region.Latitude:0.00}°, {region.Longitude:0.00}°",
                Icon = new IconInfo(isSelected ? "\uE73E" : "\uE707"),
                Tags = tags,
            });
        }

        return [.. items];
    }
}

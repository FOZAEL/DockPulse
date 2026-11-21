using System;
using System.Collections.Generic;
using System.Globalization;
using DockPulse.Commands;
using DockPulse.Services;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace DockPulse.Pages;

internal sealed partial class PrayerTimesPage : ListPage
{
    private readonly PrayerTimesService _service;
    private readonly PrayerRegionPickerPage _regionPickerPage;
    private readonly PrayerMethodPickerPage _methodPickerPage;

    private PrayerScheduleSnapshot _snapshot;

    public Action? OnManualRefresh { get; set; }

    public PrayerTimesPage(
        PrayerTimesService service,
        PrayerRegionPickerPage regionPickerPage,
        PrayerMethodPickerPage methodPickerPage)
    {
        _service = service;
        _regionPickerPage = regionPickerPage;
        _methodPickerPage = methodPickerPage;
        _snapshot = _service.GetCurrentSchedule();

        Id = "com.dockpulse.page.prayer";
        Name = "Prayer Times";
        Title = "Prayer Times (Sunni & Shia)";
        Icon = new IconInfo("\uE708");
        PlaceholderText = "Filter prayers or settings...";
    }

    public void UpdateSnapshot(PrayerScheduleSnapshot snapshot)
    {
        _snapshot = snapshot;
        RaiseItemsChanged(snapshot.TodayEntries.Count + 5);
    }

    public override IListItem[] GetItems()
    {
        var snapshot = _snapshot;
        var items = new List<IListItem>();

        string tomorrowSuffix = snapshot.NextPrayerIsTomorrow ? " (Tomorrow)" : string.Empty;
        items.Add(new ListItem(_regionPickerPage)
        {
            Title = $"Next: {snapshot.NextPrayerName} — {snapshot.FormatCountdownShort()}",
            Subtitle = $"Starts at {snapshot.NextPrayerTime:HH:mm}{tomorrowSuffix}  •  {snapshot.Region.DisplayName}",
            Icon = new IconInfo(PrayerTimesService.GetPrayerGlyph(snapshot.NextPrayerName)),
            Tags =
            [
                new Tag($"{snapshot.NextPrayerTime:HH:mm}"),
                new Tag(snapshot.Region.City),
            ],
            MoreCommands =
            [
                new CommandContextItem(_regionPickerPage) { Title = "Change Region / City" },
                new CommandContextItem(new ActionCommand("Toggle Asr School (Hanafi / Standard)", () => _service.ToggleAsrSchool(), new IconInfo("\uE8AB"))),
                new CommandContextItem(_methodPickerPage) { Title = "Change Calculation Method" },
            ],
        });

        foreach (var entry in snapshot.TodayEntries)
        {
            var tags = new List<ITag>
            {
                new Tag(entry.Time.ToString("HH:mm", CultureInfo.InvariantCulture)),
            };

            string statusText;
            if (!entry.IsObligatory)
            {
                if (string.Equals(entry.Name, "Sunset", StringComparison.Ordinal))
                {
                    tags.Add(new Tag("Sunset"));
                    statusText = entry.IsPassed ? "Sun has set" : "Maghrib follows after twilight";
                }
                else
                {
                    tags.Add(new Tag("Sunrise"));
                    statusText = entry.IsPassed ? "Fajr window ended" : "Marks end of Fajr";
                }
            }
            else if (entry.IsNext)
            {
                tags.Add(new Tag("Next"));
                statusText = $"Starts {snapshot.FormatCountdownLong()}";
            }
            else if (entry.IsCurrent)
            {
                tags.Add(new Tag("Current"));
                statusText = "Current prayer time";
            }
            else if (entry.IsPassed)
            {
                tags.Add(new Tag("Passed"));
                statusText = "Completed";
            }
            else
            {
                statusText = "Upcoming";
            }

            items.Add(new ListItem(new CopyTextCommand($"{entry.Name}: {entry.Time:HH:mm} ({snapshot.Region.City})"))
            {
                Title = $"{entry.Name}  —  {entry.Time:HH:mm}",
                Subtitle = statusText,
                Icon = new IconInfo(PrayerTimesService.GetPrayerGlyph(entry.Name)),
                Tags = [.. tags],
            });
        }

        items.Add(new ListItem(_regionPickerPage)
        {
            Title = $"Change Region / City ({snapshot.Region.DisplayName})",
            Subtitle = "Choose from 60+ cities worldwide (auto-sets local defaults)",
            Icon = new IconInfo("\uE707"),
            Tags = [new Tag("Region")],
        });

        items.Add(new ListItem(new ActionCommand("Toggle Asr School", () => _service.ToggleAsrSchool(), new IconInfo("\uE8AB")))
        {
            Title = $"Asr School: {PrayerTimesService.FormatAsrSchoolName(snapshot.AsrSchool)}",
            Subtitle = "Click to switch between Hanafi (2x shadow) and Standard (Shafi'i / Maliki / Hanbali / Jafari)",
            Icon = new IconInfo("\uE8AB"),
            Tags = [new Tag(snapshot.AsrSchool == AsrJuristicSchool.Hanafi ? "Hanafi" : "Standard")],
        });

        items.Add(new ListItem(_methodPickerPage)
        {
            Title = $"Calculation Method: {snapshot.Method.ShortName}",
            Subtitle = $"{(snapshot.Method.IsShia ? "Shia" : "Sunni")} • {snapshot.Method.Name}",
            Icon = new IconInfo("\uE909"),
            Tags = [new Tag(snapshot.Method.IsShia ? "Shia" : "Sunni")],
        });

        items.Add(new ListItem(new ActionCommand("Refresh Prayer Times", TriggerRefresh, new IconInfo("\uE72C")))
        {
            Title = "Refresh Prayer Times",
            Subtitle = $"Local region time: {snapshot.RegionNow:ddd, MMM d • HH:mm}",
            Icon = new IconInfo("\uE72C"),
        });

        return [.. items];
    }

    private void TriggerRefresh()
    {
        if (OnManualRefresh is not null)
        {
            OnManualRefresh();
            return;
        }

        UpdateSnapshot(_service.GetCurrentSchedule());
    }
}

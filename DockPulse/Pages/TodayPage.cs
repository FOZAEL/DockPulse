using System;
using System.Collections.Generic;
using System.Linq;
using DockPulse.Commands;
using DockPulse.Helpers;
using DockPulse.Services;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace DockPulse.Pages;

internal sealed partial class TodayPage : DynamicListPage
{
    private readonly TodayService _service;
    private readonly TodayAddItemPage _addPage;
    private readonly TodaySettingsPage _settingsPage;

    public TodayPage(
        TodayService service,
        TodayAddItemPage addPage,
        TodaySettingsPage settingsPage)
    {
        _service = service;
        _addPage = addPage;
        _settingsPage = settingsPage;

        Id = "com.dockpulse.page.today";
        Name = "Today";
        Title = "Today — Daily Checklist";
        Icon = new IconInfo("\uE9D5");
        PlaceholderText = "Filter items or type a new task to quick-add...";

        _service.StateChanged += () => RaiseItemsChanged();
    }

    public override void UpdateSearchText(string oldSearch, string newSearch)
    {
        RaiseItemsChanged();
    }

    public override IListItem[] GetItems()
    {
        var snap = _service.GetSnapshot();
        string coffeeTimeStr = TodayService.FormatMinutes(snap.CoffeeTimeMinutes);

        ITag[] waterTags = snap.Water >= snap.WaterGoal
            ?
            [
                DockPulseHelpers.CreateTag($"{snap.Water}/{snap.WaterGoal}", TagTone.Ok),
                DockPulseHelpers.CreateTag("Goal Met", TagTone.Ok),
            ]
            : [DockPulseHelpers.CreateTag($"{snap.Water}/{snap.WaterGoal}", TagTone.Accent)];

        ITag[] coffeeTags = snap.CoffeeDone
            ?
            [
                DockPulseHelpers.CreateTag("Done", TagTone.Ok),
                DockPulseHelpers.CreateTag(coffeeTimeStr),
            ]
            : snap.IsCoffeeDue
                ?
                [
                    DockPulseHelpers.CreateTag("Coffee Time", TagTone.Warn),
                    DockPulseHelpers.CreateTag(coffeeTimeStr),
                ]
                : [DockPulseHelpers.CreateTag(coffeeTimeStr)];

        string coffeeSubtitle = snap.CoffeeDone
            ? $"Checked off • Scheduled for {coffeeTimeStr}"
            : snap.IsCoffeeDue
                ? $"Due since {coffeeTimeStr} • Enter to check off"
                : $"Scheduled for {coffeeTimeStr} • Dock alerts at {coffeeTimeStr}";

        var baseItems = new List<ListItem>
        {
            new(new ActionCommand(
                "+1 Glass",
                () =>
                {
                    int newCount = _service.AddWater(1);
                    DockPulseHelpers.ShowInlineToast(newCount == snap.WaterGoal
                        ? $"Water goal met ({newCount}/{snap.WaterGoal})!"
                        : $"Water: {newCount}/{snap.WaterGoal}");
                },
                new IconInfo("\uEB42")))
            {
                Section = "Daily Habits",
                Icon = new IconInfo("\uEB42"),
                Title = "Water",
                Subtitle = $"{snap.Water} of {snap.WaterGoal} glasses today • Enter to add 1",
                Tags = waterTags,
                MoreCommands =
                [
                    new CommandContextItem(new ActionCommand(
                        "Remove 1 Glass",
                        () =>
                        {
                            if (snap.Water > 0)
                            {
                                int updated = _service.AddWater(-1);
                                DockPulseHelpers.ShowInlineToast($"Water: {updated}/{snap.WaterGoal}");
                            }
                            else
                            {
                                DockPulseHelpers.ShowInlineToast("Water is already 0");
                            }
                        },
                        new IconInfo("\uE738"))),
                    new CommandContextItem(new ActionCommand(
                        "Reset Water to 0",
                        () =>
                        {
                            _service.ResetWater();
                            DockPulseHelpers.ShowInlineToast("Water reset to 0");
                        },
                        new IconInfo("\uE72C"))),
                    new CommandContextItem(_settingsPage)
                    {
                        Title = "Set Water Goal & Coffee Time",
                        Icon = new IconInfo("\uE713"),
                    },
                ],
            },
            new(new ActionCommand(
                snap.CoffeeDone ? "Uncheck Coffee" : "Check Off Coffee",
                () =>
                {
                    bool done = _service.ToggleCoffee();
                    DockPulseHelpers.ShowInlineToast(done ? "Coffee checked off" : "Coffee unchecked");
                },
                new IconInfo(snap.CoffeeDone ? "\uE73A" : "\uEC32")))
            {
                Section = "Daily Habits",
                Icon = new IconInfo(snap.CoffeeDone ? "\uE73A" : "\uEC32"),
                Title = "Coffee",
                Subtitle = coffeeSubtitle,
                Tags = coffeeTags,
                MoreCommands =
                [
                    new CommandContextItem(_settingsPage)
                    {
                        Title = "Set Coffee Time & Water Goal",
                        Icon = new IconInfo("\uE713"),
                    },
                ],
            },
        };

        foreach (var item in snap.Items)
        {
            int itemId = item.Id;
            string itemTitle = item.Title;
            bool itemDone = item.Done;

            baseItems.Add(new ListItem(new ActionCommand(
                itemDone ? "Uncheck" : "Check Off",
                () => _service.ToggleItem(itemId),
                new IconInfo(itemDone ? "\uE73A" : "\uE739")))
            {
                Section = "Daily Checklist (resets at 00:00)",
                Icon = new IconInfo(itemDone ? "\uE73A" : "\uE739"),
                Title = itemTitle,
                Subtitle = itemDone ? "Completed today • Resets at midnight" : "Daily item • Enter to check off",
                Tags = itemDone ? [DockPulseHelpers.CreateTag("Done", TagTone.Ok)] : [],
                MoreCommands =
                [
                    new CommandContextItem(new TodayEditItemPage(_service, itemId))
                    {
                        Title = "Edit Item...",
                        Icon = new IconInfo("\uE70F"),
                    },
                    new CommandContextItem(new ActionCommand(
                        "Remove Item",
                        () =>
                        {
                            string? removed = _service.RemoveItem(itemId);
                            if (removed is not null)
                            {
                                DockPulseHelpers.ShowInlineToast($"Removed “{removed}”");
                            }
                        },
                        new IconInfo("\uE74D"))),
                ],
            });
        }

        baseItems.Add(new ListItem(_addPage)
        {
            Section = "Manage",
            Icon = new IconInfo("\uE710"),
            Title = "Add Daily Item",
            Subtitle = "Open Adaptive Card form or type directly in the search box above",
        });

        baseItems.Add(new ListItem(_settingsPage)
        {
            Section = "Manage",
            Icon = new IconInfo("\uE713"),
            Title = "Daily Settings",
            Subtitle = $"Coffee at {coffeeTimeStr}  •  Water goal: {snap.WaterGoal} glasses",
            Tags =
            [
                DockPulseHelpers.CreateTag(coffeeTimeStr),
                DockPulseHelpers.CreateTag($"{snap.WaterGoal} glasses"),
            ],
        });

        baseItems.Add(new ListItem(new ActionCommand(
            "Reset Now",
            () =>
            {
                _service.ResetForNewDay();
                DockPulseHelpers.ShowInlineToast("Midnight reset: checklist cleared for the new day");
            },
            new IconInfo("\uE72C")))
        {
            Section = "Manage",
            Icon = new IconInfo("\uE72C"),
            Title = "Reset Today's Checklist",
            Subtitle = "Reset all items to unchecked and water to 0 (same as local midnight reset)",
        });

        string query = SearchText?.Trim() ?? string.Empty;
        IEnumerable<ListItem> filtered = string.IsNullOrEmpty(query)
            ? baseItems
            : baseItems.Where(i =>
                i.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                i.Subtitle.Contains(query, StringComparison.OrdinalIgnoreCase));

        var result = new List<IListItem>(filtered);

        if (!string.IsNullOrEmpty(query) &&
            !snap.Items.Any(x => string.Equals(x.Title, query, StringComparison.OrdinalIgnoreCase)))
        {
            string titleToAdd = query;
            result.Add(new ListItem(new ActionCommand(
                "Add Item",
                () =>
                {
                    _service.AddItem(titleToAdd);
                    SearchText = string.Empty;
                    DockPulseHelpers.ShowInlineToast($"Added “{titleToAdd}”");
                },
                new IconInfo("\uE710")))
            {
                Section = "Quick Add",
                Icon = new IconInfo("\uE710"),
                Title = $"Add “{titleToAdd}”",
                Subtitle = "Add as a daily item (resets every day at 00:00)",
                Tags = [DockPulseHelpers.CreateTag("Quick Add", TagTone.Accent)],
            });
        }

        return [.. result];
    }
}

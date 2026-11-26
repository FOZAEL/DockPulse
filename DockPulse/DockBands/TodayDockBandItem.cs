using System;
using System.Threading;
using DockPulse.Commands;
using DockPulse.Helpers;
using DockPulse.Pages;
using DockPulse.Services;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace DockPulse.DockBands;

internal sealed partial class TodayDockBandItem : ListItem, IDisposable
{
    private readonly TodayService _service;
    private readonly TodayAddItemPage _addPage;
    private readonly TodaySettingsPage _settingsPage;
    private readonly Timer _timer;

    public TodayDockBandItem(
        TodayService service,
        TodayPage page,
        TodayAddItemPage addPage,
        TodaySettingsPage settingsPage)
        : base(page)
    {
        _service = service;
        _addPage = addPage;
        _settingsPage = settingsPage;

        Title = "0/6 done";
        Subtitle = "Water 0/8";
        Icon = new IconInfo("\uE9D5");

        _service.StateChanged += Refresh;

        _timer = new Timer(_ => Tick(), null, TimeSpan.Zero, TimeSpan.FromSeconds(30));
    }

    private void Tick()
    {
        try
        {
            _service.CheckAndSendCoffeeNotification();
            Refresh();
        }
        catch
        {
            // Ignore periodic check errors
        }
    }

    public void Refresh()
    {
        try
        {
            var snap = _service.GetSnapshot();
            if (snap.IsCoffeeDue)
            {
                Icon = new IconInfo("\uEC32");
                Title = "Coffee time";
                Subtitle = $"Since {TodayService.FormatMinutes(snap.CoffeeTimeMinutes)} • {snap.DoneCount}/{snap.TotalCount}";
            }
            else
            {
                Icon = new IconInfo("\uE9D5");
                Title = $"{snap.DoneCount}/{snap.TotalCount} done";
                Subtitle = $"Water {snap.Water}/{snap.WaterGoal}";
            }

            MoreCommands = BuildMoreCommands(snap);
        }
        catch
        {
            Title = "Today";
            Subtitle = "Checklist";
        }
    }

    private IContextItem[] BuildMoreCommands(TodaySnapshot snap)
    {
        string coffeeTimeStr = TodayService.FormatMinutes(snap.CoffeeTimeMinutes);
        string coffeeLabel = snap.IsCoffeeDue
            ? "Check Off Coffee"
            : $"Toggle Coffee ({(snap.CoffeeDone ? "Done" : coffeeTimeStr)})";

        return
        [
            new CommandContextItem(new ActionCommand(
                $"Add 1 Glass of Water ({snap.Water}/{snap.WaterGoal})",
                () =>
                {
                    int newCount = _service.AddWater(1);
                    DockPulseHelpers.ShowInlineToast($"Water: {newCount}/{snap.WaterGoal}");
                },
                new IconInfo("\uEB42"))),
            new CommandContextItem(new ActionCommand(
                coffeeLabel,
                () =>
                {
                    bool done = _service.ToggleCoffee();
                    DockPulseHelpers.ShowInlineToast(done ? "Coffee checked off" : "Coffee unchecked");
                },
                new IconInfo("\uEC32"))),
            new CommandContextItem(_addPage)
            {
                Title = "Add Daily Item...",
                Icon = new IconInfo("\uE710"),
            },
            new CommandContextItem(_settingsPage)
            {
                Title = "Daily Settings...",
                Icon = new IconInfo("\uE713"),
            },
            new CommandContextItem(new ActionCommand(
                "Reset Today's Checklist",
                () =>
                {
                    _service.ResetForNewDay();
                    DockPulseHelpers.ShowInlineToast("Midnight reset: checklist cleared for the new day");
                },
                new IconInfo("\uE72C"))),
        ];
    }

    public void Dispose()
    {
        _service.StateChanged -= Refresh;
        _timer.Dispose();
    }
}

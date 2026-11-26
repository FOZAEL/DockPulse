using System.Globalization;
using System.Text.Json;
using DockPulse.Commands;
using DockPulse.Helpers;
using DockPulse.Services;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace DockPulse.Pages;

internal sealed partial class TodaySettingsPage : ContentPage
{
    private readonly TodayService _service;

    public TodaySettingsPage(TodayService service)
    {
        _service = service;

        Id = "com.dockpulse.page.today.settings";
        Name = "Daily Settings";
        Title = "Today — Daily Settings";
        Icon = new IconInfo("\uE713");

        Commands =
        [
            new CommandContextItem(new ActionCommand(
                "Cancel",
                static () => { },
                new IconInfo("\uE711"),
                result: CommandResult.GoBack())),
        ];
    }

    public override IContent[] GetContent() => [new DailySettingsForm(_service, _service.GetSnapshot())];

    private sealed partial class DailySettingsForm : FormContent
    {
        private readonly TodayService _service;

        public DailySettingsForm(TodayService service, TodaySnapshot snapshot)
        {
            _service = service;
            string coffeeStr = TodayService.FormatMinutes(snapshot.CoffeeTimeMinutes);
            string goalStr = snapshot.WaterGoal.ToString(CultureInfo.InvariantCulture);

            TemplateJson = $$"""
            {
                "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
                "type": "AdaptiveCard",
                "version": "1.5",
                "body": [
                    {
                        "type": "TextBlock",
                        "text": "Habits & Schedule",
                        "weight": "Bolder",
                        "size": "Medium"
                    },
                    {
                        "type": "TextBlock",
                        "text": "Configure when the Coffee reminder appears on the Dock band and your daily water target.",
                        "isSubtle": true,
                        "wrap": true,
                        "spacing": "Small"
                    },
                    {
                        "type": "Input.Time",
                        "id": "coffeeTime",
                        "label": "Coffee reminder time",
                        "value": "{{coffeeStr}}"
                    },
                    {
                        "type": "Input.Number",
                        "id": "waterGoal",
                        "label": "Daily water goal (glasses)",
                        "value": {{goalStr}},
                        "min": 1,
                        "max": 20
                    }
                ],
                "actions": [
                    {
                        "type": "Action.Submit",
                        "title": "Save Settings",
                        "style": "positive"
                    }
                ]
            }
            """;
        }

        public override CommandResult SubmitForm(string payload)
        {
            try
            {
                using var doc = JsonDocument.Parse(payload);
                var root = doc.RootElement;

                var current = _service.GetSnapshot();
                int coffeeMinutes = current.CoffeeTimeMinutes;

                if (DockPulseHelpers.TryReadString(root, "coffeeTime", out var rawTime) &&
                    !string.IsNullOrWhiteSpace(rawTime))
                {
                    if (!TodayService.TryParseHhMm(rawTime, out coffeeMinutes))
                    {
                        DockPulseHelpers.ShowInlineToast("Invalid coffee reminder time.");
                        return CommandResult.KeepOpen();
                    }
                }

                if (!DockPulseHelpers.TryReadInt(root, "waterGoal", out int goal) || goal < 1 || goal > 20)
                {
                    DockPulseHelpers.ShowInlineToast("Water goal must be between 1 and 20 glasses.");
                    return CommandResult.KeepOpen();
                }

                _service.UpdateSettings(coffeeMinutes, goal);
                DockPulseHelpers.ShowInlineToast($"Saved: Coffee {TodayService.FormatMinutes(coffeeMinutes)} • Water goal {goal}");
                return CommandResult.GoBack();
            }
            catch
            {
                return CommandResult.KeepOpen();
            }
        }
    }
}

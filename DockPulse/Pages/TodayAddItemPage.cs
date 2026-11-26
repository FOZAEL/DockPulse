using System.Text.Json;
using DockPulse.Commands;
using DockPulse.Helpers;
using DockPulse.Services;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace DockPulse.Pages;

internal sealed partial class TodayAddItemPage : ContentPage
{
    private readonly TodayService _service;

    public TodayAddItemPage(TodayService service)
    {
        _service = service;

        Id = "com.dockpulse.page.today.add";
        Name = "Add Daily Item";
        Title = "Add Daily Checklist Item";
        Icon = new IconInfo("\uE710");

        Commands =
        [
            new CommandContextItem(new ActionCommand(
                "Cancel",
                static () => { },
                new IconInfo("\uE711"),
                result: CommandResult.GoBack())),
        ];
    }

    public override IContent[] GetContent() => [new AddDailyItemForm(_service)];

    private sealed partial class AddDailyItemForm : FormContent
    {
        private readonly TodayService _service;

        public AddDailyItemForm(TodayService service)
        {
            _service = service;
            TemplateJson = """
            {
                "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
                "type": "AdaptiveCard",
                "version": "1.5",
                "body": [
                    {
                        "type": "TextBlock",
                        "text": "New Daily Item",
                        "weight": "Bolder",
                        "size": "Medium"
                    },
                    {
                        "type": "TextBlock",
                        "text": "Daily items stay in your list and reset to unchecked every day at 00:00 local time.",
                        "isSubtle": true,
                        "wrap": true,
                        "spacing": "Small"
                    },
                    {
                        "type": "Input.Text",
                        "id": "title",
                        "label": "Item title",
                        "placeholder": "e.g. Review pull requests",
                        "isRequired": true,
                        "errorMessage": "Please enter an item title."
                    }
                ],
                "actions": [
                    {
                        "type": "Action.Submit",
                        "title": "Add Daily Item",
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
                if (!DockPulseHelpers.TryReadString(doc.RootElement, "title", out var rawTitle) ||
                    string.IsNullOrWhiteSpace(rawTitle))
                {
                    DockPulseHelpers.ShowInlineToast("Please enter an item title.");
                    return CommandResult.KeepOpen();
                }

                string trimmed = rawTitle.Trim();
                _service.AddItem(trimmed);
                DockPulseHelpers.ShowInlineToast($"Added “{trimmed}”");
                return CommandResult.GoBack();
            }
            catch
            {
                return CommandResult.KeepOpen();
            }
        }
    }
}

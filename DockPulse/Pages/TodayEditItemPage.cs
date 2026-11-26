using System.Text.Json;
using DockPulse.Commands;
using DockPulse.Helpers;
using DockPulse.Services;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace DockPulse.Pages;

internal sealed partial class TodayEditItemPage : ContentPage
{
    private readonly TodayService _service;
    private readonly int _itemId;

    public TodayEditItemPage(TodayService service, int itemId)
    {
        _service = service;
        _itemId = itemId;

        Id = $"com.dockpulse.page.today.edit.{itemId}";
        Name = "Edit Daily Item";
        Title = "Edit Daily Checklist Item";
        Icon = new IconInfo("\uE70F");

        Commands =
        [
            new CommandContextItem(new ActionCommand(
                "Remove Item",
                RemoveCurrentItem,
                new IconInfo("\uE74D"),
                result: CommandResult.GoBack())),
            new CommandContextItem(new ActionCommand(
                "Cancel",
                static () => { },
                new IconInfo("\uE711"),
                result: CommandResult.GoBack())),
        ];
    }

    public override IContent[] GetContent()
    {
        var item = _service.TryGetItem(_itemId);
        return
        [
            new EditDailyItemForm(_service, _itemId, item?.Title ?? string.Empty),
            new RemoveDailyItemForm(_service, _itemId),
        ];
    }

    private void RemoveCurrentItem()
    {
        string? removed = _service.RemoveItem(_itemId);
        if (removed is not null)
        {
            DockPulseHelpers.ShowInlineToast($"Removed “{removed}”");
        }
    }

    private sealed partial class EditDailyItemForm : FormContent
    {
        private readonly TodayService _service;
        private readonly int _itemId;

        public EditDailyItemForm(TodayService service, int itemId, string currentTitle)
        {
            _service = service;
            _itemId = itemId;

            string escapedTitle = JsonEncodedText.Encode(currentTitle).ToString();
            TemplateJson = $$"""
            {
                "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
                "type": "AdaptiveCard",
                "version": "1.5",
                "body": [
                    {
                        "type": "TextBlock",
                        "text": "Edit Daily Item",
                        "weight": "Bolder",
                        "size": "Medium"
                    },
                    {
                        "type": "Input.Text",
                        "id": "title",
                        "label": "Item title",
                        "value": "{{escapedTitle}}",
                        "isRequired": true,
                        "errorMessage": "Please enter an item title."
                    }
                ],
                "actions": [
                    {
                        "type": "Action.Submit",
                        "title": "Save Changes",
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

                _service.EditItem(_itemId, rawTitle);
                DockPulseHelpers.ShowInlineToast("Item updated");
                return CommandResult.GoBack();
            }
            catch
            {
                return CommandResult.KeepOpen();
            }
        }
    }

    private sealed partial class RemoveDailyItemForm : FormContent
    {
        private readonly TodayService _service;
        private readonly int _itemId;

        public RemoveDailyItemForm(TodayService service, int itemId)
        {
            _service = service;
            _itemId = itemId;

            TemplateJson = """
            {
                "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
                "type": "AdaptiveCard",
                "version": "1.5",
                "body": [],
                "actions": [
                    {
                        "type": "Action.Submit",
                        "title": "Remove Item",
                        "style": "destructive"
                    }
                ]
            }
            """;
        }

        public override CommandResult SubmitForm(string payload)
        {
            string? removed = _service.RemoveItem(_itemId);
            if (removed is not null)
            {
                DockPulseHelpers.ShowInlineToast($"Removed “{removed}”");
            }

            return CommandResult.GoBack();
        }
    }
}

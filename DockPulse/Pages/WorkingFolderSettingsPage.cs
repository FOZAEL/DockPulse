using System.IO;
using System.Text.Json;
using DockPulse.Commands;
using DockPulse.Helpers;
using DockPulse.Services;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace DockPulse.Pages;

internal sealed partial class WorkingFolderSettingsPage : ContentPage
{
    private readonly WorkingFolderService _service;

    public WorkingFolderSettingsPage(WorkingFolderService service)
    {
        _service = service;

        Id = "com.dockpulse.page.workfolder.settings";
        Name = "Change Folder";
        Title = "Working Folder";
        Icon = new IconInfo("\uE8B7");

        Commands =
        [
            new CommandContextItem(new ActionCommand(
                "Cancel",
                static () => { },
                new IconInfo("\uE711"),
                result: CommandResult.GoBack())),
        ];
    }

    public override IContent[] GetContent() => [new WorkingFolderForm(_service)];

    private sealed partial class WorkingFolderForm : FormContent
    {
        private readonly WorkingFolderService _service;

        public WorkingFolderForm(WorkingFolderService service)
        {
            _service = service;

            // JsonEncodedText escapes backslashes and quotes so the path is a valid JSON string value
            string pathJson = JsonEncodedText.Encode(service.FolderPath).ToString();

            TemplateJson = $$"""
            {
                "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
                "type": "AdaptiveCard",
                "version": "1.5",
                "body": [
                    {
                        "type": "TextBlock",
                        "text": "Working Folder",
                        "weight": "Bolder",
                        "size": "Medium"
                    },
                    {
                        "type": "TextBlock",
                        "text": "The Dock band opens this folder in File Explorer with one click.",
                        "isSubtle": true,
                        "wrap": true,
                        "spacing": "Small"
                    },
                    {
                        "type": "Input.Text",
                        "id": "path",
                        "label": "Folder path",
                        "placeholder": "C:\\Dev\\MyProject",
                        "value": "{{pathJson}}"
                    }
                ],
                "actions": [
                    {
                        "type": "Action.Submit",
                        "title": "Save Folder",
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
                DockPulseHelpers.TryReadString(doc.RootElement, "path", out var raw);

                if (!WorkingFolderService.TryNormalize(raw, out string path))
                {
                    DockPulseHelpers.ShowInlineToast("Enter a folder path.");
                    return CommandResult.KeepOpen();
                }

                if (!Directory.Exists(path))
                {
                    DockPulseHelpers.ShowInlineToast($"Folder not found: {path}");
                    return CommandResult.KeepOpen();
                }

                _service.SetFolder(path);
                DockPulseHelpers.ShowInlineToast($"Working folder: {path}");
                return CommandResult.GoBack();
            }
            catch
            {
                return CommandResult.KeepOpen();
            }
        }
    }
}

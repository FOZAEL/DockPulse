using System.Globalization;
using System.Text.Json;
using DockPulse.Commands;
using DockPulse.Helpers;
using DockPulse.Services;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace DockPulse.Pages;

internal sealed partial class PomodoroSettingsPage : ContentPage
{
    private readonly PomodoroService _service;

    public PomodoroSettingsPage(PomodoroService service)
    {
        _service = service;

        Id = "com.dockpulse.page.pomodoro.settings";
        Name = "Pomodoro Settings";
        Title = "Pomodoro Settings";
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

    public override IContent[] GetContent() => [new PomodoroSettingsForm(_service, _service.GetSnapshot())];

    private sealed partial class PomodoroSettingsForm : FormContent
    {
        private readonly PomodoroService _service;

        public PomodoroSettingsForm(PomodoroService service, PomodoroSnapshot snap)
        {
            _service = service;

            string focusStr = snap.FocusMinutes.ToString(CultureInfo.InvariantCulture);
            string shortStr = snap.ShortBreakMinutes.ToString(CultureInfo.InvariantCulture);
            string longStr = snap.LongBreakMinutes.ToString(CultureInfo.InvariantCulture);
            string cycleStr = snap.SessionsPerCycle.ToString(CultureInfo.InvariantCulture);
            string notifyStr = snap.NotifyOnPhaseEnd ? "true" : "false";

            TemplateJson = $$"""
            {
                "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
                "type": "AdaptiveCard",
                "version": "1.5",
                "body": [
                    {
                        "type": "TextBlock",
                        "text": "Timer Durations & Cycle",
                        "weight": "Bolder",
                        "size": "Medium"
                    },
                    {
                        "type": "TextBlock",
                        "text": "Changes apply immediately to upcoming phases (and to the current timer when paused at full duration).",
                        "isSubtle": true,
                        "wrap": true,
                        "spacing": "Small"
                    },
                    {
                        "type": "ColumnSet",
                        "columns": [
                            {
                                "type": "Column",
                                "width": "stretch",
                                "items": [
                                    {
                                        "type": "Input.Number",
                                        "id": "focusMin",
                                        "label": "Focus (minutes)",
                                        "value": {{focusStr}},
                                        "min": 1,
                                        "max": 120
                                    }
                                ]
                            },
                            {
                                "type": "Column",
                                "width": "stretch",
                                "items": [
                                    {
                                        "type": "Input.Number",
                                        "id": "shortBreakMin",
                                        "label": "Short break (minutes)",
                                        "value": {{shortStr}},
                                        "min": 1,
                                        "max": 60
                                    }
                                ]
                            }
                        ]
                    },
                    {
                        "type": "ColumnSet",
                        "columns": [
                            {
                                "type": "Column",
                                "width": "stretch",
                                "items": [
                                    {
                                        "type": "Input.Number",
                                        "id": "longBreakMin",
                                        "label": "Long break (minutes)",
                                        "value": {{longStr}},
                                        "min": 1,
                                        "max": 60
                                    }
                                ]
                            },
                            {
                                "type": "Column",
                                "width": "stretch",
                                "items": [
                                    {
                                        "type": "Input.Number",
                                        "id": "sessionsPerCycle",
                                        "label": "Sessions before long break",
                                        "value": {{cycleStr}},
                                        "min": 1,
                                        "max": 8
                                    }
                                ]
                            }
                        ]
                    },
                    {
                        "type": "Input.Toggle",
                        "id": "notify",
                        "title": "Send Windows notification when phase ends",
                        "valueOn": "true",
                        "valueOff": "false",
                        "value": "{{notifyStr}}"
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

                if (!DockPulseHelpers.TryReadInt(root, "focusMin", out int focus) || focus < 1 ||
                    !DockPulseHelpers.TryReadInt(root, "shortBreakMin", out int sb) || sb < 1 ||
                    !DockPulseHelpers.TryReadInt(root, "longBreakMin", out int lb) || lb < 1 ||
                    !DockPulseHelpers.TryReadInt(root, "sessionsPerCycle", out int cyc) || cyc < 1)
                {
                    DockPulseHelpers.ShowInlineToast("All durations and cycle count must be 1 or greater.");
                    return CommandResult.KeepOpen();
                }

                bool notify = !DockPulseHelpers.TryReadBool(root, "notify", out bool parsedNotify) || parsedNotify;

                int clampedCyc = System.Math.Min(8, cyc);
                _service.UpdateSettings(focus, sb, lb, clampedCyc, notify);
                DockPulseHelpers.ShowInlineToast($"Saved: {focus}/{sb}/{lb}m ({clampedCyc}×)");
                return CommandResult.GoBack();
            }
            catch
            {
                return CommandResult.KeepOpen();
            }
        }
    }
}

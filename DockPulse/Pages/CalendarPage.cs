using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DockPulse.Commands;
using DockPulse.Helpers;
using DockPulse.Services;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Windows.Storage.Streams;

namespace DockPulse.Pages;

/// <summary>The Calendar card for one week, the week buttons, and today's date in all three calendars.</summary>
internal sealed partial class CalendarPage : ContentPage, IDisposable
{
    private readonly CalendarService _service;
    private readonly CalendarSettingsPage _settingsPage;
    private readonly object _stateLock = new();

    private int _weekOffset;
    private IContent[] _cachedContent;
    private List<InMemoryRandomAccessStream> _activeStreams = [];
    private List<InMemoryRandomAccessStream> _retiringStreams = [];
    private int _refreshQueued;
    private int _isRefreshing;

    public CalendarPage(CalendarService service, CalendarSettingsPage settingsPage)
    {
        _service = service;
        _settingsPage = settingsPage;

        Id = "com.dockpulse.page.calendar";
        Name = "Calendar";
        Title = "Calendar";
        Icon = new IconInfo("\uE787");

        _cachedContent = [new MarkdownContent(BuildSummary(DateTime.Today))];
        _service.Changed += RequestRefresh;
        RequestRefresh();
    }

    public override IContent[] GetContent()
    {
        lock (_stateLock)
        {
            return _cachedContent;
        }
    }

    /// <summary>Opens on this week again (the band calls this before showing the page).</summary>
    public void ResetToToday() => MoveWeeks(0);

    private void MoveWeeks(int delta)
    {
        lock (_stateLock)
        {
            _weekOffset = delta == 0 ? 0 : _weekOffset + delta;
        }

        RequestRefresh();
    }

    private void RequestRefresh()
    {
        Interlocked.Exchange(ref _refreshQueued, 1);
        if (Interlocked.Exchange(ref _isRefreshing, 1) == 0)
        {
            _ = ProcessRefreshLoopAsync();
        }
    }

    private async Task ProcessRefreshLoopAsync()
    {
        try
        {
            while (Interlocked.Exchange(ref _refreshQueued, 0) == 1)
            {
                try
                {
                    await RefreshOnceAsync().ConfigureAwait(false);
                }
                catch
                {
                    // Keep the last card if drawing fails
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _isRefreshing, 0);
            if (Interlocked.CompareExchange(ref _refreshQueued, 0, 0) == 1)
            {
                RequestRefresh();
            }
        }
    }

    private async Task RefreshOnceAsync()
    {
        int offset;
        lock (_stateLock)
        {
            offset = _weekOffset;
        }

        var today = DateTime.Today;
        var main = _service.Main;
        var weekStart = CalendarService.WeekStart(today).AddDays(offset * 7);
        var mid = weekStart.AddDays(3);
        var midDate = _service.DateIn(main, mid);
        var months = _service.MonthNames(main);
        string monthName = months[midDate.Month - 1];

        var week = new List<CalendarRenderer.WeekDay>(7);
        for (int i = 0; i < 7; i++)
        {
            var d = weekStart.AddDays(i);
            var inMain = _service.DateIn(main, d);
            week.Add(new CalendarRenderer.WeekDay(
                d,
                inMain.Day,
                inMain.Month == midDate.Month,
                d == today,
                d.DayOfWeek.ToString()[..1]));
        }

        var others = new[] { CalendarKind.Gregorian, CalendarKind.Persian, CalendarKind.Qamari }.Where(k => k != main).ToArray();
        var card = new CalendarRenderer.CardData(
            midDate.Year,
            monthName,
            [months[midDate.Month % 12], months[(midDate.Month + 1) % 12]],
            week,
            CalendarService.ShortLabel(others[0]),
            _service.Format(others[0], today),
            CalendarService.ShortLabel(others[1]),
            _service.Format(others[1], today));

        byte[] png = CalendarRenderer.RenderPng(card);
        var (stream, iconData) = await DockPulseHelpers.CreatePngIconDataAsync(png).ConfigureAwait(false);

        IContent[] next =
        [
            new ImageContent(new IconInfo(iconData, iconData))
            {
                MaxWidth = CalendarRenderer.Width,
                MaxHeight = CalendarRenderer.Height,
            },
            new CalendarControlsForm(offset == 0, HandleAction),
            new MarkdownContent(BuildSummary(today)),
        ];

        List<InMemoryRandomAccessStream> toDispose;
        lock (_stateLock)
        {
            toDispose = _retiringStreams;
            _retiringStreams = _activeStreams;
            _activeStreams = [stream];
            _cachedContent = next;
        }

        DockPulseHelpers.DisposeStreams(toDispose);

        Title = $"Calendar  •  {monthName} {midDate.Year}";
        Commands = BuildCommands(today);
        RaiseItemsChanged(next.Length);
    }

    private string BuildSummary(DateTime today)
    {
        int dayOfYear = today.DayOfYear;
        int daysInYear = DateTime.IsLeapYear(today.Year) ? 366 : 365;
        return
            $"**{CalendarService.LongDate(today)}**  \n" +
            $"{_service.Format(CalendarKind.Persian, today)}  •  {_service.Format(CalendarKind.Qamari, today)}  \n" +
            $"Week {CalendarService.IsoWeek(today)}  •  Day {dayOfYear} of {daysInYear}  •  {daysInYear - dayOfYear} days left this year";
    }

    private IContextItem[] BuildCommands(DateTime today) =>
    [
        new CommandContextItem(new ActionCommand("Previous Week", () => MoveWeeks(-1), new IconInfo("\uE76B"))),
        new CommandContextItem(new ActionCommand("Go to Today", () => MoveWeeks(0), new IconInfo("\uE8D1"))),
        new CommandContextItem(new ActionCommand("Next Week", () => MoveWeeks(1), new IconInfo("\uE76C"))),
        .. BuildMoreCommands(_settingsPage, today),
    ];

    internal static IContextItem[] BuildMoreCommands(CalendarSettingsPage settingsPage, DateTime today) =>
    [
        new CommandContextItem(new CopyTextCommand(CalendarService.IsoDate(today))
        {
            Name = $"Copy Date ({CalendarService.IsoDate(today)})",
            Icon = new IconInfo("\uE8C8"),
        }),
        new CommandContextItem(new CopyTextCommand(CalendarService.LongDate(today))
        {
            Name = "Copy Long Date",
            Icon = new IconInfo("\uE8C8"),
        }),
        new CommandContextItem(settingsPage)
        {
            Title = "Calendar Settings",
            Icon = new IconInfo("\uE713"),
        },
    ];

    private CommandResult HandleAction(string actionId)
    {
        switch (actionId)
        {
            case "prev":
                MoveWeeks(-1);
                break;
            case "next":
                MoveWeeks(1);
                break;
            case "today":
                MoveWeeks(0);
                break;
            case "settings":
                return CommandResult.GoToPage(new GoToPageArgs { PageId = _settingsPage.Id });
        }

        return CommandResult.KeepOpen();
    }

    public void Dispose()
    {
        _service.Changed -= RequestRefresh;

        List<InMemoryRandomAccessStream> active;
        List<InMemoryRandomAccessStream> retiring;
        lock (_stateLock)
        {
            active = _activeStreams;
            retiring = _retiringStreams;
            _activeStreams = [];
            _retiringStreams = [];
        }

        DockPulseHelpers.DisposeStreams(active);
        DockPulseHelpers.DisposeStreams(retiring);
    }

    private sealed partial class CalendarControlsForm : FormContent
    {
        private readonly Func<string, CommandResult> _onAction;

        public CalendarControlsForm(bool isThisWeek, Func<string, CommandResult> onAction)
        {
            _onAction = onAction;
            string todayStyle = isThisWeek ? ",\n                        \"style\": \"positive\"" : string.Empty;

            TemplateJson = $$"""
            {
                "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
                "type": "AdaptiveCard",
                "version": "1.5",
                "body": [],
                "actions": [
                    {
                        "type": "Action.Submit",
                        "title": "Previous",
                        "data": { "action": "prev" }
                    },
                    {
                        "type": "Action.Submit",
                        "title": "Today",
                        "data": { "action": "today" }{{todayStyle}}
                    },
                    {
                        "type": "Action.Submit",
                        "title": "Next",
                        "data": { "action": "next" }
                    },
                    {
                        "type": "Action.Submit",
                        "title": "Settings",
                        "data": { "action": "settings" }
                    }
                ]
            }
            """;
        }

        public override ICommandResult SubmitForm(string inputs, string data)
        {
            string? action = ExtractAction(data) ?? ExtractAction(inputs);
            return action is not null ? _onAction(action) : CommandResult.KeepOpen();
        }

        public override CommandResult SubmitForm(string payload)
        {
            string? action = ExtractAction(payload);
            return action is not null ? _onAction(action) : CommandResult.KeepOpen();
        }

        private static string? ExtractAction(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.String)
                {
                    return root.GetString();
                }

                if (DockPulseHelpers.TryReadString(root, "action", out var action) && !string.IsNullOrWhiteSpace(action))
                {
                    return action;
                }
            }
            catch
            {
                // Ignore malformed payloads
            }

            return null;
        }
    }
}

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

internal sealed partial class PomodoroStatsPage : ContentPage, IDisposable
{
    private readonly PomodoroService _service;
    private readonly PomodoroSettingsPage _settingsPage;
    private readonly object _stateLock = new();

    private IContent[] _cachedContent;
    private List<InMemoryRandomAccessStream> _activeStreams = [];
    private List<InMemoryRandomAccessStream> _retiringStreams = [];
    private int _refreshQueued;
    private int _isRefreshing;

    public PomodoroStatsPage(PomodoroService service, PomodoroSettingsPage settingsPage)
    {
        _service = service;
        _settingsPage = settingsPage;

        Id = "com.dockpulse.page.pomodoro.stats";
        Name = "Pomodoro Stats";
        Title = "Pomodoro — Stats";
        Icon = new IconInfo("\uE9D2");

        Commands =
        [
            new CommandContextItem(new ActionCommand(
                "Back to Timer",
                static () => { },
                new IconInfo("\uE916"),
                result: CommandResult.GoBack())),
            new CommandContextItem(_settingsPage)
            {
                Title = "Pomodoro Settings",
                Icon = new IconInfo("\uE713"),
            },
        ];

        var initial = _service.GetSnapshot();
        byte[] initialPng = PomodoroChartRenderer.RenderStatsChartPng(initial);
        string initialUri = $"data:image/png;base64,{Convert.ToBase64String(initialPng)}";
        _cachedContent = BuildContentArray(new MarkdownContent($"![Pomodoro Sessions]({initialUri})"), initial);

        _service.StateChanged += RequestRefresh;
        RequestRefresh();
    }

    public override IContent[] GetContent()
    {
        lock (_stateLock)
        {
            return _cachedContent;
        }
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
                await RefreshOnceAsync().ConfigureAwait(false);
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
        var snap = _service.GetSnapshot();
        byte[] png = PomodoroChartRenderer.RenderStatsChartPng(snap);
        var (stream, iconData) = await DockPulseHelpers.CreatePngIconDataAsync(png).ConfigureAwait(false);

        var chart = new ImageContent(new IconInfo(iconData, iconData))
        {
            MaxWidth = PomodoroChartRenderer.StatsWidth,
            MaxHeight = PomodoroChartRenderer.StatsHeight,
        };

        var nextContent = BuildContentArray(chart, snap);

        List<InMemoryRandomAccessStream> toDispose;
        lock (_stateLock)
        {
            toDispose = _retiringStreams;
            _retiringStreams = _activeStreams;
            _activeStreams = [stream];
            _cachedContent = nextContent;
        }

        DockPulseHelpers.DisposeStreams(toDispose);
        RaiseItemsChanged(nextContent.Length);
    }

    private IContent[] BuildContentArray(IContent chartVisual, PomodoroSnapshot snap)
    {
        int weekTotal = snap.Last7Days.Sum(d => d.Sessions);
        int todayMins = snap.TodaySessions * snap.FocusMinutes;
        int weekMins = weekTotal * snap.FocusMinutes;
        var best = snap.Last7Days.OrderByDescending(d => d.Sessions).First();
        double avg = weekTotal / (double)Math.Max(1, snap.Last7Days.Count);
        int avgMins = (int)Math.Round(weekMins / (double)Math.Max(1, snap.Last7Days.Count));

        string markdown = string.Create(
            CultureInfo.InvariantCulture,
            $"""
            ### Focus Summary
            | Period | Completed | Focus Time |
            | :--- | ---: | ---: |
            | **Today** | {snap.TodaySessions} sessions | {todayMins} min |
            | **This Week (7d)** | {weekTotal} sessions | {weekMins / 60.0:0.0} hrs |
            | **Daily Average** | {avg:0.0} sessions/day | {avgMins} min/day |
            | **Best Day** | {best.Label} ({best.Sessions} sessions) | {best.Sessions * snap.FocusMinutes} min |

            **Current Cycle:** {snap.PhaseSubtitle}  •  Focus {snap.FocusMinutes}m / Short {snap.ShortBreakMinutes}m / Long {snap.LongBreakMinutes}m
            """);

        return
        [
            chartVisual,
            new MarkdownContent(markdown),
            new StatsActionRowForm("Timer", () => CommandResult.GoBack()),
            new StatsActionRowForm("Settings", () => CommandResult.GoToPage(new GoToPageArgs { PageId = _settingsPage.Id })),
        ];
    }

    public void Dispose()
    {
        _service.StateChanged -= RequestRefresh;

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

    private sealed partial class StatsActionRowForm : FormContent
    {
        private readonly Func<CommandResult> _onSelect;

        public StatsActionRowForm(string title, Func<CommandResult> onSelect)
        {
            _onSelect = onSelect;
            string escTitle = JsonEncodedText.Encode(title).ToString();
            TemplateJson = $$"""
            {
                "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
                "type": "AdaptiveCard",
                "version": "1.5",
                "body": [],
                "actions": [
                    {
                        "type": "Action.Submit",
                        "title": "{{escTitle}}"
                    }
                ]
            }
            """;
        }

        public override CommandResult SubmitForm(string payload) => _onSelect();
    }
}

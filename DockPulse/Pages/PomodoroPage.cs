using System;
using System.Collections.Generic;
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

internal sealed partial class PomodoroPage : ContentPage, IDisposable
{
    private readonly PomodoroService _service;
    private readonly PomodoroStatsPage _statsPage;
    private readonly PomodoroSettingsPage _settingsPage;
    private readonly object _stateLock = new();

    private IContent[] _cachedContent;
    private List<InMemoryRandomAccessStream> _activeStreams = [];
    private List<InMemoryRandomAccessStream> _retiringStreams = [];
    private int _refreshQueued;
    private int _isRefreshing;

    public PomodoroPage(
        PomodoroService service,
        PomodoroStatsPage statsPage,
        PomodoroSettingsPage settingsPage)
    {
        _service = service;
        _statsPage = statsPage;
        _settingsPage = settingsPage;

        var initial = _service.GetSnapshot();

        Id = "com.dockpulse.page.pomodoro";
        Name = "Pomodoro";
        Title = FormatTitle(initial);
        Icon = new IconInfo("\uE916");

        Commands = BuildCommands(initial);

        byte[] initialPng = PomodoroChartRenderer.RenderTimerCardPng(initial);
        string initialUri = $"data:image/png;base64,{Convert.ToBase64String(initialPng)}";
        _cachedContent = BuildContentArray(new MarkdownContent($"![Pomodoro Timer]({initialUri})"), initial);

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
        byte[] png = PomodoroChartRenderer.RenderTimerCardPng(snap);
        var (stream, iconData) = await DockPulseHelpers.CreatePngIconDataAsync(png).ConfigureAwait(false);

        var timerImage = new ImageContent(new IconInfo(iconData, iconData))
        {
            MaxWidth = PomodoroChartRenderer.TimerWidth,
            MaxHeight = PomodoroChartRenderer.TimerHeight,
        };

        var nextContent = BuildContentArray(timerImage, snap);

        List<InMemoryRandomAccessStream> toDispose;
        lock (_stateLock)
        {
            toDispose = _retiringStreams;
            _retiringStreams = _activeStreams;
            _activeStreams = [stream];
            _cachedContent = nextContent;
        }

        DockPulseHelpers.DisposeStreams(toDispose);

        Title = FormatTitle(snap);
        Commands = BuildCommands(snap);
        RaiseItemsChanged(nextContent.Length);
    }

    private IContent[] BuildContentArray(IContent timerVisual, PomodoroSnapshot snap) =>
    [
        timerVisual,
        new PomodoroActionButtonForm(
            snap.IsRunning ? "Pause" : "Start",
            isPrimary: true,
            () =>
            {
                _service.ToggleStartPause();
                return CommandResult.KeepOpen();
            }),
        new PomodoroActionButtonForm(
            "+1 min",
            isPrimary: false,
            () =>
            {
                _service.AddOneMinute();
                DockPulseHelpers.ShowInlineToast("Added +1 min");
                return CommandResult.KeepOpen();
            }),
        new PomodoroActionButtonForm(
            "Skip",
            isPrimary: false,
            () =>
            {
                var next = _service.SkipPhase();
                DockPulseHelpers.ShowInlineToast($"Skipped to {next.PhaseSubtitle}");
                return CommandResult.KeepOpen();
            }),
        new PomodoroActionButtonForm(
            "Reset",
            isPrimary: false,
            () =>
            {
                _service.Reset();
                DockPulseHelpers.ShowInlineToast("Timer reset");
                return CommandResult.KeepOpen();
            }),
        new PomodoroActionButtonForm(
            "Stats",
            isPrimary: false,
            () => CommandResult.GoToPage(new GoToPageArgs { PageId = _statsPage.Id })),
        new PomodoroActionButtonForm(
            "Settings",
            isPrimary: false,
            () => CommandResult.GoToPage(new GoToPageArgs { PageId = _settingsPage.Id })),
    ];

    private IContextItem[] BuildCommands(PomodoroSnapshot snap) =>
    [
        new CommandContextItem(new ActionCommand(
            snap.IsRunning ? "Pause Timer" : "Start Timer",
            () => _service.ToggleStartPause(),
            new IconInfo(snap.IsRunning ? "\uE769" : "\uE768"))),
        new CommandContextItem(new ActionCommand(
            "+1 min",
            () =>
            {
                _service.AddOneMinute();
                DockPulseHelpers.ShowInlineToast("Added +1 min");
            },
            new IconInfo("\uE710"))),
        new CommandContextItem(new ActionCommand(
            "Skip Phase",
            () =>
            {
                var next = _service.SkipPhase();
                DockPulseHelpers.ShowInlineToast($"Skipped to {next.PhaseSubtitle}");
            },
            new IconInfo("\uE893"))),
        new CommandContextItem(new ActionCommand(
            "Reset Timer",
            () =>
            {
                _service.Reset();
                DockPulseHelpers.ShowInlineToast("Timer reset");
            },
            new IconInfo("\uE72C"))),
        new CommandContextItem(_statsPage)
        {
            Title = "Stats (Today & This Week)",
            Icon = new IconInfo("\uE9D2"),
        },
        new CommandContextItem(_settingsPage)
        {
            Title = "Pomodoro Settings",
            Icon = new IconInfo("\uE713"),
        },
    ];

    private static string FormatTitle(PomodoroSnapshot snap) =>
        $"Pomodoro  •  {snap.PhaseSubtitle}";

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

    private sealed partial class PomodoroActionButtonForm : FormContent
    {
        private readonly Func<CommandResult> _onSelect;

        public PomodoroActionButtonForm(string title, bool isPrimary, Func<CommandResult> onSelect)
        {
            _onSelect = onSelect;
            string escTitle = JsonEncodedText.Encode(title).ToString();
            string styleProp = isPrimary ? ",\n                        \"style\": \"positive\"" : string.Empty;

            TemplateJson = $$"""
            {
                "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
                "type": "AdaptiveCard",
                "version": "1.5",
                "body": [],
                "actions": [
                    {
                        "type": "Action.Submit",
                        "title": "{{escTitle}}"{{styleProp}}
                    }
                ]
            }
            """;
        }

        public override CommandResult SubmitForm(string payload) => _onSelect();
    }
}

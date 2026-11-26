using System;
using DockPulse.Commands;
using DockPulse.Helpers;
using DockPulse.Pages;
using DockPulse.Services;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace DockPulse.DockBands;

internal sealed partial class PomodoroDockBandItem : ListItem, IDisposable
{
    private readonly PomodoroService _service;
    private readonly PomodoroStatsPage _statsPage;
    private readonly PomodoroSettingsPage _settingsPage;
    private bool? _lastIsRunning;

    public PomodoroDockBandItem(
        PomodoroService service,
        PomodoroPage page,
        PomodoroStatsPage statsPage,
        PomodoroSettingsPage settingsPage)
        : base(page)
    {
        _service = service;
        _statsPage = statsPage;
        _settingsPage = settingsPage;

        Icon = new IconInfo("\uE916");
        _service.StateChanged += Refresh;
        Refresh();
    }

    public void Refresh()
    {
        var snap = _service.GetSnapshot();
        Title = PomodoroService.FormatMmSs(snap.RemainingSeconds);
        Subtitle = snap.FormatDockSubtitle();

        // Refresh runs every second while the timer ticks; only rebuild the menu when Start/Pause flips,
        // so Command Palette is not asked to re-read the context menu 60 times a minute.
        if (_lastIsRunning != snap.IsRunning)
        {
            _lastIsRunning = snap.IsRunning;
            MoreCommands = BuildMoreCommands(snap.IsRunning);
        }
    }

    internal IContextItem[] BuildMoreCommands(bool isRunning) =>
    [
        new CommandContextItem(new ActionCommand(
            isRunning ? "Pause Timer" : "Start Timer",
            () => _service.ToggleStartPause(),
            new IconInfo(isRunning ? "\uE769" : "\uE768"))),
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
            Title = "Pomodoro Stats",
            Icon = new IconInfo("\uE9D2"),
        },
        new CommandContextItem(_settingsPage)
        {
            Title = "Pomodoro Settings",
            Icon = new IconInfo("\uE713"),
        },
    ];

    public void Dispose()
    {
        _service.StateChanged -= Refresh;
    }
}

internal sealed partial class PomodoroToggleDockItem : ListItem, IDisposable
{
    private readonly PomodoroService _service;
    private readonly PomodoroDockBandItem _mainBandItem;
    private bool? _lastIsRunning;

    public PomodoroToggleDockItem(PomodoroService service, PomodoroDockBandItem mainBandItem)
        : base(new ActionCommand(
            "Start Pomodoro",
            () => service.ToggleStartPause(),
            new IconInfo("\uE768"),
            id: "com.dockpulse.band.pomodoro.toggle",
            keepOpen: true))
    {
        _service = service;
        _mainBandItem = mainBandItem;

        Title = string.Empty;
        Subtitle = string.Empty;
        _service.StateChanged += Refresh;
        Refresh();
    }

    public void Refresh()
    {
        var snap = _service.GetSnapshot();
        if (_lastIsRunning == snap.IsRunning)
        {
            return;
        }

        _lastIsRunning = snap.IsRunning;
        var icon = new IconInfo(snap.IsRunning ? "\uE769" : "\uE768");
        Command = new ActionCommand(
            snap.IsRunning ? "Pause Pomodoro" : "Start Pomodoro",
            () => _service.ToggleStartPause(),
            icon,
            id: "com.dockpulse.band.pomodoro.toggle",
            keepOpen: true);
        Icon = icon;
        MoreCommands = _mainBandItem.BuildMoreCommands(snap.IsRunning);
    }

    public void Dispose()
    {
        _service.StateChanged -= Refresh;
    }
}

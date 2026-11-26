using System;
using System.IO;
using System.Threading;
using DockPulse.Commands;
using DockPulse.Helpers;
using DockPulse.Pages;
using DockPulse.Services;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace DockPulse.DockBands;

/// <summary>One click opens the working folder in File Explorer; if it's gone, the click opens Change Folder.</summary>
internal sealed partial class WorkingFolderDockBandItem : ListItem, IDisposable
{
    private readonly WorkingFolderService _service;
    private readonly WorkingFolderSettingsPage _settingsPage;
    private readonly OpenWorkingFolderCommand _openCommand;
    private readonly Timer _timer;
    private readonly object _refreshLock = new();
    private string? _lastPath;
    private bool? _lastExists;

    public WorkingFolderDockBandItem(
        WorkingFolderService service,
        WorkingFolderSettingsPage settingsPage,
        OpenWorkingFolderCommand openCommand)
        : base(openCommand)
    {
        _service = service;
        _settingsPage = settingsPage;
        _openCommand = openCommand;

        _service.Changed += Refresh;
        Refresh();

        // Notice when the folder is deleted, renamed or its drive is unplugged
        _timer = new Timer(_ => Refresh(), null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
    }

    public void Refresh()
    {
        lock (_refreshLock)
        {
            string path = _service.FolderPath;
            bool exists = Directory.Exists(path);
            if (string.Equals(path, _lastPath, StringComparison.OrdinalIgnoreCase) && exists == _lastExists)
            {
                return;
            }

            _lastPath = path;
            _lastExists = exists;

            if (exists)
            {
                Command = _openCommand;
                Icon = new IconInfo("");
                Title = WorkingFolderService.GetDisplayName(path);
                Subtitle = WorkingFolderService.GetParentDisplay(path);
            }
            else
            {
                Command = _settingsPage;
                Icon = new IconInfo("");
                Title = "Folder missing";
                Subtitle = path;
            }

            MoreCommands = BuildMoreCommands(path);
        }
    }

    private IContextItem[] BuildMoreCommands(string path) =>
    [
        new CommandContextItem(new ActionCommand(
            "Open in VS Code",
            () =>
            {
                if (!WorkingFolderService.OpenInVsCode(path))
                {
                    DockPulseHelpers.ShowInlineToast("VS Code not found, or the folder is missing.");
                }
            },
            new IconInfo(""),
            keepOpen: false)),
        new CommandContextItem(new ActionCommand(
            "Open in Terminal",
            () =>
            {
                if (!WorkingFolderService.OpenInTerminal(path))
                {
                    DockPulseHelpers.ShowInlineToast($"Folder not found: {path}");
                }
            },
            new IconInfo(""),
            keepOpen: false)),
        new CommandContextItem(new CopyTextCommand(path)
        {
            Name = "Copy Path",
            Icon = new IconInfo(""),
        }),
        new CommandContextItem(_settingsPage)
        {
            Title = "Change Folder...",
            Icon = new IconInfo(""),
        },
    ];

    public void Dispose()
    {
        _service.Changed -= Refresh;
        _timer.Dispose();
    }
}

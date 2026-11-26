using DockPulse.Helpers;
using DockPulse.Services;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace DockPulse.Commands;

/// <summary>Opens the current working folder in File Explorer (reads the folder when invoked, so it follows changes).</summary>
internal sealed partial class OpenWorkingFolderCommand : InvokableCommand
{
    private readonly WorkingFolderService _service;

    public OpenWorkingFolderCommand(WorkingFolderService service)
    {
        _service = service;
        Name = "Open in File Explorer";
        Icon = new IconInfo("");
        Id = "com.dockpulse.command.workfolder.open";
    }

    public override ICommandResult Invoke()
    {
        string path = _service.FolderPath;
        if (WorkingFolderService.OpenInExplorer(path))
        {
            return CommandResult.Dismiss();
        }

        DockPulseHelpers.ShowInlineToast($"Folder not found: {path}");
        return CommandResult.KeepOpen();
    }
}

using System;
using System.Diagnostics;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace DockPulse.Commands;

internal sealed partial class ActionCommand : InvokableCommand
{
    private readonly Action _action;
    private readonly bool _keepOpen;

    public ActionCommand(string name, Action action, IconInfo? icon = null, string? id = null, bool keepOpen = true)
    {
        Name = name;
        _action = action;
        _keepOpen = keepOpen;

        if (icon is not null)
        {
            Icon = icon;
        }

        if (!string.IsNullOrWhiteSpace(id))
        {
            Id = id;
        }
    }

    public override ICommandResult Invoke()
    {
        _action();
        return _keepOpen ? CommandResult.KeepOpen() : CommandResult.Dismiss();
    }
}

internal sealed partial class OpenUriCommand : InvokableCommand
{
    private readonly string _uri;

    public OpenUriCommand(string name, string uri, IconInfo? icon = null, string? id = null)
    {
        Name = name;
        _uri = uri;

        if (icon is not null)
        {
            Icon = icon;
        }

        if (!string.IsNullOrWhiteSpace(id))
        {
            Id = id;
        }
    }

    public override ICommandResult Invoke()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_uri) { UseShellExecute = true });
        }
        catch
        {
            // Ignore shell launch errors
        }

        return CommandResult.Dismiss();
    }
}
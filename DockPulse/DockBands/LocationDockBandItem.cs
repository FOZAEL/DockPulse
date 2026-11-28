using System;
using DockPulse.Pages;
using DockPulse.Services;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace DockPulse.DockBands;

/// <summary>Shows the city and country of the public IP (the VPN's exit when a VPN is on).</summary>
internal sealed partial class LocationDockBandItem : ListItem, IDisposable
{
    private readonly LocationService _service;

    public LocationDockBandItem(LocationService service, LocationPage page)
        : base(page)
    {
        _service = service;
        Icon = new IconInfo("\uE81D");
        Title = "Locating...";
        Subtitle = "Public IP";
        MoreCommands = LocationPage.BuildMoreCommands(_service, null);

        _service.Changed += Refresh;
        Refresh();
    }

    // Command Palette reads the title when it shows the band, so the first lookup waits until then
    public override string Title
    {
        get
        {
            // The base constructor can read Title before _service is set
            if (_service is not null)
            {
                _service.EnsureStarted();
            }

            return base.Title;
        }

        set => base.Title = value;
    }

    public void Refresh()
    {
        var snapshot = _service.GetSnapshot();
        if (snapshot is null)
        {
            return;
        }

        var loc = snapshot.Location;
        if (loc is null)
        {
            Title = "Offline";
            Subtitle = "No internet";
        }
        else
        {
            Title = LocationService.FormatShortPlace(loc);
            Subtitle = snapshot.Adapter?.IsVpn == true ? $"VPN • {loc.Ip}" : loc.Ip;
        }

        MoreCommands = LocationPage.BuildMoreCommands(_service, loc);
    }

    public void Dispose()
    {
        _service.Changed -= Refresh;
    }
}

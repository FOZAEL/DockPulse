using System;
using System.Collections.Generic;
using System.Globalization;
using DockPulse.Commands;
using DockPulse.Helpers;
using DockPulse.Services;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace DockPulse.Pages;

internal sealed partial class LocationPage : ListPage, IDisposable
{
    private readonly LocationService _service;

    public LocationPage(LocationService service)
    {
        _service = service;

        Id = "com.dockpulse.page.location";
        Name = "My Location";
        Title = "My Location (Public IP)";
        Icon = new IconInfo("\uE81D");
        PlaceholderText = "Filter location details...";

        _service.Changed += OnChanged;
    }

    public override IListItem[] GetItems()
    {
        _service.EnsureStarted();
        var snapshot = _service.GetSnapshot();
        if (snapshot is null)
        {
            return
            [
                new ListItem(RefreshCommand())
                {
                    Title = "Looking up your location...",
                    Subtitle = "Asking " + LocationService.SourceName + " for the location of your public IP",
                    Icon = new IconInfo("\uE81D"),
                },
                SourceItem(),
            ];
        }

        var loc = snapshot.Location;
        if (loc is null)
        {
            return
            [
                new ListItem(RefreshCommand())
                {
                    Title = "You're offline",
                    Subtitle = "DockPulse will look up your location again when you're back online",
                    Icon = new IconInfo("\uE7BA"),
                    Tags = [DockPulseHelpers.CreateTag("Offline", TagTone.Danger)],
                    MoreCommands = BuildMoreCommands(_service, null),
                },
                new ListItem(new OpenUriCommand("Open Network Settings", "ms-settings:network", new IconInfo("\uE713")))
                {
                    Title = "Open Network & Internet Settings",
                    Subtitle = "Check your connection in Windows Settings",
                    Icon = new IconInfo("\uE713"),
                },
                SourceItem(),
            ];
        }

        var more = BuildMoreCommands(_service, loc);
        bool isVpn = snapshot.Adapter?.IsVpn == true;

        var placeTags = new List<ITag>();
        if (!string.IsNullOrWhiteSpace(loc.CountryCode))
        {
            placeTags.Add(new Tag(loc.CountryCode));
        }

        if (isVpn)
        {
            placeTags.Add(DockPulseHelpers.CreateTag("VPN", TagTone.Accent));
        }

        var items = new List<IListItem>
        {
            new ListItem(new OpenUriCommand("Open in Maps", LocationService.GetMapsUrl(loc), new IconInfo("\uE707")))
            {
                Title = LocationService.FormatPlace(loc),
                Subtitle = "Approximate location of your public IP",
                Icon = new IconInfo("\uE81D"),
                Tags = [.. placeTags],
                MoreCommands = more,
            },
            new ListItem(new CopyTextCommand(loc.Ip) { Name = "Copy Public IP", Icon = new IconInfo("\uE8C8") })
            {
                Title = $"Public IP: {loc.Ip}",
                Subtitle = string.IsNullOrWhiteSpace(loc.Asn) ? loc.AsnOrganization : $"{loc.AsnOrganization} • {loc.Asn}",
                Icon = new IconInfo("\uE774"),
                Tags = [new Tag(loc.IpVersion == 6 ? "IPv6" : "IPv4")],
                MoreCommands = more,
            },
        };

        if (snapshot.Adapter is { } adapter)
        {
            items.Add(new ListItem(new CopyTextCommand(adapter.LocalIp) { Name = "Copy Local IP", Icon = new IconInfo("\uE8C8") })
            {
                Title = $"Active Adapter: {adapter.Name}",
                Subtitle = $"Local IP {adapter.LocalIp}",
                Icon = new IconInfo(adapter.IsVpn ? "\uE705" : adapter.IsWireless ? "\uE701" : "\uE839"),
                Tags = adapter.IsVpn ? [DockPulseHelpers.CreateTag("VPN", TagTone.Accent)] : [],
            });
        }

        if (!string.IsNullOrWhiteSpace(loc.TimeZoneId))
        {
            string offset = LocationService.FormatUtcOffset(loc.TimeZoneId);
            items.Add(new ListItem(new CopyTextCommand(loc.TimeZoneId) { Name = "Copy Time Zone", Icon = new IconInfo("\uE8C8") })
            {
                Title = $"Time Zone: {loc.TimeZoneId}",
                Subtitle = string.IsNullOrEmpty(offset) ? "Time zone at this location" : $"{offset} at this location",
                Icon = new IconInfo("\uE917"),
            });
        }

        items.Add(new ListItem(RefreshCommand())
        {
            Title = "Refresh Location",
            Subtitle = $"Last checked at {snapshot.CheckedAt.ToString("HH:mm", CultureInfo.InvariantCulture)} • updates when your network changes",
            Icon = new IconInfo("\uE72C"),
        });

        items.Add(SourceItem());
        return [.. items];
    }

    internal static IContextItem[] BuildMoreCommands(LocationService service, PublicIpLocation? loc)
    {
        var refresh = new CommandContextItem(new ActionCommand(
            "Refresh Location",
            () => _ = service.RefreshAsync(),
            new IconInfo("\uE72C")));

        if (loc is null)
        {
            return [refresh];
        }

        return
        [
            new CommandContextItem(new CopyTextCommand(loc.Ip) { Name = "Copy Public IP", Icon = new IconInfo("\uE8C8") }),
            new CommandContextItem(new OpenUriCommand("Open in Maps", LocationService.GetMapsUrl(loc), new IconInfo("\uE707"))),
            refresh,
        ];
    }

    private ActionCommand RefreshCommand() =>
        new("Refresh Now", () => _ = _service.RefreshAsync(), new IconInfo("\uE72C"));

    private static ListItem SourceItem() =>
        new(new OpenUriCommand("Open " + LocationService.SourceName, LocationService.SourceUrl, new IconInfo("\uE774")))
        {
            Title = "Looked up with " + LocationService.SourceName,
            Subtitle = "DockPulse asks once when your network changes, and at most every 30 minutes",
            Icon = new IconInfo("\uE72E"),
        };

    private void OnChanged() => RaiseItemsChanged();

    public void Dispose()
    {
        _service.Changed -= OnChanged;
    }
}

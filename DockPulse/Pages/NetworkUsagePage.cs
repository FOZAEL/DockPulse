using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DockPulse.Commands;
using DockPulse.Services;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace DockPulse.Pages;

internal sealed partial class NetworkUsagePage : ListPage
{
    private readonly NetworkUsageService _service;
    private NetworkUsageSnapshot? _snapshot;

    public Action? OnManualRefresh { get; set; }

    public NetworkUsagePage(NetworkUsageService service)
    {
        _service = service;

        Id = "com.dockpulse.page.network";
        Name = "Network Usage";
        Title = "Network Data Usage (24h & This Month)";
        Icon = new IconInfo("\uE774");
        PlaceholderText = "Filter network adapters...";
    }

    public void UpdateSnapshot(NetworkUsageSnapshot snapshot)
    {
        _snapshot = snapshot;
        RaiseItemsChanged();
    }

    public override IListItem[] GetItems()
    {
        var items = new List<IListItem>();

        if (_snapshot is null)
        {
            items.Add(new ListItem(new ActionCommand("Refresh Now", TriggerRefresh, new IconInfo("\uE72C")))
            {
                Title = "Loading network usage data...",
                Subtitle = "Reading Windows Data Usage (SRUM) statistics",
                Icon = new IconInfo("\uE774"),
            });
            return [.. items];
        }

        items.Add(new ListItem(new OpenUriCommand("Open Data Usage Settings", "ms-settings:datausage", new IconInfo("\uE774")))
        {
            Title = $"Last 24 Hours: {NetworkUsageService.FormatBytes(_snapshot.Total24h)}",
            Subtitle = $"↓ {NetworkUsageService.FormatBytes(_snapshot.TotalReceived24h)}   •   ↑ {NetworkUsageService.FormatBytes(_snapshot.TotalSent24h)}",
            Icon = new IconInfo("\uE88A"),
            Tags = [new Tag("24h")],
        });

        items.Add(new ListItem(new OpenUriCommand("Open Data Usage Settings", "ms-settings:datausage", new IconInfo("\uE774")))
        {
            Title = $"This Month ({_snapshot.MonthStart:MMM yyyy}): {NetworkUsageService.FormatBytes(_snapshot.TotalMonth)}",
            Subtitle = $"↓ {NetworkUsageService.FormatBytes(_snapshot.TotalReceivedMonth)}   •   ↑ {NetworkUsageService.FormatBytes(_snapshot.TotalSentMonth)}",
            Icon = new IconInfo("\uE774"),
            Tags = [new Tag("Month")],
        });

        foreach (var adapter in _snapshot.Adapters)
        {
            var tags = new List<ITag>
            {
                new Tag($"24h: {NetworkUsageService.FormatBytes(adapter.Total24h)}"),
                new Tag($"Mo: {NetworkUsageService.FormatBytes(adapter.TotalMonth)}"),
            };

            if (adapter.IsActiveInternet)
            {
                tags.Insert(0, new Tag("Active"));
            }

            items.Add(new ListItem(new OpenUriCommand("Open Data Usage Settings", "ms-settings:datausage", new IconInfo("\uE839")))
            {
                Title = adapter.ProfileName,
                Subtitle = $"24h ↓{NetworkUsageService.FormatBytes(adapter.Received24h)} ↑{NetworkUsageService.FormatBytes(adapter.Sent24h)}  |  Month ↓{NetworkUsageService.FormatBytes(adapter.ReceivedMonth)} ↑{NetworkUsageService.FormatBytes(adapter.SentMonth)}",
                Icon = new IconInfo(adapter.IsActiveInternet ? "\uE701" : "\uE839"),
                Tags = [.. tags],
            });
        }

        items.Add(new ListItem(new ActionCommand("Refresh Now", TriggerRefresh, new IconInfo("\uE72C")))
        {
            Title = "Refresh Network Usage",
            Subtitle = $"Last updated at {_snapshot.UpdatedAt:HH:mm:ss}",
            Icon = new IconInfo("\uE72C"),
        });

        items.Add(new ListItem(new OpenUriCommand("Open Windows Data Usage", "ms-settings:datausage", new IconInfo("\uE713")))
        {
            Title = "Open Windows Data Usage Settings",
            Subtitle = "View per-app usage breakdown in Windows Settings",
            Icon = new IconInfo("\uE713"),
        });

        return [.. items];
    }

    private void TriggerRefresh()
    {
        if (OnManualRefresh is not null)
        {
            OnManualRefresh();
            return;
        }

        _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            var latest = await _service.GetUsageAsync().ConfigureAwait(false);
            UpdateSnapshot(latest);
        }
        finally
        {
            IsLoading = false;
        }
    }
}
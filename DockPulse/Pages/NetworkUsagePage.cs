using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using DockPulse.Commands;
using DockPulse.Helpers;
using DockPulse.Services;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace DockPulse.Pages;

internal sealed partial class NetworkUsagePage : ListPage, IDisposable
{
    private readonly NetworkUsageService _service;
    private readonly NetworkLiveService _liveService;
    private readonly NetworkDashboardPage _dashboardPage;
    private readonly object _itemsLock = new();
    private readonly ListItem _liveItem;
    private IListItem[] _cachedItems;

    public Action? OnManualRefresh { get; set; }

    public NetworkUsagePage(
        NetworkUsageService service,
        NetworkLiveService liveService,
        NetworkDashboardPage dashboardPage)
    {
        _service = service;
        _liveService = liveService;
        _dashboardPage = dashboardPage;

        Id = "com.dockpulse.page.network";
        Name = "Network Usage";
        Title = "Network Data Usage (Today & This Month)";
        Icon = new IconInfo("\uE774");
        PlaceholderText = "Filter network adapters...";
        ShowDetails = true;

        _liveItem = new ListItem(_dashboardPage)
        {
            Icon = new IconInfo("\uE9D9"),
            Subtitle = "Last 60 seconds  •  updates every 2 s",
            Tags = [DockPulseHelpers.CreateTag("Live", TagTone.Accent)],
            MoreCommands =
            [
                new CommandContextItem(new ActionCommand("Refresh Now", TriggerRefresh, new IconInfo("\uE72C"))),
                new CommandContextItem(new OpenUriCommand("Open Data Usage Settings", "ms-settings:datausage", new IconInfo("\uE774"))),
            ],
        };
        UpdateLiveItem(_liveService.GetStats());

        _cachedItems =
        [
            _liveItem,
            new ListItem(new ActionCommand("Refresh Now", TriggerRefresh, new IconInfo("\uE72C")))
            {
                Title = "Loading network usage data...",
                Subtitle = "Reading Windows Data Usage (SRUM) statistics",
                Icon = new IconInfo("\uE774"),
            },
        ];

        _liveService.Sampled += OnLiveSampled;
    }

    public void UpdateSnapshot(NetworkUsageSnapshot snapshot)
    {
        _dashboardPage.UpdateSnapshot(snapshot);
        var built = BuildItems(snapshot);
        lock (_itemsLock)
        {
            _cachedItems = built;
        }

        RaiseItemsChanged(built.Length);
    }

    public override IListItem[] GetItems()
    {
        _liveService.Touch();
        lock (_itemsLock)
        {
            return _cachedItems;
        }
    }

    private void OnLiveSampled()
    {
        UpdateLiveItem(_liveService.GetStats());

        int count;
        lock (_itemsLock)
        {
            count = _cachedItems.Length;
        }

        RaiseItemsChanged(count);
    }

    private void UpdateLiveItem(NetworkLiveStats live)
    {
        string down = NetworkLiveService.FormatBps(live.Current.DownBps);
        string up = NetworkLiveService.FormatBps(live.Current.UpBps);
        string peakDown = NetworkLiveService.FormatBps(live.PeakDownBps);
        string peakUp = NetworkLiveService.FormatBps(live.PeakUpBps);

        string chartUri = NetworkLiveChartRenderer.RenderLiveChartDataUri(
            live,
            width: NetworkLiveChartRenderer.DetailsWidth,
            height: NetworkLiveChartRenderer.DetailsHeight);

        _liveItem.Title = $"Live Speed: ↓ {down} • ↑ {up}";
        _liveItem.Details = new Details
        {
            Title = "Live Speed",
            Body =
                $"![Live Speed]({chartUri})\n\n" +
                $"**Download:** {down}  •  **Upload:** {up}  \n" +
                $"**Peak (60 s):** ↓ {peakDown}  •  ↑ {peakUp}  \n" +
                "_Sampled every 2 s while this page is open_",
        };
    }

    private IListItem[] BuildItems(NetworkUsageSnapshot snapshot)
    {
        // Details-pane charts are Markdown images: Command Palette shows them at natural size and clips
        // anything wider than the pane (about 345 px at its default size), so keep them narrower than that.
        const int DetailsChartWidth = 320;

        var items = new List<IListItem> { _liveItem };

        string peakHour = NetworkUsageService.FormatPeakHour(snapshot.HourlyToday);
        string peakDay = NetworkUsageService.FormatPeakDay(snapshot.DailyMonth, snapshot.MonthStart);
        ulong dailyAvg = snapshot.TotalMonth / (ulong)Math.Max(1, snapshot.CurrentDay);
        string monthLabel = snapshot.MonthStart.ToString("MMM yyyy", CultureInfo.InvariantCulture);

        string todayChartUri = NetworkChartRenderer.RenderBarChartDataUri(
            title: "Today Hourly",
            subtitle: $"SINCE 00:00  |  PEAK: {peakHour.ToUpperInvariant()}",
            buckets: snapshot.HourlyToday,
            activeBucketIndex: snapshot.CurrentHour,
            isDarkTheme: true,
            width: DetailsChartWidth,
            height: 185);

        string monthChartUri = NetworkChartRenderer.RenderBarChartDataUri(
            title: $"{monthLabel} Daily",
            subtitle: $"PEAK: {peakDay.ToUpperInvariant()}  |  AVG: {NetworkUsageService.FormatBytes(dailyAvg)}/DAY",
            buckets: snapshot.DailyMonth,
            activeBucketIndex: snapshot.CurrentDay - 1,
            isDarkTheme: true,
            width: DetailsChartWidth,
            height: 185);

        var todayDetails = new Details
        {
            Title = "Today (Hourly Usage)",
            Body =
                $"![Today Hourly Usage]({todayChartUri})\n\n" +
                $"**Total:** {NetworkUsageService.FormatBytes(snapshot.TotalToday)}  \n" +
                $"**Download:** {NetworkUsageService.FormatBytes(snapshot.TotalReceivedToday)}  •  " +
                $"**Upload:** {NetworkUsageService.FormatBytes(snapshot.TotalSentToday)}  \n" +
                $"**Peak Hour:** {peakHour}  \n" +
                $"_Updated at {snapshot.UpdatedAt.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}_",
        };

        var monthDetails = new Details
        {
            Title = $"{monthLabel} (Daily Usage)",
            Body =
                $"![Monthly Daily Usage]({monthChartUri})\n\n" +
                $"**Total:** {NetworkUsageService.FormatBytes(snapshot.TotalMonth)}  \n" +
                $"**Download:** {NetworkUsageService.FormatBytes(snapshot.TotalReceivedMonth)}  •  " +
                $"**Upload:** {NetworkUsageService.FormatBytes(snapshot.TotalSentMonth)}  \n" +
                $"**Daily Average:** {NetworkUsageService.FormatBytes(dailyAvg)}  •  " +
                $"**Peak Day:** {peakDay}",
        };

        items.Add(new ListItem(new OpenUriCommand("Open Data Usage Settings", "ms-settings:datausage", new IconInfo("\uE774")))
        {
            Title = $"Today (since 00:00): {NetworkUsageService.FormatBytes(snapshot.TotalToday)}",
            Subtitle = $"↓ {NetworkUsageService.FormatBytes(snapshot.TotalReceivedToday)}   •   ↑ {NetworkUsageService.FormatBytes(snapshot.TotalSentToday)}",
            Icon = new IconInfo("\uE88A"),
            Tags = [new Tag("Today")],
            Details = todayDetails,
            MoreCommands =
            [
                new CommandContextItem(new ActionCommand("Refresh Now", TriggerRefresh, new IconInfo("\uE72C"))),
            ],
        });

        items.Add(new ListItem(new OpenUriCommand("Open Data Usage Settings", "ms-settings:datausage", new IconInfo("\uE774")))
        {
            Title = $"This Month ({monthLabel}): {NetworkUsageService.FormatBytes(snapshot.TotalMonth)}",
            Subtitle = $"↓ {NetworkUsageService.FormatBytes(snapshot.TotalReceivedMonth)}   •   ↑ {NetworkUsageService.FormatBytes(snapshot.TotalSentMonth)}",
            Icon = new IconInfo("\uE774"),
            Tags = [new Tag("Month")],
            Details = monthDetails,
            MoreCommands =
            [
                new CommandContextItem(new ActionCommand("Refresh Now", TriggerRefresh, new IconInfo("\uE72C"))),
            ],
        });

        foreach (var adapter in snapshot.Adapters)
        {
            var tags = new List<ITag>
            {
                new Tag($"Today: {NetworkUsageService.FormatBytes(adapter.TotalToday)}"),
                new Tag($"Mo: {NetworkUsageService.FormatBytes(adapter.TotalMonth)}"),
            };

            if (adapter.IsActiveInternet)
            {
                tags.Insert(0, new Tag("Active"));
            }

            string adapterPeakDay = NetworkUsageService.FormatPeakDay(adapter.DailyMonth, snapshot.MonthStart);
            string adapterChartUri = NetworkChartRenderer.RenderBarChartDataUri(
                title: $"{TruncateChartTitle(adapter.ProfileName, 14)} ({monthLabel})",
                subtitle: $"TODAY: {NetworkUsageService.FormatBytes(adapter.TotalToday)}  |  PEAK: {adapterPeakDay.ToUpperInvariant()}",
                buckets: adapter.DailyMonth,
                activeBucketIndex: snapshot.CurrentDay - 1,
                isDarkTheme: true,
                width: DetailsChartWidth,
                height: 185);

            items.Add(new ListItem(new OpenUriCommand("Open Data Usage Settings", "ms-settings:datausage", new IconInfo("\uE774")))
            {
                Title = adapter.ProfileName,
                Subtitle = $"Today ↓{NetworkUsageService.FormatBytes(adapter.ReceivedToday)} ↑{NetworkUsageService.FormatBytes(adapter.SentToday)}  |  Month ↓{NetworkUsageService.FormatBytes(adapter.ReceivedMonth)} ↑{NetworkUsageService.FormatBytes(adapter.SentMonth)}",
                Icon = new IconInfo(adapter.IsActiveInternet ? "\uE701" : "\uE839"),
                Tags = [.. tags],
                Details = new Details
                {
                    Title = adapter.ProfileName,
                    Body =
                        $"![{adapter.ProfileName} Monthly Usage]({adapterChartUri})\n\n" +
                        $"**Today:** {NetworkUsageService.FormatBytes(adapter.TotalToday)} " +
                        $"(↓ {NetworkUsageService.FormatBytes(adapter.ReceivedToday)} • ↑ {NetworkUsageService.FormatBytes(adapter.SentToday)})  \n" +
                        $"**This Month:** {NetworkUsageService.FormatBytes(adapter.TotalMonth)} " +
                        $"(↓ {NetworkUsageService.FormatBytes(adapter.ReceivedMonth)} • ↑ {NetworkUsageService.FormatBytes(adapter.SentMonth)})  \n" +
                        $"**Peak Day:** {adapterPeakDay}",
                },
                MoreCommands =
                [
                    new CommandContextItem(new ActionCommand("Refresh Now", TriggerRefresh, new IconInfo("\uE72C"))),
                ],
            });
        }

        items.Add(new ListItem(new ActionCommand("Refresh Now", TriggerRefresh, new IconInfo("\uE72C")))
        {
            Title = "Refresh Network Usage",
            Subtitle = $"Last updated at {snapshot.UpdatedAt.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}",
            Icon = new IconInfo("\uE72C"),
            Details = todayDetails,
        });

        items.Add(new ListItem(new OpenUriCommand("Open Windows Data Usage", "ms-settings:datausage", new IconInfo("\uE713")))
        {
            Title = "Open Windows Data Usage Settings",
            Subtitle = "View per-app usage breakdown in Windows Settings",
            Icon = new IconInfo("\uE713"),
            Details = monthDetails,
        });

        return [.. items];
    }

    private static string TruncateChartTitle(string value, int maxLen) =>
        value.Length <= maxLen ? value : value[..maxLen].TrimEnd();

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

    public void Dispose()
    {
        _liveService.Sampled -= OnLiveSampled;
    }
}

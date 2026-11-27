using System;
using System.Collections.Generic;
using System.Globalization;
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

internal sealed partial class NetworkDashboardPage : ContentPage, IDisposable
{
    private enum DashboardRange
    {
        Live,
        Today,
        Month,
    }

    private readonly NetworkLiveService _liveService;
    private readonly object _stateLock = new();

    private DashboardRange _range = DashboardRange.Live;
    private NetworkUsageSnapshot? _lastSnapshot;
    private IContent[] _cachedContent;
    private List<InMemoryRandomAccessStream> _activeStreams = [];
    private List<InMemoryRandomAccessStream> _retiringStreams = [];
    private int _refreshQueued;
    private int _isRefreshing;

    public Action? OnManualRefresh { get; set; }

    public NetworkDashboardPage(NetworkLiveService liveService)
    {
        _liveService = liveService;

        Id = "com.dockpulse.page.network.dashboard";
        Name = "Network Dashboard";
        Title = FormatTitle(DashboardRange.Live);
        Icon = new IconInfo("\uE774");

        Commands =
        [
            new CommandContextItem(new ActionCommand(
                "Show Live Speed",
                () => SetRange(DashboardRange.Live),
                new IconInfo("\uE9D9"))),
            new CommandContextItem(new ActionCommand(
                "Show Today",
                () => SetRange(DashboardRange.Today),
                new IconInfo("\uE8BF"))),
            new CommandContextItem(new ActionCommand(
                "Show This Month",
                () => SetRange(DashboardRange.Month),
                new IconInfo("\uE787"))),
            new CommandContextItem(new ActionCommand(
                "Refresh Now",
                TriggerRefresh,
                new IconInfo("\uE72C"))),
            new CommandContextItem(new OpenUriCommand(
                "Open Windows Data Usage Settings",
                "ms-settings:datausage",
                new IconInfo("\uE713"))),
        ];

        var initialLive = _liveService.GetStats();
        string initialUri = NetworkLiveChartRenderer.RenderLiveChartDataUri(
            initialLive,
            NetworkLiveChartRenderer.DashboardWidth,
            NetworkLiveChartRenderer.DashboardHeight);
        _cachedContent = BuildContentArray(
            new MarkdownContent($"![Live Network Speed]({initialUri})"),
            DashboardRange.Live,
            initialLive,
            null);

        _liveService.Sampled += OnLiveSampled;
        RequestRefresh();
    }

    public override IContent[] GetContent()
    {
        _liveService.Touch();
        lock (_stateLock)
        {
            return _cachedContent;
        }
    }

    public void UpdateSnapshot(NetworkUsageSnapshot snapshot)
    {
        bool shouldRefresh;
        lock (_stateLock)
        {
            _lastSnapshot = snapshot;
            shouldRefresh = _range != DashboardRange.Live;
        }

        if (shouldRefresh)
        {
            RequestRefresh();
        }
    }

    private void OnLiveSampled()
    {
        bool isLive;
        lock (_stateLock)
        {
            isLive = _range == DashboardRange.Live;
        }

        if (isLive)
        {
            RequestRefresh();
        }
    }

    private void SetRange(DashboardRange range)
    {
        bool changed;
        lock (_stateLock)
        {
            changed = _range != range;
            _range = range;
        }

        if (range == DashboardRange.Live)
        {
            _liveService.Touch();
        }

        if (changed)
        {
            RequestRefresh();
        }
    }

    private void TriggerRefresh()
    {
        DashboardRange current;
        lock (_stateLock)
        {
            current = _range;
        }

        if (current == DashboardRange.Live)
        {
            _liveService.SampleNow();
        }
        else
        {
            OnManualRefresh?.Invoke();
        }

        DockPulseHelpers.ShowInlineToast("Network usage refreshed");
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
        DashboardRange range;
        NetworkUsageSnapshot? snap;
        lock (_stateLock)
        {
            range = _range;
            snap = _lastSnapshot;
        }

        var live = _liveService.GetStats();
        byte[] png = RenderChartPng(range, live, snap);
        var (stream, iconData) = await DockPulseHelpers.CreatePngIconDataAsync(png).ConfigureAwait(false);

        var chartImage = new ImageContent(new IconInfo(iconData, iconData))
        {
            MaxWidth = NetworkLiveChartRenderer.DashboardWidth,
            MaxHeight = NetworkLiveChartRenderer.DashboardHeight,
        };

        var nextContent = BuildContentArray(chartImage, range, live, snap);

        List<InMemoryRandomAccessStream> toDispose;
        lock (_stateLock)
        {
            toDispose = _retiringStreams;
            _retiringStreams = _activeStreams;
            _activeStreams = [stream];
            _cachedContent = nextContent;
        }

        DockPulseHelpers.DisposeStreams(toDispose);

        Title = FormatTitle(range);
        RaiseItemsChanged(nextContent.Length);
    }

    private static byte[] RenderChartPng(DashboardRange range, NetworkLiveStats live, NetworkUsageSnapshot? snap)
    {
        if (range == DashboardRange.Today && snap is not null)
        {
            string peakHour = NetworkUsageService.FormatPeakHour(snap.HourlyToday);
            return NetworkChartRenderer.RenderBarChartPng(
                title: "Today Hourly",
                subtitle: $"SINCE 00:00  |  PEAK: {peakHour.ToUpperInvariant()}",
                buckets: snap.HourlyToday,
                activeBucketIndex: snap.CurrentHour,
                isDarkTheme: true,
                width: NetworkLiveChartRenderer.DashboardWidth,
                height: NetworkLiveChartRenderer.DashboardHeight);
        }

        if (range == DashboardRange.Month && snap is not null)
        {
            string monthLabel = snap.MonthStart.ToString("MMM yyyy", CultureInfo.InvariantCulture);
            string peakDay = NetworkUsageService.FormatPeakDay(snap.DailyMonth, snap.MonthStart);
            ulong avg = snap.TotalMonth / (ulong)Math.Max(1, snap.CurrentDay);
            return NetworkChartRenderer.RenderBarChartPng(
                title: $"{monthLabel} Daily",
                subtitle: $"PEAK: {peakDay.ToUpperInvariant()}  |  AVG: {NetworkUsageService.FormatBytes(avg)}/DAY",
                buckets: snap.DailyMonth,
                activeBucketIndex: snap.CurrentDay - 1,
                isDarkTheme: true,
                width: NetworkLiveChartRenderer.DashboardWidth,
                height: NetworkLiveChartRenderer.DashboardHeight);
        }

        return NetworkLiveChartRenderer.RenderLiveChartPng(
            live,
            width: NetworkLiveChartRenderer.DashboardWidth,
            height: NetworkLiveChartRenderer.DashboardHeight);
    }

    private IContent[] BuildContentArray(
        IContent chartVisual,
        DashboardRange range,
        NetworkLiveStats live,
        NetworkUsageSnapshot? snap)
    {
        string md;
        if (range == DashboardRange.Today)
        {
            if (snap is null)
            {
                md = "_Loading network usage data..._";
            }
            else
            {
                string peakHour = NetworkUsageService.FormatPeakHour(snap.HourlyToday);
                md =
                    $"**Total:** {NetworkUsageService.FormatBytes(snap.TotalToday)}  \n" +
                    $"**Download:** {NetworkUsageService.FormatBytes(snap.TotalReceivedToday)}  •  " +
                    $"**Upload:** {NetworkUsageService.FormatBytes(snap.TotalSentToday)}  \n" +
                    $"**Peak Hour:** {peakHour}  \n" +
                    $"_Updated at {snap.UpdatedAt.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}_";
            }
        }
        else if (range == DashboardRange.Month)
        {
            if (snap is null)
            {
                md = "_Loading network usage data..._";
            }
            else
            {
                string peakDay = NetworkUsageService.FormatPeakDay(snap.DailyMonth, snap.MonthStart);
                ulong avg = snap.TotalMonth / (ulong)Math.Max(1, snap.CurrentDay);
                md =
                    $"**Total:** {NetworkUsageService.FormatBytes(snap.TotalMonth)}  \n" +
                    $"**Download:** {NetworkUsageService.FormatBytes(snap.TotalReceivedMonth)}  •  " +
                    $"**Upload:** {NetworkUsageService.FormatBytes(snap.TotalSentMonth)}  \n" +
                    $"**Daily Average:** {NetworkUsageService.FormatBytes(avg)}  •  **Peak Day:** {peakDay}";
            }
        }
        else
        {
            md =
                $"**Current:** ↓ {NetworkLiveService.FormatBps(live.Current.DownBps)}  •  ↑ {NetworkLiveService.FormatBps(live.Current.UpBps)}  \n" +
                $"**Peak (60 s):** ↓ {NetworkLiveService.FormatBps(live.PeakDownBps)}  •  ↑ {NetworkLiveService.FormatBps(live.PeakUpBps)}  \n" +
                $"**Average (60 s):** ↓ {NetworkLiveService.FormatBps(live.AvgDownBps)}  •  ↑ {NetworkLiveService.FormatBps(live.AvgUpBps)}  \n" +
                "_Sampled every 2 s while this page is open_";
        }

        return
        [
            chartVisual,
            new NetworkDashboardControlsForm(range, HandleAction),
            new MarkdownContent(md),
        ];
    }

    private CommandResult HandleAction(string actionId)
    {
        if (string.Equals(actionId, "live", StringComparison.OrdinalIgnoreCase))
        {
            SetRange(DashboardRange.Live);
        }
        else if (string.Equals(actionId, "today", StringComparison.OrdinalIgnoreCase))
        {
            SetRange(DashboardRange.Today);
        }
        else if (string.Equals(actionId, "month", StringComparison.OrdinalIgnoreCase))
        {
            SetRange(DashboardRange.Month);
        }
        else if (string.Equals(actionId, "refresh", StringComparison.OrdinalIgnoreCase))
        {
            TriggerRefresh();
        }

        return CommandResult.KeepOpen();
    }

    private static string FormatTitle(DashboardRange range) => range switch
    {
        DashboardRange.Today => "Network Dashboard  •  Today",
        DashboardRange.Month => "Network Dashboard  •  Month",
        _ => "Network Dashboard  •  Live",
    };

    public void Dispose()
    {
        _liveService.Sampled -= OnLiveSampled;

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

    private sealed partial class NetworkDashboardControlsForm : FormContent
    {
        private readonly Func<string, CommandResult> _onAction;

        public NetworkDashboardControlsForm(DashboardRange activeRange, Func<string, CommandResult> onAction)
        {
            _onAction = onAction;
            string liveStyle = activeRange == DashboardRange.Live ? ",\n                        \"style\": \"positive\"" : string.Empty;
            string todayStyle = activeRange == DashboardRange.Today ? ",\n                        \"style\": \"positive\"" : string.Empty;
            string monthStyle = activeRange == DashboardRange.Month ? ",\n                        \"style\": \"positive\"" : string.Empty;

            TemplateJson = $$"""
            {
                "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
                "type": "AdaptiveCard",
                "version": "1.5",
                "body": [],
                "actions": [
                    {
                        "type": "Action.Submit",
                        "title": "Live",
                        "data": { "action": "live" }{{liveStyle}}
                    },
                    {
                        "type": "Action.Submit",
                        "title": "Today",
                        "data": { "action": "today" }{{todayStyle}}
                    },
                    {
                        "type": "Action.Submit",
                        "title": "Month",
                        "data": { "action": "month" }{{monthStyle}}
                    },
                    {
                        "type": "Action.Submit",
                        "title": "Refresh",
                        "data": { "action": "refresh" }
                    }
                ]
            }
            """;
        }

        public override ICommandResult SubmitForm(string inputs, string data)
        {
            string? action = ExtractAction(data) ?? ExtractAction(inputs);
            return action is not null ? _onAction(action) : CommandResult.KeepOpen();
        }

        public override CommandResult SubmitForm(string payload)
        {
            string? action = ExtractAction(payload);
            return action is not null ? _onAction(action) : CommandResult.KeepOpen();
        }

        private static string? ExtractAction(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.String)
                {
                    return root.GetString();
                }

                if (DockPulseHelpers.TryReadString(root, "action", out var action) && !string.IsNullOrWhiteSpace(action))
                {
                    return action;
                }
            }
            catch
            {
                // Ignore malformed payloads
            }

            return null;
        }
    }
}

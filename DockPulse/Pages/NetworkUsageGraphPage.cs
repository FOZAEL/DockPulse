using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using DockPulse.Commands;
using DockPulse.Services;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Windows.Storage.Streams;

namespace DockPulse.Pages;

internal sealed partial class NetworkUsageGraphPage : ContentPage, IDisposable
{
    private readonly NetworkUsageService _service;
    private readonly object _stateLock = new();

    private IContent[] _cachedContent =
    [
        new MarkdownContent("### Loading Network Usage Graphs...\nReading Windows Data Usage (SRUM) hourly and daily statistics."),
    ];

    private List<InMemoryRandomAccessStream> _activeStreams = [];
    private List<InMemoryRandomAccessStream> _retiringStreams = [];

    public Action? OnManualRefresh { get; set; }

    public NetworkUsageGraphPage(NetworkUsageService service)
    {
        _service = service;

        Id = "com.dockpulse.page.network.graphs";
        Name = "Usage Graphs";
        Title = "Network Usage Graphs (Today & This Month)";
        Icon = new IconInfo("\uE9D2");

        Commands =
        [
            new CommandContextItem(new ActionCommand("Refresh Graphs", TriggerRefresh, new IconInfo("\uE72C"))),
            new CommandContextItem(new OpenUriCommand("Open Windows Data Usage Settings", "ms-settings:datausage", new IconInfo("\uE713"))),
        ];
    }

    public override IContent[] GetContent()
    {
        lock (_stateLock)
        {
            return _cachedContent;
        }
    }

    public async Task UpdateSnapshotAsync(NetworkUsageSnapshot snapshot)
    {
        string peakHour = NetworkUsageService.FormatPeakHour(snapshot.HourlyToday);
        string peakDay = NetworkUsageService.FormatPeakDay(snapshot.DailyMonth, snapshot.MonthStart);
        ulong dailyAvg = snapshot.TotalMonth / (ulong)Math.Max(1, snapshot.CurrentDay);
        string monthLabel = snapshot.MonthStart.ToString("MMM yyyy", CultureInfo.InvariantCulture);

        byte[] todayLightPng = NetworkChartRenderer.RenderBarChartPng(
            title: "Today Hourly",
            subtitle: $"SINCE 00:00  |  PEAK: {peakHour.ToUpperInvariant()}",
            buckets: snapshot.HourlyToday,
            activeBucketIndex: snapshot.CurrentHour,
            isDarkTheme: false);

        byte[] todayDarkPng = NetworkChartRenderer.RenderBarChartPng(
            title: "Today Hourly",
            subtitle: $"SINCE 00:00  |  PEAK: {peakHour.ToUpperInvariant()}",
            buckets: snapshot.HourlyToday,
            activeBucketIndex: snapshot.CurrentHour,
            isDarkTheme: true);

        byte[] monthLightPng = NetworkChartRenderer.RenderBarChartPng(
            title: $"{monthLabel} Daily",
            subtitle: $"PEAK: {peakDay.ToUpperInvariant()}  |  AVG: {NetworkUsageService.FormatBytes(dailyAvg)}/DAY",
            buckets: snapshot.DailyMonth,
            activeBucketIndex: snapshot.CurrentDay - 1,
            isDarkTheme: false);

        byte[] monthDarkPng = NetworkChartRenderer.RenderBarChartPng(
            title: $"{monthLabel} Daily",
            subtitle: $"PEAK: {peakDay.ToUpperInvariant()}  |  AVG: {NetworkUsageService.FormatBytes(dailyAvg)}/DAY",
            buckets: snapshot.DailyMonth,
            activeBucketIndex: snapshot.CurrentDay - 1,
            isDarkTheme: true);

        var newStreams = new List<InMemoryRandomAccessStream>(4);
        var (todayLightStream, todayLightIcon) = await CreatePngIconDataAsync(todayLightPng).ConfigureAwait(false);
        newStreams.Add(todayLightStream);
        var (todayDarkStream, todayDarkIcon) = await CreatePngIconDataAsync(todayDarkPng).ConfigureAwait(false);
        newStreams.Add(todayDarkStream);

        var (monthLightStream, monthLightIcon) = await CreatePngIconDataAsync(monthLightPng).ConfigureAwait(false);
        newStreams.Add(monthLightStream);
        var (monthDarkStream, monthDarkIcon) = await CreatePngIconDataAsync(monthDarkPng).ConfigureAwait(false);
        newStreams.Add(monthDarkStream);

        var todayImage = new ImageContent(new IconInfo(todayLightIcon, todayDarkIcon))
        {
            MaxWidth = NetworkChartRenderer.DefaultWidth,
            MaxHeight = NetworkChartRenderer.DefaultHeight,
        };

        var monthImage = new ImageContent(new IconInfo(monthLightIcon, monthDarkIcon))
        {
            MaxWidth = NetworkChartRenderer.DefaultWidth,
            MaxHeight = NetworkChartRenderer.DefaultHeight,
        };

        var todayHeader = new MarkdownContent(
            $"### Today (since 00:00) — **{NetworkUsageService.FormatBytes(snapshot.TotalToday)}**\n" +
            $"Download: **{NetworkUsageService.FormatBytes(snapshot.TotalReceivedToday)}**  •  " +
            $"Upload: **{NetworkUsageService.FormatBytes(snapshot.TotalSentToday)}**  •  " +
            $"Peak Hour: **{peakHour}**");

        var monthHeader = new MarkdownContent(
            $"### This Month ({monthLabel}) — **{NetworkUsageService.FormatBytes(snapshot.TotalMonth)}**\n" +
            $"Download: **{NetworkUsageService.FormatBytes(snapshot.TotalReceivedMonth)}**  •  " +
            $"Upload: **{NetworkUsageService.FormatBytes(snapshot.TotalSentMonth)}**  •  " +
            $"Daily Avg: **{NetworkUsageService.FormatBytes(dailyAvg)}**  •  " +
            $"Peak Day: **{peakDay}**");

        var adapterSummary = new MarkdownContent(BuildAdapterSummaryMarkdown(snapshot));

        IContent[] nextContent =
        [
            todayHeader,
            todayImage,
            monthHeader,
            monthImage,
            adapterSummary,
        ];

        List<InMemoryRandomAccessStream> toDispose;
        lock (_stateLock)
        {
            // Keep one retiring generation alive across UI swaps so WinUI never reads a disposed stream
            toDispose = _retiringStreams;
            _retiringStreams = _activeStreams;
            _activeStreams = newStreams;
            _cachedContent = nextContent;
        }

        DisposeStreams(toDispose);
        RaiseItemsChanged(nextContent.Length);
    }

    private static string BuildAdapterSummaryMarkdown(NetworkUsageSnapshot snapshot)
    {
        var sb = new StringBuilder();
        sb.AppendLine("### Adapters");
        if (snapshot.Adapters.Count == 0)
        {
            sb.AppendLine("_No active network adapters with SRUM data usage found._");
        }
        else
        {
            sb.AppendLine("| Adapter | Status | Today | This Month |");
            sb.AppendLine("| :--- | :--- | ---: | ---: |");
            foreach (var adapter in snapshot.Adapters)
            {
                string status = adapter.IsActiveInternet ? "Active" : "Idle";
                sb.AppendLine(
                    $"| {adapter.ProfileName} | {status} | " +
                    $"{NetworkUsageService.FormatBytes(adapter.TotalToday)} | " +
                    $"{NetworkUsageService.FormatBytes(adapter.TotalMonth)} |");
            }
        }

        sb.AppendLine();
        sb.Append($"_Last updated at {snapshot.UpdatedAt:HH:mm:ss}_");
        return sb.ToString();
    }

    private static async Task<(InMemoryRandomAccessStream Stream, IconData IconData)> CreatePngIconDataAsync(byte[] pngBytes)
    {
        var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(pngBytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }

        stream.Seek(0);
        return (stream, new IconData(RandomAccessStreamReference.CreateFromStream(stream)));
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
            await UpdateSnapshotAsync(latest).ConfigureAwait(false);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static void DisposeStreams(List<InMemoryRandomAccessStream> streams)
    {
        foreach (var stream in streams)
        {
            try
            {
                stream.Dispose();
            }
            catch
            {
                // Ignore disposal errors
            }
        }
    }

    public void Dispose()
    {
        List<InMemoryRandomAccessStream> active;
        List<InMemoryRandomAccessStream> retiring;
        lock (_stateLock)
        {
            active = _activeStreams;
            retiring = _retiringStreams;
            _activeStreams = [];
            _retiringStreams = [];
        }

        DisposeStreams(active);
        DisposeStreams(retiring);
    }
}

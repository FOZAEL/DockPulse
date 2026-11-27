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

internal sealed partial class AiDashboardPage : ContentPage, IDisposable
{
    private readonly AiUsageService _service;
    private readonly AiDockSettings _dockSettings;
    private readonly object _stateLock = new();

    private IContent[] _cachedContent;
    private List<InMemoryRandomAccessStream> _activeStreams = [];
    private List<InMemoryRandomAccessStream> _retiringStreams = [];
    private int _refreshQueued;
    private int _isRefreshing;

    public Action? OnManualRefresh { get; set; }

    public AiDashboardPage(AiUsageService service, AiDockSettings dockSettings)
    {
        _service = service;
        _dockSettings = dockSettings;

        bool weekly = _dockSettings.ShowWeeklyCompare;
        Id = "com.dockpulse.page.aiusage.dashboard";
        Name = "AI Usage Dashboard";
        Title = FormatTitle(weekly);
        Icon = new IconInfo("\uE9E9");

        Commands =
        [
            new CommandContextItem(new ActionCommand(
                "Show 5-Hour Window",
                () => _dockSettings.SetShowWeeklyCompare(false),
                new IconInfo("\uE823"))),
            new CommandContextItem(new ActionCommand(
                "Show Weekly Limit",
                () => _dockSettings.SetShowWeeklyCompare(true),
                new IconInfo("\uE8C0"))),
            new CommandContextItem(new ActionCommand(
                "Refresh AI Usage",
                TriggerRefresh,
                new IconInfo("\uE72C"))),
            new CommandContextItem(new OpenUriCommand(
                "Open Cursor Usage",
                AiDockSettings.CursorUrl,
                new IconInfo("\uE774"))),
        ];

        var initial = _service.GetUsageSnapshot();
        var nowUtc = DateTimeOffset.UtcNow;
        string initialUri = AiUsageChartRenderer.RenderCompareCardDataUri(
            initial,
            weekly,
            nowUtc,
            AiUsageChartRenderer.CompareDashboardWidth,
            AiUsageChartRenderer.CompareDashboardHeight);
        _cachedContent = BuildContentArray(new MarkdownContent($"![Compare Providers]({initialUri})"), initial, weekly, nowUtc);

        _service.Updated += RequestRefresh;
        _dockSettings.CompareWindowChanged += RequestRefresh;
        RequestRefresh();
    }

    public override IContent[] GetContent()
    {
        lock (_stateLock)
        {
            return _cachedContent;
        }
    }

    public void UpdateSnapshot(AiUsageSnapshot _) => RequestRefresh();

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
        var snap = _service.GetUsageSnapshot();
        bool weekly = _dockSettings.ShowWeeklyCompare;
        var nowUtc = DateTimeOffset.UtcNow;

        byte[] png = AiUsageChartRenderer.RenderCompareCardPng(
            snap,
            weekly,
            nowUtc,
            AiUsageChartRenderer.CompareDashboardWidth,
            AiUsageChartRenderer.CompareDashboardHeight);
        var (stream, iconData) = await DockPulseHelpers.CreatePngIconDataAsync(png).ConfigureAwait(false);

        var chartImage = new ImageContent(new IconInfo(iconData, iconData))
        {
            MaxWidth = AiUsageChartRenderer.CompareDashboardWidth,
            MaxHeight = AiUsageChartRenderer.CompareDashboardHeight,
        };

        var nextContent = BuildContentArray(chartImage, snap, weekly, nowUtc);

        List<InMemoryRandomAccessStream> toDispose;
        lock (_stateLock)
        {
            toDispose = _retiringStreams;
            _retiringStreams = _activeStreams;
            _activeStreams = [stream];
            _cachedContent = nextContent;
        }

        DockPulseHelpers.DisposeStreams(toDispose);

        Title = FormatTitle(weekly);
        RaiseItemsChanged(nextContent.Length);
    }

    private IContent[] BuildContentArray(IContent chartVisual, AiUsageSnapshot snap, bool weekly, DateTimeOffset nowUtc)
    {
        var lines = new List<string>(snap.Providers.Count + 2);
        foreach (var provider in snap.Providers)
        {
            if (!provider.HasData)
            {
                string unavail = string.IsNullOrWhiteSpace(provider.UnavailableSubtitle)
                    ? "No data"
                    : provider.UnavailableSubtitle;
                lines.Add($"**{provider.Name}:** {unavail}  •  {provider.PlanSubtitle}");
                continue;
            }

            var window = weekly ? provider.Weekly : provider.FiveHour;
            if (window is null)
            {
                lines.Add($"**{provider.Name}:** No data  •  {provider.PlanSubtitle}");
                continue;
            }

            string reset = window.FormatResetShort(nowUtc);
            string resetText = string.Equals(reset, "Reset", StringComparison.Ordinal)
                ? "window reset"
                : $"resets in {reset}";
            lines.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"**{provider.Name}:** {window.RemainingPercent}% remaining ({resetText})  •  {provider.PlanSubtitle}"));
        }

        lines.Add("**Cursor:** usage on cursor.com (link only)");
        lines.Add($"_Last checked at {snap.CheckedAt.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}_");

        return
        [
            chartVisual,
            new AiDashboardControlsForm(weekly, HandleAction),
            new MarkdownContent(string.Join("  \n", lines)),
        ];
    }

    private CommandResult HandleAction(string actionId)
    {
        if (string.Equals(actionId, "5h", StringComparison.OrdinalIgnoreCase))
        {
            _dockSettings.SetShowWeeklyCompare(false);
        }
        else if (string.Equals(actionId, "week", StringComparison.OrdinalIgnoreCase))
        {
            _dockSettings.SetShowWeeklyCompare(true);
        }
        else if (string.Equals(actionId, "refresh", StringComparison.OrdinalIgnoreCase))
        {
            TriggerRefresh();
        }

        return CommandResult.KeepOpen();
    }

    private void TriggerRefresh()
    {
        DockPulseHelpers.ShowInlineToast("Refreshing AI usage...");
        if (OnManualRefresh is not null)
        {
            OnManualRefresh();
            RequestRefresh();
            return;
        }

        _ = _service.GetUsageSnapshot(forceRefresh: true);
        RequestRefresh();
    }

    private static string FormatTitle(bool weekly) =>
        weekly ? "AI Usage Dashboard  •  Weekly" : "AI Usage Dashboard  •  5h";

    public void Dispose()
    {
        _service.Updated -= RequestRefresh;
        _dockSettings.CompareWindowChanged -= RequestRefresh;

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

    private sealed partial class AiDashboardControlsForm : FormContent
    {
        private readonly Func<string, CommandResult> _onAction;

        public AiDashboardControlsForm(bool weekly, Func<string, CommandResult> onAction)
        {
            _onAction = onAction;
            string fiveHourStyle = weekly ? string.Empty : ",\n                        \"style\": \"positive\"";
            string weeklyStyle = weekly ? ",\n                        \"style\": \"positive\"" : string.Empty;

            TemplateJson = $$"""
            {
                "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
                "type": "AdaptiveCard",
                "version": "1.5",
                "body": [],
                "actions": [
                    {
                        "type": "Action.Submit",
                        "title": "5h",
                        "data": { "action": "5h" }{{fiveHourStyle}}
                    },
                    {
                        "type": "Action.Submit",
                        "title": "Weekly",
                        "data": { "action": "week" }{{weeklyStyle}}
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

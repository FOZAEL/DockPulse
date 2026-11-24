using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using DockPulse.Commands;
using DockPulse.Services;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Windows.Storage.Streams;

namespace DockPulse.Pages;

internal sealed partial class PrayerTimesPage : ContentPage, IDisposable
{
    private readonly PrayerTimesService _service;
    private readonly PrayerRegionPickerPage _regionPickerPage;
    private readonly PrayerMethodPickerPage _methodPickerPage;
    private readonly object _stateLock = new();

    private IContent[] _cachedContent;
    private List<InMemoryRandomAccessStream> _activeStreams = [];
    private List<InMemoryRandomAccessStream> _retiringStreams = [];

    public Action? OnManualRefresh { get; set; }

    public PrayerTimesPage(
        PrayerTimesService service,
        PrayerRegionPickerPage regionPickerPage,
        PrayerMethodPickerPage methodPickerPage)
    {
        _service = service;
        _regionPickerPage = regionPickerPage;
        _methodPickerPage = methodPickerPage;

        var initial = _service.GetCurrentSchedule();

        Id = "com.dockpulse.page.prayer";
        Name = "Prayer Times";
        Title = FormatFooterTitle(initial);
        Icon = new IconInfo("\uE708");

        byte[] initialPng = PrayerChartRenderer.RenderScheduleCardPng(initial);
        string initialDataUri = $"data:image/png;base64,{Convert.ToBase64String(initialPng)}";
        _cachedContent = BuildContentArray(new MarkdownContent($"![Prayer Schedule]({initialDataUri})"), initial);
        Commands = BuildFooterCommands(initial);

        _ = UpdateSnapshotAsync(initial);
    }

    public override IContent[] GetContent()
    {
        lock (_stateLock)
        {
            return _cachedContent;
        }
    }

    public async Task UpdateSnapshotAsync(PrayerScheduleSnapshot snapshot)
    {
        byte[] cardPng = PrayerChartRenderer.RenderScheduleCardPng(snapshot);
        var (cardStream, cardIcon) = await CreatePngIconDataAsync(cardPng).ConfigureAwait(false);

        var scheduleCard = new ImageContent(new IconInfo(cardIcon, cardIcon))
        {
            MaxWidth = PrayerChartRenderer.DefaultWidth,
            MaxHeight = PrayerChartRenderer.DefaultHeight,
        };

        IContent[] nextContent = BuildContentArray(scheduleCard, snapshot);

        List<InMemoryRandomAccessStream> toDispose;
        lock (_stateLock)
        {
            toDispose = _retiringStreams;
            _retiringStreams = _activeStreams;
            _activeStreams = [cardStream];
            _cachedContent = nextContent;
        }

        DisposeStreams(toDispose);

        Title = FormatFooterTitle(snapshot);
        Commands = BuildFooterCommands(snapshot);
        RaiseItemsChanged(nextContent.Length);
    }

    private IContent[] BuildContentArray(IContent scheduleVisual, PrayerScheduleSnapshot snapshot)
    {
        string asrShort = snapshot.AsrSchool == AsrJuristicSchool.Hanafi ? "Hanafi" : "Standard";
        bool notifOn = snapshot.NotificationsEnabled;

        return
        [
            scheduleVisual,
            new MarkdownContent("**Configuration**"),
            new PrayerConfigRowForm(
                title: $"Region & Timezone: {snapshot.Region.DisplayName}",
                subtitle: $"{snapshot.UtcOffsetLabel}  •  Choose from 60+ cities & timezones worldwide",
                tagText: snapshot.UtcOffsetLabel,
                onSelect: () => CommandResult.GoToPage(new GoToPageArgs { PageId = _regionPickerPage.Id })),
            new PrayerConfigRowForm(
                title: $"Calculation Method: {snapshot.Method.ShortName}",
                subtitle: $"{snapshot.Method.Name}  •  Asr School: {asrShort}",
                tagText: asrShort,
                onSelect: () => CommandResult.GoToPage(new GoToPageArgs { PageId = _methodPickerPage.Id })),
            new PrayerConfigRowForm(
                title: $"Prayer Notifications: {(notifOn ? "On" : "Off")}",
                subtitle: "Send a Windows notification when each prayer time is reached",
                tagText: notifOn ? "Enabled" : "Muted",
                onSelect: () =>
                {
                    _service.ToggleNotifications();
                    return CommandResult.KeepOpen();
                }),
            new PrayerConfigRowForm(
                title: "Refresh Prayer Times",
                subtitle: $"Local region time: {snapshot.RegionNow:ddd, MMM d • HH:mm} ({snapshot.UtcOffsetLabel})",
                tagText: null,
                onSelect: () =>
                {
                    TriggerRefresh();
                    return CommandResult.KeepOpen();
                }),
        ];
    }

    private IContextItem[] BuildFooterCommands(PrayerScheduleSnapshot snapshot)
    {
        string asrShort = snapshot.AsrSchool == AsrJuristicSchool.Hanafi ? "Hanafi" : "Standard";
        bool notifOn = snapshot.NotificationsEnabled;

        return
        [
            new CommandContextItem(_regionPickerPage)
            {
                Title = $"Change Region & Timezone ({snapshot.Region.City} • {snapshot.UtcOffsetLabel})",
                Icon = new IconInfo("\uE707"),
            },
            new CommandContextItem(_methodPickerPage)
            {
                Title = $"Change Calculation Method ({snapshot.Method.ShortName} • {asrShort})",
                Icon = new IconInfo("\uE909"),
            },
            new CommandContextItem(new ActionCommand(
                $"Prayer Notifications ({(notifOn ? "On" : "Off")})",
                () => _service.ToggleNotifications(),
                new IconInfo(notifOn ? "\uEA8F" : "\uE7ED"))),
            new CommandContextItem(new ActionCommand(
                "Send Test Windows Notification",
                () => PrayerTimesService.SendTestNotification(snapshot),
                new IconInfo("\uEA8F"))),
            new CommandContextItem(new ActionCommand(
                "Refresh Prayer Times",
                TriggerRefresh,
                new IconInfo("\uE72C"))),
        ];
    }

    private static string FormatFooterTitle(PrayerScheduleSnapshot snapshot) =>
        $"Prayer Times  •  {snapshot.Region.City} ({snapshot.UtcOffsetLabel})  •  {snapshot.Method.ShortName}";

    private void TriggerRefresh()
    {
        if (OnManualRefresh is not null)
        {
            OnManualRefresh();
            return;
        }

        _ = UpdateSnapshotAsync(_service.GetCurrentSchedule());
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

internal sealed partial class PrayerConfigRowForm : FormContent
{
    private readonly Func<CommandResult> _onSelect;

    public PrayerConfigRowForm(
        string title,
        string subtitle,
        string? tagText,
        Func<CommandResult> onSelect)
    {
        _onSelect = onSelect;

        string escTitle = JsonEncodedText.Encode(title).ToString();
        string escSubtitle = JsonEncodedText.Encode(subtitle).ToString();

        string tagColumnJson = string.IsNullOrWhiteSpace(tagText)
            ? string.Empty
            : $$"""
              ,
              {
                  "type": "Column",
                  "width": "auto",
                  "verticalContentAlignment": "Center",
                  "items": [
                      {
                          "type": "Container",
                          "style": "emphasis",
                          "items": [
                              {
                                  "type": "TextBlock",
                                  "text": "{{JsonEncodedText.Encode(tagText).ToString()}}",
                                  "size": "Small",
                                  "isSubtle": true
                              }
                          ]
                      }
                  ]
              }
              """;

        TemplateJson = $$"""
        {
            "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
            "type": "AdaptiveCard",
            "version": "1.5",
            "body": [
                {
                    "type": "Container",
                    "selectAction": {
                        "type": "Action.Submit",
                        "title": "{{escTitle}}"
                    },
                    "items": [
                        {
                            "type": "ColumnSet",
                            "columns": [
                                {
                                    "type": "Column",
                                    "width": "stretch",
                                    "verticalContentAlignment": "Center",
                                    "items": [
                                        {
                                            "type": "TextBlock",
                                            "text": "{{escTitle}}",
                                            "weight": "Bolder",
                                            "wrap": true
                                        },
                                        {
                                            "type": "TextBlock",
                                            "text": "{{escSubtitle}}",
                                            "size": "Small",
                                            "isSubtle": true,
                                            "spacing": "None",
                                            "wrap": true
                                        }
                                    ]
                                }{{tagColumnJson}}
                            ]
                        }
                    ]
                }
            ]
        }
        """;
    }

    public override CommandResult SubmitForm(string payload) => _onSelect();
}

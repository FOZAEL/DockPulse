using System;
using System.Collections.Generic;
using System.Globalization;
using DockPulse.Commands;
using DockPulse.Helpers;
using DockPulse.Services;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace DockPulse.Pages;

internal sealed partial class AiDockPickerPage : ListPage, IDisposable
{
    private readonly AiUsageService _service;
    private readonly AiDockSettings _dockSettings;
    private readonly object _lock = new();
    private AiUsageSnapshot _lastSnapshot;
    private IListItem[] _cachedItems;

    public AiDockPickerPage(AiUsageService service, AiDockSettings dockSettings)
    {
        _service = service;
        _dockSettings = dockSettings;

        Id = "com.dockpulse.page.aiusage.dock";
        Name = "Dock Provider";
        Title = "Choose What the AI Usage Dock Shows";
        Icon = new IconInfo("\uE718");
        PlaceholderText = "Filter providers...";

        _lastSnapshot = _service.GetUsageSnapshot();
        _cachedItems = BuildItems(_lastSnapshot);

        _service.Updated += OnServiceUpdated;
        _dockSettings.Changed += RebuildFromLastSnapshot;
    }

    public void UpdateSnapshot(AiUsageSnapshot snapshot)
    {
        IListItem[] built;
        lock (_lock)
        {
            _lastSnapshot = snapshot;
            built = BuildItems(snapshot);
            _cachedItems = built;
        }

        RaiseItemsChanged(built.Length);
    }

    public override IListItem[] GetItems()
    {
        lock (_lock)
        {
            return _cachedItems;
        }
    }

    private void OnServiceUpdated() => UpdateSnapshot(_service.GetUsageSnapshot());

    private void RebuildFromLastSnapshot()
    {
        IListItem[] built;
        lock (_lock)
        {
            built = BuildItems(_lastSnapshot);
            _cachedItems = built;
        }

        RaiseItemsChanged(built.Length);
    }

    private IListItem[] BuildItems(AiUsageSnapshot snapshot)
    {
        string current = _dockSettings.DockMode;
        bool isAuto = string.Equals(current, AiDockSettings.AutoMode, StringComparison.OrdinalIgnoreCase);

        var items = new List<IListItem>(snapshot.Providers.Count + 1)
        {
            new ListItem(new ActionCommand(
                "Select Auto",
                () =>
                {
                    _dockSettings.SetDockMode(AiDockSettings.AutoMode);
                    DockPulseHelpers.ShowInlineToast($"Dock now shows {AiDockSettings.AutoLabel}");
                },
                new IconInfo("\uE73E"),
                result: CommandResult.GoBack()))
            {
                Icon = new IconInfo("\uE99A"),
                Title = AiDockSettings.AutoLabel,
                Subtitle = "Whichever provider and window is closest to its limit",
                Tags = isAuto
                    ? [DockPulseHelpers.CreateTag("Selected", TagTone.Accent, new IconInfo("\uE73E"))]
                    : [],
            },
        };

        foreach (var provider in snapshot.Providers)
        {
            bool selected = string.Equals(current, provider.Id, StringComparison.OrdinalIgnoreCase);
            var tags = new List<ITag>();
            if (selected)
            {
                tags.Add(DockPulseHelpers.CreateTag("Selected", TagTone.Accent, new IconInfo("\uE73E")));
            }

            if (!provider.HasData)
            {
                tags.Add(DockPulseHelpers.CreateTag("No data", TagTone.Warn));
            }

            string subtitle;
            if (provider.HasData)
            {
                var parts = new List<string>(2);
                if (provider.FiveHour is not null)
                {
                    parts.Add(string.Create(CultureInfo.InvariantCulture, $"5h {provider.FiveHour.RemainingPercent}%"));
                }

                if (provider.Weekly is not null)
                {
                    parts.Add(string.Create(CultureInfo.InvariantCulture, $"Weekly {provider.Weekly.RemainingPercent}%"));
                }

                subtitle = parts.Count > 0 ? string.Join("  •  ", parts) : "No usage data available";
            }
            else
            {
                subtitle = string.IsNullOrWhiteSpace(provider.UnavailableSubtitle)
                    ? "No usage data available"
                    : provider.UnavailableSubtitle;
            }

            string targetId = provider.Id;
            string targetName = provider.Name;
            items.Add(new ListItem(new ActionCommand(
                $"Show {targetName} on Dock",
                () =>
                {
                    _dockSettings.SetDockMode(targetId);
                    DockPulseHelpers.ShowInlineToast($"Dock now shows {targetName}");
                },
                new IconInfo("\uE718"),
                result: CommandResult.GoBack()))
            {
                Icon = AiLogos.GetIcon(provider.Id),
                Title = provider.Name,
                Subtitle = subtitle,
                Tags = [.. tags],
            });
        }

        return [.. items];
    }

    public void Dispose()
    {
        _service.Updated -= OnServiceUpdated;
        _dockSettings.Changed -= RebuildFromLastSnapshot;
    }
}

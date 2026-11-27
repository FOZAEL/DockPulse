using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using DockPulse.Commands;
using DockPulse.Helpers;
using DockPulse.Services;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace DockPulse.Pages;

internal sealed partial class AiUsagePage : ListPage, IDisposable
{
    private readonly AiUsageService _service;
    private readonly AiDockSettings _dockSettings;
    private readonly AiDashboardPage _dashboardPage;
    private readonly AiDockPickerPage _pickerPage;
    private readonly object _itemsLock = new();
    private AiUsageSnapshot _lastSnapshot;
    private IListItem[] _cachedItems;

    public Action? OnManualRefresh { get; set; }

    public AiUsagePage(
        AiUsageService service,
        AiDockSettings dockSettings,
        AiDashboardPage dashboardPage,
        AiDockPickerPage pickerPage)
    {
        _service = service;
        _dockSettings = dockSettings;
        _dashboardPage = dashboardPage;
        _pickerPage = pickerPage;

        Id = "com.dockpulse.page.aiusage";
        Name = "AI Usage";
        Title = "AI Usage Limits";
        Icon = new IconInfo("\uE99A");
        PlaceholderText = "Filter AI providers...";
        ShowDetails = true;

        _lastSnapshot = _service.GetUsageSnapshot();
        _cachedItems = BuildItems(_lastSnapshot);

        _dockSettings.Changed += RebuildFromLastSnapshot;
        _dockSettings.CompareWindowChanged += RebuildFromLastSnapshot;
    }

    public void UpdateSnapshot(AiUsageSnapshot snapshot)
    {
        var built = BuildItems(snapshot);
        lock (_itemsLock)
        {
            _lastSnapshot = snapshot;
            _cachedItems = built;
        }

        RaiseItemsChanged(built.Length);
    }

    public override IListItem[] GetItems()
    {
        lock (_itemsLock)
        {
            return _cachedItems;
        }
    }

    private void RebuildFromLastSnapshot()
    {
        AiUsageSnapshot snapshot;
        lock (_itemsLock)
        {
            snapshot = _lastSnapshot;
        }

        var built = BuildItems(snapshot);
        lock (_itemsLock)
        {
            _cachedItems = built;
        }

        RaiseItemsChanged(built.Length);
    }

    private IListItem[] BuildItems(AiUsageSnapshot snapshot)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        bool weekly = _dockSettings.ShowWeeklyCompare;
        string winLabel = weekly ? "Weekly" : "5h";
        string dockMode = _dockSettings.DockMode;
        var dock = AiDockSettings.ResolveDockDisplay(snapshot, dockMode, nowUtc);

        string compareChartUri = AiUsageChartRenderer.RenderCompareCardDataUri(
            snapshot,
            weekly,
            nowUtc,
            width: AiUsageChartRenderer.DefaultWidth,
            height: AiUsageChartRenderer.DefaultHeight);

        string dockDetailBody = string.Equals(dock.Mode, AiDockSettings.AutoMode, StringComparison.OrdinalIgnoreCase)
            ? $"**Mode:** {AiDockSettings.AutoLabel}  \n" +
              $"**Showing:** {dock.Title}  •  {dock.Subtitle}\n\n" +
              "Automatically tracks whichever signed-in provider and window (5-hour or weekly) is closest to its limit.\n\n" +
              "Press **Enter** to pin a specific provider to the Dock."
            : $"**Mode:** {dock.ChoiceLabel} (pinned)  \n" +
              $"**Showing:** {dock.Title}  •  {dock.Subtitle}\n\n" +
              $"Always displays **{dock.ChoiceLabel}** on the Dock band using its tightest window (5-hour or weekly).\n\n" +
              "Press **Enter** to choose another provider or switch back to auto.";

        var items = new List<IListItem>(snapshot.Providers.Count + 5)
        {
            new ListItem(_dashboardPage)
            {
                Icon = new IconInfo("\uE9E9"),
                Title = "Compare Providers",
                Subtitle = $"{winLabel} remaining for Claude, Antigravity and Codex side by side",
                Tags = [DockPulseHelpers.CreateTag("Live", TagTone.Accent)],
                Details = new Details
                {
                    Title = "Compare Providers",
                    Body = $"![Compare Providers]({compareChartUri})\n\n_Redraws when new usage arrives_",
                },
                MoreCommands =
                [
                    new CommandContextItem(new ActionCommand("Refresh AI Usage", TriggerRefresh, new IconInfo("\uE72C"))),
                ],
            },
            new ListItem(_pickerPage)
            {
                Icon = new IconInfo("\uE718"),
                Title = $"Dock Shows: {dock.ChoiceLabel}",
                Subtitle = "Choose which AI provider appears on the Dock",
                Tags = [DockPulseHelpers.CreateTag("Dock", TagTone.Accent)],
                Details = new Details
                {
                    Title = "Dock Display",
                    Body = dockDetailBody,
                },
                MoreCommands =
                [
                    new CommandContextItem(new ActionCommand("Refresh AI Usage", TriggerRefresh, new IconInfo("\uE72C"))),
                ],
            },
        };

        AiProviderUsage? tightestProvider = null;
        string? tightestChartUri = null;
        string? firstChartUri = null;

        foreach (var provider in snapshot.Providers)
        {
            string chartUri = AiUsageChartRenderer.RenderProviderCardDataUri(provider, nowUtc);
            firstChartUri ??= chartUri;
            if (snapshot.Tightest is not null &&
                string.Equals(provider.Id, snapshot.Tightest.ProviderId, StringComparison.OrdinalIgnoreCase))
            {
                tightestProvider = provider;
                tightestChartUri = chartUri;
            }

            bool isPinned = string.Equals(dockMode, provider.Id, StringComparison.OrdinalIgnoreCase);
            string targetId = provider.Id;
            string targetName = provider.Name;
            var showOnDockItem = new CommandContextItem(new ActionCommand(
                isPinned ? "Showing on Dock" : "Show on Dock",
                () =>
                {
                    if (!isPinned)
                    {
                        _dockSettings.SetDockMode(targetId);
                    }

                    DockPulseHelpers.ShowInlineToast($"Dock now shows {targetName}");
                },
                new IconInfo("\uE718")));

            if (!provider.HasData)
            {
                items.Add(new ListItem(new ActionCommand(
                    $"Refresh {provider.Name}",
                    TriggerRefresh,
                    new IconInfo("\uE72C")))
                {
                    Icon = AiLogos.GetIcon(provider.Id),
                    Title = provider.Name,
                    Subtitle = provider.UnavailableSubtitle,
                    Tags = [DockPulseHelpers.CreateTag("No data", TagTone.Warn)],
                    Details = new Details
                    {
                        Title = provider.Name,
                        Body =
                            $"![{provider.Name} Usage]({chartUri})\n\n" +
                            $"**Status:** {provider.UnavailableSubtitle}  \n" +
                            $"**Source:** {provider.PlanSubtitle}  \n" +
                            provider.DetailsNote,
                    },
                    MoreCommands =
                    [
                        showOnDockItem,
                        new CommandContextItem(new ActionCommand("Refresh All AI Usage", TriggerRefresh, new IconInfo("\uE72C"))),
                        new CommandContextItem(new OpenUriCommand($"Open {provider.Name} Settings", provider.Url, new IconInfo("\uE774"))),
                    ],
                });
                continue;
            }

            bool limitHit = (provider.FiveHour?.RemainingPercent == 0) || (provider.Weekly?.RemainingPercent == 0);
            string ageText = AiUsageService.FormatDataAge(provider.UpdatedAtUtc, nowUtc);

            var subtitleParts = new List<string>();
            if (limitHit)
            {
                subtitleParts.Add("Limit reached");
            }

            if (provider.FiveHour is not null)
            {
                string reset = provider.FiveHour.FormatResetShort(nowUtc);
                subtitleParts.Add(reset == "Reset" ? "5h reset" : $"5h resets in {reset}");
            }

            if (provider.Weekly is not null)
            {
                string reset = provider.Weekly.FormatResetShort(nowUtc);
                subtitleParts.Add(reset == "Reset" ? "Weekly reset" : $"Weekly resets in {reset}");
            }

            subtitleParts.Add(ageText);

            var tags = new List<ITag>();
            if (provider.FiveHour is not null)
            {
                tags.Add(DockPulseHelpers.CreateTag(
                    string.Create(CultureInfo.InvariantCulture, $"5h {provider.FiveHour.RemainingPercent}%"),
                    ToneForPercent(provider.FiveHour.RemainingPercent)));
            }

            if (provider.Weekly is not null)
            {
                tags.Add(DockPulseHelpers.CreateTag(
                    string.Create(CultureInfo.InvariantCulture, $"Weekly {provider.Weekly.RemainingPercent}%"),
                    ToneForPercent(provider.Weekly.RemainingPercent)));
            }

            var detailsBody = new StringBuilder();
            detailsBody.Append(CultureInfo.InvariantCulture, $"![{provider.Name} Usage]({chartUri})\n\n");
            detailsBody.Append(CultureInfo.InvariantCulture, $"**Source:** {provider.PlanSubtitle}  \n");
            if (provider.FiveHour is not null)
            {
                string reset = provider.FiveHour.FormatResetShort(nowUtc);
                string resetDesc = reset == "Reset" ? "window reset" : $"resets in {reset}";
                detailsBody.Append(CultureInfo.InvariantCulture, $"**5-Hour Window:** {provider.FiveHour.RemainingPercent}% remaining ({resetDesc})  \n");
            }
            else
            {
                detailsBody.Append("**5-Hour Window:** No data  \n");
            }

            if (provider.Weekly is not null)
            {
                string reset = provider.Weekly.FormatResetShort(nowUtc);
                string resetDesc = reset == "Reset" ? "window reset" : $"resets in {reset}";
                detailsBody.Append(CultureInfo.InvariantCulture, $"**Weekly Limit:** {provider.Weekly.RemainingPercent}% remaining ({resetDesc})  \n");
            }
            else
            {
                detailsBody.Append("**Weekly Limit:** No data  \n");
            }

            detailsBody.Append(CultureInfo.InvariantCulture, $"_{ageText}_");

            items.Add(new ListItem(new ActionCommand(
                $"Refresh {provider.Name}",
                TriggerRefresh,
                new IconInfo("\uE72C")))
            {
                Icon = AiLogos.GetIcon(provider.Id),
                Title = provider.Name,
                Subtitle = string.Join("  •  ", subtitleParts),
                Tags = [.. tags],
                Details = new Details
                {
                    Title = provider.Name,
                    Body = detailsBody.ToString(),
                },
                MoreCommands =
                [
                    showOnDockItem,
                    new CommandContextItem(new ActionCommand("Refresh All AI Usage", TriggerRefresh, new IconInfo("\uE72C"))),
                    new CommandContextItem(new OpenUriCommand($"Open {provider.Name} Usage", provider.Url, new IconInfo("\uE774"))),
                ],
            });
        }

        // Cursor is link-only: DockPulse never reads any local Cursor files or credentials
        items.Add(new ListItem(new OpenUriCommand("Open Cursor Usage", AiDockSettings.CursorUrl, new IconInfo("\uE774")))
        {
            Icon = AiLogos.GetIcon("cursor"),
            Title = "Cursor",
            Subtitle = "Usage is shown on cursor.com  •  press Enter to open",
            Tags = [DockPulseHelpers.CreateTag("Link", TagTone.Default)],
            Details = new Details
            {
                Title = "Cursor",
                Body =
                    "**Source:** cursor.com dashboard\n\n" +
                    "Cursor doesn't offer a local way to read plan usage, so DockPulse doesn't show numbers for it and never reads your Cursor sign-in.\n\n" +
                    "Press **Enter** to open your usage page in the browser.",
            },
            MoreCommands =
            [
                new CommandContextItem(new OpenUriCommand("Open Cursor Usage", AiDockSettings.CursorUrl, new IconInfo("\uE774"))),
                new CommandContextItem(new ActionCommand("Refresh All AI Usage", TriggerRefresh, new IconInfo("\uE72C"))),
            ],
        });

        items.Add(new ListItem(new CopyTextCommand(AiUsageService.GetClaudeSetupClipboardText())
        {
            Name = "Copy Setup Snippet",
        })
        {
            Icon = new IconInfo("\uE8C8"),
            Title = "Set up Claude usage",
            Subtitle = "Copy status-line script & ~/.claude/settings.json snippet to clipboard",
            Tags = [DockPulseHelpers.CreateTag("Claude Code", TagTone.Accent)],
            Details = new Details
            {
                Title = "Set up Claude usage",
                Body = AiUsageService.GetClaudeSetupMarkdown(),
            },
            MoreCommands =
            [
                new CommandContextItem(new CopyTextCommand(AiUsageService.GetClaudeStatusLineScript())
                {
                    Name = "Copy PowerShell Script Only",
                }),
                new CommandContextItem(new CopyTextCommand(AiUsageService.GetClaudeSettingsSnippet())
                {
                    Name = "Copy settings.json Snippet Only",
                }),
            ],
        });

        string fallbackTitle = tightestProvider is not null
            ? $"{tightestProvider.Name} (Tightest Limit)"
            : snapshot.Providers[0].Name;
        string fallbackUri = tightestChartUri ?? firstChartUri ?? string.Empty;

        items.Add(new ListItem(new ActionCommand("Refresh AI Usage", TriggerRefresh, new IconInfo("\uE72C")))
        {
            Icon = new IconInfo("\uE72C"),
            Title = "Refresh AI Usage",
            Subtitle = $"Last checked at {snapshot.CheckedAt.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}",
            Details = new Details
            {
                Title = fallbackTitle,
                Body = $"![AI Usage]({fallbackUri})\n\nReads local usage snapshots for Claude (`~/.claude/dockpulse-usage.json`) and Codex (`~/.codex/sessions`). Strictly offline.",
            },
        });

        return [.. items];
    }

    private static TagTone ToneForPercent(int pct) => pct switch
    {
        0 => TagTone.Danger,
        <= 25 => TagTone.Warn,
        _ => TagTone.Default,
    };

    private void TriggerRefresh()
    {
        if (OnManualRefresh is not null)
        {
            OnManualRefresh();
            return;
        }

        Refresh();
    }

    public void Refresh()
    {
        var latest = _service.GetUsageSnapshot();
        UpdateSnapshot(latest);
    }

    public void Dispose()
    {
        _dockSettings.Changed -= RebuildFromLastSnapshot;
        _dockSettings.CompareWindowChanged -= RebuildFromLastSnapshot;
    }
}

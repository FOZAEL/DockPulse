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

internal sealed partial class AiUsagePage : ListPage
{
    private readonly AiUsageService _service;
    private readonly object _itemsLock = new();
    private IListItem[] _cachedItems;

    public Action? OnManualRefresh { get; set; }

    public AiUsagePage(AiUsageService service)
    {
        _service = service;

        Id = "com.dockpulse.page.aiusage";
        Name = "AI Usage";
        Title = "AI Usage Limits";
        Icon = new IconInfo("\uE99A");
        PlaceholderText = "Filter AI providers...";
        ShowDetails = true;

        var initial = _service.GetUsageSnapshot();
        _cachedItems = BuildItems(initial);
    }

    public void UpdateSnapshot(AiUsageSnapshot snapshot)
    {
        var built = BuildItems(snapshot);
        lock (_itemsLock)
        {
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

    private IListItem[] BuildItems(AiUsageSnapshot snapshot)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var items = new List<IListItem>(snapshot.Providers.Count + 2);
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

            if (!provider.HasData)
            {
                items.Add(new ListItem(new ActionCommand(
                    $"Refresh {provider.Name}",
                    TriggerRefresh,
                    new IconInfo("\uE72C")))
                {
                    Icon = new IconInfo("\uE946"),
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
                    $"5h {provider.FiveHour.RemainingPercent}%",
                    ToneForPercent(provider.FiveHour.RemainingPercent)));
            }

            if (provider.Weekly is not null)
            {
                tags.Add(DockPulseHelpers.CreateTag(
                    $"Weekly {provider.Weekly.RemainingPercent}%",
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
                Icon = new IconInfo(limitHit ? "\uE7BA" : "\uE99A"),
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
                    new CommandContextItem(new ActionCommand("Refresh All AI Usage", TriggerRefresh, new IconInfo("\uE72C"))),
                    new CommandContextItem(new OpenUriCommand($"Open {provider.Name} Usage", provider.Url, new IconInfo("\uE774"))),
                ],
            });
        }

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
            Subtitle = $"Last checked at {snapshot.CheckedAt:HH:mm:ss}",
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
}

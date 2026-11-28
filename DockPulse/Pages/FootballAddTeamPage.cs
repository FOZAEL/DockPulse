using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DockPulse.Commands;
using DockPulse.Helpers;
using DockPulse.Services;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace DockPulse.Pages;

/// <summary>Popular picks (Ronaldo, Messi) and every team in the supported leagues, loaded from ESPN.</summary>
internal sealed partial class FootballAddTeamPage : ListPage, IDisposable
{
    private readonly FootballService _service;
    private readonly ConcurrentDictionary<string, IReadOnlyList<FootballTeam>> _loaded = new();
    private readonly ConcurrentDictionary<string, bool> _failed = new();
    private int _loading;

    public FootballAddTeamPage(FootballService service)
    {
        _service = service;
        Id = "com.dockpulse.page.football.add";
        Name = "Add Team";
        Title = "Add Team";
        Icon = new IconInfo("\uE710");
        PlaceholderText = "Search teams...";
        _service.Changed += OnChanged;
    }

    public override IListItem[] GetItems()
    {
        StartLoading();
        var items = new List<IListItem>();
        foreach (var star in FootballService.Stars)
        {
            items.Add(TeamItem(star.Team, "Popular", $"{star.Team.Name} ({star.Player})"));
        }

        foreach (var league in FootballService.Leagues)
        {
            if (_loaded.TryGetValue(league.Id, out var teams))
            {
                foreach (var team in teams)
                {
                    items.Add(TeamItem(team, league.Name, team.Name));
                }
            }
            else
            {
                bool failed = _failed.ContainsKey(league.Id);
                items.Add(new ListItem(new ActionCommand("Try Again", Retry, new IconInfo("\uE72C")))
                {
                    Section = league.Name,
                    Title = failed ? "Couldn't load teams" : "Loading teams...",
                    Subtitle = failed ? "Check your internet connection, then try again" : league.Name,
                    Icon = new IconInfo(failed ? "\uE7BA" : "\uE895"),
                });
            }
        }

        return [.. items];
    }

    private ListItem TeamItem(FootballTeam team, string section, string title)
    {
        bool added = _service.IsFollowing(team.Id);
        return new ListItem(new ActionCommand(
            added ? "Remove Team" : "Add Team",
            () =>
            {
                if (_service.IsFollowing(team.Id))
                {
                    _service.RemoveTeam(team.Id);
                    DockPulseHelpers.ShowInlineToast($"Removed {team.Name}");
                }
                else
                {
                    _service.AddTeam(team);
                    DockPulseHelpers.ShowInlineToast($"Added {team.Name}");
                }
            },
            new IconInfo(added ? "\uE74D" : "\uE710")))
        {
            Section = section,
            Title = title,
            Subtitle = FootballService.LeagueName(team.League),
            // A remote crest image here: Command Palette loads and caches it, so opening this list doesn't
            // make DockPulse download ~140 crests itself
            Icon = new IconInfo($"https://a.espncdn.com/combiner/i?img=/i/teamlogos/soccer/500/{Uri.EscapeDataString(team.Id)}.png&h=64&w=64"),
            Tags = added ? [DockPulseHelpers.CreateTag("Added", TagTone.Accent)] : [],
        };
    }

    private void Retry()
    {
        _failed.Clear();
        Interlocked.Exchange(ref _loading, 0);
        StartLoading();
    }

    private void StartLoading()
    {
        if (Interlocked.Exchange(ref _loading, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            foreach (var league in FootballService.Leagues)
            {
                if (_loaded.ContainsKey(league.Id))
                {
                    continue;
                }

                try
                {
                    _loaded[league.Id] = await _service.GetLeagueTeamsAsync(league.Id).ConfigureAwait(false);
                }
                catch
                {
                    _failed[league.Id] = true;
                }

                RaiseItemsChanged();
            }
        });
    }

    private void OnChanged() => RaiseItemsChanged();

    public void Dispose() => _service.Changed -= OnChanged;
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DockPulse.Commands;
using DockPulse.Helpers;
using DockPulse.Services;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace DockPulse.Pages;

internal sealed partial class FootballPage : ListPage, IDisposable
{
    private readonly FootballService _service;
    private readonly FootballAddTeamPage _addPage;
    private readonly ConcurrentDictionary<string, string> _cardCache = new();

    public FootballPage(FootballService service, FootballAddTeamPage addPage)
    {
        _service = service;
        _addPage = addPage;
        Id = "com.dockpulse.page.football";
        Name = "Football";
        Title = "Football";
        Icon = new IconInfo("\uE734");
        PlaceholderText = "Filter matches and teams...";
        ShowDetails = true;
        _service.Changed += OnChanged;
    }

    public override IListItem[] GetItems()
    {
        _service.EnsureStarted();
        var teams = _service.Teams;
        var items = new List<IListItem>();

        if (teams.Count == 0)
        {
            items.Add(new ListItem(_addPage)
            {
                Title = "Add your teams",
                Subtitle = "Pick clubs from the big European leagues, MLS and the Saudi Pro League",
                Icon = new IconInfo("\uE710"),
            });
            foreach (var star in FootballService.Stars)
            {
                var team = star.Team;
                items.Add(new ListItem(new ActionCommand(
                    "Add Team",
                    () =>
                    {
                        _service.AddTeam(team);
                        DockPulseHelpers.ShowInlineToast($"Added {team.Name}");
                    },
                    new IconInfo("\uE710")))
                {
                    Title = $"Follow {star.Player}",
                    Subtitle = $"Adds {team.Name} ({FootballService.LeagueName(team.League)})",
                    Icon = CrestIcon(_service, team.Id),
                });
            }

            items.Add(SourceItem());
            return [.. items];
        }

        var matches = _service.Matches;
        var now = DateTime.Now;
        foreach (var m in matches.Where(m => m.State == FootballMatchState.Live))
        {
            items.Add(MatchItem(m, "Live"));
        }

        foreach (var m in matches.Where(m => m.State == FootballMatchState.Scheduled && m.Kickoff > now.AddHours(-3)).OrderBy(m => m.Kickoff))
        {
            items.Add(MatchItem(m, "Coming up"));
        }

        foreach (var m in matches.Where(m => m.State == FootballMatchState.Final).OrderByDescending(m => m.Kickoff).Take(3))
        {
            items.Add(MatchItem(m, "Results"));
        }

        if (matches.Count == 0)
        {
            items.Add(new ListItem(new ActionCommand("Refresh Now", () => _ = _service.RefreshAsync(), new IconInfo("\uE72C")))
            {
                Section = "Matches",
                Title = _service.CheckedAt is null ? "Loading matches..." : _service.LastRefreshFailed ? "Couldn't reach ESPN" : "No matches in the next 3 weeks",
                Subtitle = _service.LastRefreshFailed ? "Check your internet connection, then refresh" : "Your teams' fixtures and results show here",
                Icon = new IconInfo(_service.LastRefreshFailed ? "\uE7BA" : "\uE734"),
            });
        }

        foreach (var team in teams)
        {
            var next = matches.Where(m => m.State == FootballMatchState.Scheduled && (m.Home.Id == team.Id || m.Away.Id == team.Id))
                              .OrderBy(m => m.Kickoff).FirstOrDefault();
            string league = FootballService.LeagueName(team.League);
            var teamId = team.Id;
            var teamName = team.Name;
            items.Add(new ListItem(new OpenUriCommand("Open Team on ESPN", $"https://www.espn.com/soccer/team/_/id/{Uri.EscapeDataString(team.Id)}", new IconInfo("\uE774")))
            {
                Section = "My teams",
                Title = team.Name,
                Subtitle = next is null ? league : $"{league}  •  next: {When(next)} vs {(next.Home.Id == team.Id ? next.Away : next.Home).ShortName}",
                Icon = CrestIcon(_service, team.Id),
                MoreCommands =
                [
                    new CommandContextItem(new ActionCommand(
                        "Remove Team",
                        () =>
                        {
                            _service.RemoveTeam(teamId);
                            DockPulseHelpers.ShowInlineToast($"Removed {teamName}");
                        },
                        new IconInfo("\uE74D"))),
                ],
            });
        }

        items.Add(new ListItem(_addPage)
        {
            Section = "My teams",
            Title = "Add Team...",
            Subtitle = "European leagues, MLS and Saudi Pro League",
            Icon = new IconInfo("\uE710"),
        });

        items.Add(new ListItem(new ActionCommand("Refresh Now", () => _ = _service.RefreshAsync(), new IconInfo("\uE72C")))
        {
            Section = "About",
            Title = "Refresh Scores",
            Subtitle = _service.CheckedAt is { } at ? $"Last checked at {at.ToString("HH:mm", CultureInfo.InvariantCulture)}" : "Checking...",
            Icon = new IconInfo("\uE72C"),
        });
        items.Add(SourceItem());
        return [.. items];
    }

    private ListItem MatchItem(FootballMatch m, string section)
    {
        string league = FootballService.LeagueName(m.League);
        string subtitle = m.State switch
        {
            FootballMatchState.Scheduled => league,
            FootballMatchState.Live => $"{league}  •  Kicked off {m.Kickoff.ToString("HH:mm", CultureInfo.InvariantCulture)}",
            _ => $"{league}  •  {When(m)}",
        };

        string extra = m.State switch
        {
            FootballMatchState.Live => $"**Live:** {FootballCardRenderer.ShortStatus(m.StatusDetail)}  \n",
            FootballMatchState.Final => "**Full time**  \n",
            _ => string.Empty,
        };

        return new ListItem(new OpenUriCommand("Open Match on ESPN", $"https://www.espn.com/soccer/match/_/gameId/{Uri.EscapeDataString(m.Id)}", new IconInfo("\uE774")))
        {
            Section = section,
            Title = MatchTitle(m),
            Subtitle = subtitle,
            Icon = CrestIcon(_service, _service.MyTeamIn(m).Id),
            Tags = [MatchTag(m)],
            Details = new Details
            {
                Title = league,
                Body =
                    $"![Match]({CardUri(m)})\n\n{extra}" +
                    $"**Kickoff:** {CalendarService.LongDate(m.Kickoff)}, {m.Kickoff.ToString("HH:mm", CultureInfo.InvariantCulture)} (your time)",
            },
            MoreCommands =
            [
                new CommandContextItem(new ActionCommand("Refresh Scores", () => _ = _service.RefreshAsync(), new IconInfo("\uE72C"))),
            ],
        };
    }

    private string CardUri(FootballMatch m)
    {
        var home = _service.GetCrestBitmap(m.Home.Id);
        var away = _service.GetCrestBitmap(m.Away.Id);
        string key = $"{m.Id}|{m.State}|{m.HomeScore}|{m.AwayScore}|{m.StatusDetail}|{m.Kickoff:HHmm}|{home is not null}|{away is not null}";
        return _cardCache.GetOrAdd(key, _ =>
            $"data:image/png;base64,{Convert.ToBase64String(FootballCardRenderer.RenderMatchPng(m, home, away))}");
    }

    internal static string MatchTitle(FootballMatch m) => m.State == FootballMatchState.Scheduled
        ? $"{m.Home.ShortName} vs {m.Away.ShortName}"
        : $"{m.Home.ShortName} {m.HomeScore}–{m.AwayScore} {m.Away.ShortName}";

    internal static ITag MatchTag(FootballMatch m) => m.State switch
    {
        FootballMatchState.Live => DockPulseHelpers.CreateTag($"LIVE {FootballCardRenderer.ShortStatus(m.StatusDetail)}", TagTone.Ok),
        FootballMatchState.Final => new Tag("FT"),
        _ => new Tag(When(m)),
    };

    /// <summary>"Today 19:30", "Tomorrow 15:15", "Thu 02:30", "Yesterday", "Sat 21 Nov".</summary>
    internal static string When(FootballMatch m)
    {
        var k = m.Kickoff;
        int dayDiff = (k.Date - DateTime.Today).Days;
        string time = k.ToString("HH:mm", CultureInfo.InvariantCulture);
        return dayDiff switch
        {
            0 => $"Today {time}",
            1 => $"Tomorrow {time}",
            -1 => "Yesterday",
            > 1 and < 7 => $"{k.ToString("ddd", CultureInfo.InvariantCulture)} {time}",
            _ => k.ToString("ddd d MMM", CultureInfo.InvariantCulture),
        };
    }

    internal static IconInfo CrestIcon(FootballService service, string teamId) =>
        service.GetCrestPath(teamId) is { } path ? new IconInfo(path) : new IconInfo("\uE734");

    private static ListItem SourceItem() =>
        new(new OpenUriCommand("Open ESPN Football", FootballService.SourceUrl, new IconInfo("\uE774")))
        {
            Section = "About",
            Title = "Scores from " + FootballService.SourceName,
            Subtitle = "Checked every 15 minutes, and every minute while one of your teams is playing",
            Icon = new IconInfo("\uE72E"),
        };

    private void OnChanged() => RaiseItemsChanged();

    public void Dispose() => _service.Changed -= OnChanged;
}

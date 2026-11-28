using System;
using DockPulse.Commands;
using DockPulse.Pages;
using DockPulse.Services;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace DockPulse.DockBands;

/// <summary>Your teams' nearest match: live score first, then a recent result, then the next kickoff.</summary>
internal sealed partial class FootballDockBandItem : ListItem, IDisposable
{
    private readonly FootballService _service;

    public FootballDockBandItem(FootballService service, FootballPage page, FootballAddTeamPage addPage)
        : base(page)
    {
        _service = service;
        MoreCommands =
        [
            new CommandContextItem(new ActionCommand("Refresh Scores", () => _ = _service.RefreshAsync(), new IconInfo("\uE72C"))),
            new CommandContextItem(addPage) { Title = "Add Team...", Icon = new IconInfo("\uE710") },
        ];

        _service.Changed += Refresh;
        Refresh();
    }

    // Command Palette reads the title when it shows the band, so ESPN isn't contacted until then
    public override string Title
    {
        get
        {
            // The base constructor can read Title before _service is set
            if (_service is not null)
            {
                _service.EnsureStarted();
            }

            return base.Title;
        }

        set => base.Title = value;
    }

    public void Refresh()
    {
        if (_service.Teams.Count == 0)
        {
            Icon = new IconInfo("\uE734");
            Title = "Football";
            Subtitle = "Add your teams";
            return;
        }

        var match = _service.GetDockMatch();
        if (match is null)
        {
            Icon = new IconInfo("\uE734");
            Title = _service.CheckedAt is null ? "Football" : "No matches";
            Subtitle = _service.CheckedAt is null ? "Loading..." : "Next 3 weeks";
            return;
        }

        Icon = FootballPage.CrestIcon(_service, _service.MyTeamIn(match).Id);
        Title = FootballPage.MatchTitle(match);
        Subtitle = match.State switch
        {
            FootballMatchState.Live => $"LIVE {FootballCardRenderer.ShortStatus(match.StatusDetail)}  •  {FootballService.LeagueName(match.League)}",
            FootballMatchState.Final => "Full time",
            _ => FootballPage.When(match),
        };
    }

    public void Dispose() => _service.Changed -= Refresh;
}

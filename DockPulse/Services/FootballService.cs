using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DockPulse.Helpers;

namespace DockPulse.Services;

public sealed record FootballTeam(string Id, string League, string Name, string ShortName, string Abbreviation);

public enum FootballMatchState
{
    Scheduled,
    Live,
    Final,
}

public sealed record FootballMatch(
    string Id,
    string League,
    DateTime Kickoff,
    FootballMatchState State,
    string StatusDetail,
    FootballTeam Home,
    FootballTeam Away,
    int HomeScore,
    int AwayScore);

public sealed record FootballLeague(string Id, string Name);

public sealed record FootballStar(string Player, FootballTeam Team);

/// <summary>
/// Scores and fixtures for the user's teams from ESPN's public soccer API, plus club crests (cached on disk).
/// Nothing is requested until <see cref="EnsureStarted"/> runs, which happens only when the Football band is
/// shown or one of its pages is opened. Then: every 15 minutes, and every minute around your teams' matches.
/// </summary>
internal sealed class FootballService : IDisposable
{
    public const string SourceName = "ESPN";
    public const string SourceUrl = "https://www.espn.com/soccer/";

    public static readonly FootballLeague[] Leagues =
    [
        new("eng.1", "Premier League"),
        new("esp.1", "LaLiga"),
        new("ita.1", "Serie A"),
        new("ger.1", "Bundesliga"),
        new("fra.1", "Ligue 1"),
        new("usa.1", "MLS"),
        new("ksa.1", "Saudi Pro League"),
    ];

    public static readonly FootballStar[] Stars =
    [
        new("Cristiano Ronaldo", new FootballTeam("817", "ksa.1", "Al Nassr", "Al Nassr", "NAS")),
        new("Lionel Messi", new FootballTeam("20232", "usa.1", "Inter Miami CF", "Miami", "MIA")),
    ];

    private const string ApiBase = "https://site.api.espn.com/apis/site/v2/sports/soccer/";
    private static readonly TimeSpan SlowInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan FastInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ResultShownFor = TimeSpan.FromHours(14); // ~12 h after a 2 h match

    private readonly HttpClient _http;
    private readonly Timer _slowTimer;
    private readonly Timer _fastTimer;
    private readonly object _lock = new();
    private readonly ConcurrentDictionary<string, IReadOnlyList<FootballTeam>> _catalog = new();
    private readonly ConcurrentDictionary<string, AiLogos.LogoBitmap?> _crestBitmaps = new();
    private readonly ConcurrentDictionary<string, byte> _crestDownloads = new();
    private List<FootballTeam> _teams = [];
    private List<FootballMatch> _matches = [];
    private DateTime? _checkedAt;
    private bool _lastFailed;
    private int _started;
    private int _isRefreshing;

    public event Action? Changed;

    public FootballService()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("DockPulse");
        _slowTimer = new Timer(_ => _ = RefreshAsync(), null, Timeout.Infinite, Timeout.Infinite);
        _fastTimer = new Timer(_ => _ = RefreshLiveIfNeededAsync(), null, Timeout.Infinite, Timeout.Infinite);
        LoadTeams();
    }

    public IReadOnlyList<FootballTeam> Teams
    {
        get { lock (_lock) { return [.. _teams]; } }
    }

    public IReadOnlyList<FootballMatch> Matches
    {
        get { lock (_lock) { return [.. _matches]; } }
    }

    public DateTime? CheckedAt
    {
        get { lock (_lock) { return _checkedAt; } }
    }

    public bool LastRefreshFailed
    {
        get { lock (_lock) { return _lastFailed; } }
    }

    public static string LeagueName(string leagueId) =>
        Leagues.FirstOrDefault(l => l.Id == leagueId)?.Name ?? leagueId;

    public bool IsFollowing(string teamId)
    {
        lock (_lock)
        {
            return _teams.Any(t => t.Id == teamId);
        }
    }

    public void AddTeam(FootballTeam team)
    {
        lock (_lock)
        {
            if (_teams.Any(t => t.Id == team.Id))
            {
                return;
            }

            _teams.Add(team);
        }

        SaveTeams();
        RaiseChanged();
        _ = RefreshAsync();
    }

    public void RemoveTeam(string teamId)
    {
        lock (_lock)
        {
            _teams.RemoveAll(t => t.Id == teamId);
            _matches.RemoveAll(m => !_teams.Any(t => t.Id == m.Home.Id || t.Id == m.Away.Id));
        }

        SaveTeams();
        RaiseChanged();
    }

    public void EnsureStarted()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }

        _slowTimer.Change(TimeSpan.Zero, SlowInterval);
        _fastTimer.Change(FastInterval, FastInterval);
    }

    /// <summary>A live match first, then a result from the last ~12 hours, then the next match.</summary>
    public FootballMatch? GetDockMatch()
    {
        var now = DateTime.Now;
        var matches = Matches;
        return matches.FirstOrDefault(m => m.State == FootballMatchState.Live)
            ?? matches.Where(m => m.State == FootballMatchState.Final && now - m.Kickoff < ResultShownFor)
                      .OrderByDescending(m => m.Kickoff).FirstOrDefault()
            ?? matches.Where(m => m.State == FootballMatchState.Scheduled && m.Kickoff > now.AddHours(-3))
                      .OrderBy(m => m.Kickoff).FirstOrDefault();
    }

    /// <summary>The followed team in a match (for its crest), or the home team.</summary>
    public FootballTeam MyTeamIn(FootballMatch match) => IsFollowing(match.Home.Id) ? match.Home : match.Away;

    public async Task RefreshAsync()
    {
        EnsureStarted();
        if (Interlocked.Exchange(ref _isRefreshing, 1) == 1)
        {
            return;
        }

        try
        {
            var teams = Teams;
            var byId = new Dictionary<string, FootballMatch>();
            bool failed = false;
            foreach (var team in teams)
            {
                try
                {
                    foreach (var m in await FetchScheduleAsync(team, fixtures: true).ConfigureAwait(false))
                    {
                        byId[m.Id] = m;
                    }

                    foreach (var m in await FetchScheduleAsync(team, fixtures: false).ConfigureAwait(false))
                    {
                        byId.TryAdd(m.Id, m);
                    }
                }
                catch
                {
                    failed = true;
                }
            }

            var now = DateTime.Now;
            var kept = new List<FootballMatch>();
            foreach (var team in teams)
            {
                var mine = byId.Values.Where(m => m.Home.Id == team.Id || m.Away.Id == team.Id).ToList();
                kept.AddRange(mine.Where(m => m.State != FootballMatchState.Final && m.Kickoff < now.AddDays(21)).OrderBy(m => m.Kickoff).Take(3));
                kept.AddRange(mine.Where(m => m.State == FootballMatchState.Final).OrderByDescending(m => m.Kickoff).Take(2));
            }

            var merged = kept.GroupBy(m => m.Id).Select(g => g.First()).OrderBy(m => m.Kickoff).ToList();
            lock (_lock)
            {
                if (!failed || merged.Count > 0)
                {
                    _matches = merged;
                }

                _checkedAt = DateTime.Now;
                _lastFailed = failed;
            }

            await UpdateLiveAsync().ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _isRefreshing, 0);
        }

        RaiseChanged();
    }

    /// <summary>All teams of a league for the Add Team page (cached for the session).</summary>
    public async Task<IReadOnlyList<FootballTeam>> GetLeagueTeamsAsync(string leagueId)
    {
        EnsureStarted();
        if (_catalog.TryGetValue(leagueId, out var cached))
        {
            return cached;
        }

        string json = await _http.GetStringAsync($"{ApiBase}{leagueId}/teams").ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var list = new List<FootballTeam>();
        var teams = doc.RootElement.GetProperty("sports")[0].GetProperty("leagues")[0].GetProperty("teams");
        foreach (var entry in teams.EnumerateArray())
        {
            if (entry.TryGetProperty("team", out var t) && ReadTeam(t, leagueId) is { } team)
            {
                list.Add(team);
            }
        }

        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        _catalog[leagueId] = list;
        return list;
    }

    /// <summary>Local path of a team's crest, or null while it downloads (Changed fires when it's ready).</summary>
    public string? GetCrestPath(string teamId)
    {
        string path = CrestFile(teamId);
        if (File.Exists(path))
        {
            return path;
        }

        if (_crestDownloads.TryAdd(teamId, 0))
        {
            _ = DownloadCrestAsync(teamId, path);
        }

        return null;
    }

    /// <summary>Decoded crest for drawing on the match card (null until downloaded).</summary>
    public AiLogos.LogoBitmap? GetCrestBitmap(string teamId)
    {
        if (_crestBitmaps.TryGetValue(teamId, out var cached) && cached is not null)
        {
            return cached;
        }

        string? path = GetCrestPath(teamId);
        if (path is null)
        {
            return null;
        }

        try
        {
            if (AiLogos.TryDecodePng(File.ReadAllBytes(path), out var rgba, out int w, out int h))
            {
                var bitmap = new AiLogos.LogoBitmap(rgba, w, h);
                _crestBitmaps[teamId] = bitmap;
                return bitmap;
            }
        }
        catch
        {
            // Unreadable crest: draw without it
        }

        return null;
    }

    private async Task DownloadCrestAsync(string teamId, string path)
    {
        try
        {
            string url = $"https://a.espncdn.com/combiner/i?img=/i/teamlogos/soccer/500/{Uri.EscapeDataString(teamId)}.png&h=64&w=64";
            byte[] bytes = await _http.GetByteArrayAsync(url).ConfigureAwait(false);
            if (bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == 0x50)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllBytesAsync(path, bytes).ConfigureAwait(false);
                RaiseChanged();
            }
        }
        catch
        {
            // Try again next time the crest is asked for
            _crestDownloads.TryRemove(teamId, out _);
        }
    }

    private static string CrestFile(string teamId)
    {
        string safe = new string(teamId.Where(char.IsLetterOrDigit).ToArray());
        string dir = Path.Combine(Path.GetDirectoryName(DockPulseHelpers.GetSettingsFilePath("football.cfg"))!, "crests");
        return Path.Combine(dir, safe + ".png");
    }

    private async Task RefreshLiveIfNeededAsync()
    {
        if (!Matches.Any(InLiveWindow))
        {
            return;
        }

        await UpdateLiveAsync().ConfigureAwait(false);
        RaiseChanged();
    }

    private static bool InLiveWindow(FootballMatch m)
    {
        var now = DateTime.Now;
        return m.State != FootballMatchState.Final && m.Kickoff <= now.AddMinutes(5) && m.Kickoff > now.AddHours(-4);
    }

    /// <summary>Updates matches around kickoff from the league scoreboard, which has the live minute and score.</summary>
    private async Task UpdateLiveAsync()
    {
        var candidates = Matches.Where(InLiveWindow).ToList();
        foreach (var group in candidates.GroupBy(m => (m.League, Day: m.Kickoff.ToUniversalTime().ToString("yyyyMMdd", CultureInfo.InvariantCulture))))
        {
            try
            {
                string json = await _http.GetStringAsync($"{ApiBase}{group.Key.League}/scoreboard?dates={group.Key.Day}").ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("events", out var events))
                {
                    continue;
                }

                foreach (var e in events.EnumerateArray())
                {
                    if (ParseEvent(e, group.Key.League) is not { } updated)
                    {
                        continue;
                    }

                    lock (_lock)
                    {
                        int i = _matches.FindIndex(m => m.Id == updated.Id);
                        if (i >= 0)
                        {
                            _matches[i] = updated;
                        }
                    }
                }
            }
            catch
            {
                // Keep the last known state
            }
        }
    }

    private async Task<List<FootballMatch>> FetchScheduleAsync(FootballTeam team, bool fixtures)
    {
        string url = $"{ApiBase}{team.League}/teams/{Uri.EscapeDataString(team.Id)}/schedule" + (fixtures ? "?fixture=true" : string.Empty);
        string json = await _http.GetStringAsync(url).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var list = new List<FootballMatch>();
        if (doc.RootElement.TryGetProperty("events", out var events))
        {
            foreach (var e in events.EnumerateArray())
            {
                if (ParseEvent(e, team.League) is { } m)
                {
                    list.Add(m);
                }
            }
        }

        return list;
    }

    private static FootballMatch? ParseEvent(JsonElement e, string league)
    {
        if (!DockPulseHelpers.TryReadString(e, "id", out var id) ||
            !DockPulseHelpers.TryReadString(e, "date", out var date) ||
            !DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var kickoff) ||
            !e.TryGetProperty("competitions", out var comps) || comps.GetArrayLength() == 0)
        {
            return null;
        }

        var comp = comps[0];
        FootballTeam? home = null, away = null;
        int homeScore = 0, awayScore = 0;
        foreach (var c in comp.GetProperty("competitors").EnumerateArray())
        {
            if (!c.TryGetProperty("team", out var t) || ReadTeam(t, league) is not { } team)
            {
                continue;
            }

            int score = ReadScore(c);
            if (DockPulseHelpers.TryReadString(c, "homeAway", out var side) && side == "away")
            {
                away = team;
                awayScore = score;
            }
            else
            {
                home = team;
                homeScore = score;
            }
        }

        if (home is null || away is null)
        {
            return null;
        }

        var status = comp.TryGetProperty("status", out var s1) ? s1 : e.TryGetProperty("status", out var s2) ? s2 : default;
        string stateText = string.Empty, detail = string.Empty;
        if (status.ValueKind == JsonValueKind.Object && status.TryGetProperty("type", out var type))
        {
            DockPulseHelpers.TryReadString(type, "state", out stateText);
            DockPulseHelpers.TryReadString(type, "shortDetail", out detail);
        }

        if (string.IsNullOrWhiteSpace(detail) && status.ValueKind == JsonValueKind.Object)
        {
            DockPulseHelpers.TryReadString(status, "displayClock", out detail);
        }

        var state = stateText switch
        {
            "in" => FootballMatchState.Live,
            "post" => FootballMatchState.Final,
            _ => FootballMatchState.Scheduled,
        };

        return new FootballMatch(id, league, kickoff.LocalDateTime, state, detail ?? string.Empty, home, away, homeScore, awayScore);
    }

    private static int ReadScore(JsonElement competitor)
    {
        if (!competitor.TryGetProperty("score", out var score))
        {
            return 0;
        }

        string? text = score.ValueKind switch
        {
            JsonValueKind.String => score.GetString(),
            JsonValueKind.Object when score.TryGetProperty("displayValue", out var dv) => dv.GetString(),
            JsonValueKind.Number => score.GetRawText(),
            _ => null,
        };

        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : 0;
    }

    private static FootballTeam? ReadTeam(JsonElement t, string league)
    {
        if (!DockPulseHelpers.TryReadString(t, "id", out var id) || string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        DockPulseHelpers.TryReadString(t, "displayName", out var name);
        DockPulseHelpers.TryReadString(t, "shortDisplayName", out var shortName);
        DockPulseHelpers.TryReadString(t, "abbreviation", out var abbr);
        name = string.IsNullOrWhiteSpace(name) ? shortName : name;
        return new FootballTeam(
            id,
            league,
            name ?? id,
            string.IsNullOrWhiteSpace(shortName) ? name ?? id : shortName,
            abbr ?? string.Empty);
    }

    private static string SettingsFile => DockPulseHelpers.GetSettingsFilePath("football.cfg");

    private void LoadTeams()
    {
        try
        {
            if (!File.Exists(SettingsFile))
            {
                return;
            }

            var teams = new List<FootballTeam>();
            foreach (var line in File.ReadAllLines(SettingsFile))
            {
                if (!line.StartsWith("team=", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var parts = line[5..].Split('|');
                if (parts.Length >= 5 && !teams.Any(t => t.Id == parts[1]))
                {
                    teams.Add(new FootballTeam(parts[1], parts[0], parts[2], parts[3], parts[4]));
                }
            }

            _teams = teams;
        }
        catch
        {
            // Ignore unreadable settings
        }
    }

    private void SaveTeams()
    {
        try
        {
            var teams = Teams;
            File.WriteAllLines(SettingsFile, teams.Select(t =>
                $"team={t.League}|{t.Id}|{Clean(t.Name)}|{Clean(t.ShortName)}|{Clean(t.Abbreviation)}"));
        }
        catch
        {
            // Ignore write errors
        }

        static string Clean(string value) => value.Replace('|', '/').Replace('\n', ' ').Replace('\r', ' ');
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch
        {
            // Ignore subscriber errors
        }
    }

    public void Dispose()
    {
        _slowTimer.Dispose();
        _fastTimer.Dispose();
        _http.Dispose();
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DockPulse.Services;

public sealed record AiLimitWindow(
    string WindowShort,
    string Label,
    int RemainingPercent,
    DateTimeOffset? ResetsAtUtc,
    bool ResetPassed)
{
    public string FormatResetShort(DateTimeOffset nowUtc)
    {
        if (ResetPassed || ResetsAtUtc is null)
        {
            return "Reset";
        }

        var remaining = ResetsAtUtc.Value - nowUtc;
        if (remaining <= TimeSpan.Zero)
        {
            return "Reset";
        }

        if (remaining.TotalDays >= 1.0)
        {
            int days = (int)remaining.TotalDays;
            int hours = remaining.Hours;
            return $"{days}d {hours}h";
        }

        if (remaining.TotalHours >= 1.0)
        {
            int hours = (int)remaining.TotalHours;
            int mins = remaining.Minutes;
            return $"{hours}h {mins}m";
        }

        int totalMins = Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes));
        return $"{totalMins}m";
    }
}

public sealed record AiProviderUsage(
    string Id,
    string Name,
    string PlanSubtitle,
    bool HasData,
    AiLimitWindow? FiveHour,
    AiLimitWindow? Weekly,
    DateTimeOffset? UpdatedAtUtc,
    string UnavailableSubtitle,
    string UnavailableCardText,
    string DetailsNote,
    string Url);

public sealed record AiTightestLimit(
    string ProviderId,
    string ProviderName,
    string WindowShort,
    int RemainingPercent,
    string ResetShort);

public sealed record AiUsageSnapshot(
    DateTimeOffset CheckedAt,
    IReadOnlyList<AiProviderUsage> Providers,
    AiTightestLimit? Tightest);

public sealed partial class AiUsageService
{
    private const int MaxCodexFilesToScan = 15;

    // Each CLI /usage call contacts the provider, so run it at most this often (manual refresh skips the wait)
    private static readonly TimeSpan CliRefreshInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CliTimeout = TimeSpan.FromSeconds(30);

    private readonly BackgroundCliReader _agyReader;
    private readonly BackgroundCliReader _claudeReader;

    public AiUsageService()
    {
        _agyReader = new BackgroundCliReader(FetchAgyUsage, RaiseUpdated);
        _claudeReader = new BackgroundCliReader(FetchClaudeCliUsage, RaiseUpdated);
    }

    /// <summary>Raised on a thread-pool thread when a background CLI read (Claude or Antigravity) finishes.</summary>
    public event Action? Updated;

    private void RaiseUpdated()
    {
        try
        {
            Updated?.Invoke();
        }
        catch
        {
            // Ignore subscriber errors
        }
    }

    public AiUsageSnapshot GetUsageSnapshot(bool forceRefresh = false)
    {
        var nowUtc = DateTimeOffset.UtcNow;

        var claude = GetClaudeUsage(nowUtc, forceRefresh);
        var antigravity = GetAntigravityUsage(nowUtc, forceRefresh);
        var codex = ReadCodexUsage(nowUtc);

        AiProviderUsage[] providers = [claude, antigravity, codex];

        AiTightestLimit? tightest = null;
        foreach (var provider in providers)
        {
            if (!provider.HasData)
            {
                continue;
            }

            ConsiderWindow(provider, provider.FiveHour, nowUtc, ref tightest);
            ConsiderWindow(provider, provider.Weekly, nowUtc, ref tightest);
        }

        return new AiUsageSnapshot(DateTimeOffset.Now, providers, tightest);
    }

    private static void ConsiderWindow(
        AiProviderUsage provider,
        AiLimitWindow? window,
        DateTimeOffset nowUtc,
        ref AiTightestLimit? tightest)
    {
        if (window is null)
        {
            return;
        }

        if (tightest is null || window.RemainingPercent < tightest.RemainingPercent)
        {
            tightest = new AiTightestLimit(
                provider.Id,
                provider.Name,
                window.WindowShort,
                window.RemainingPercent,
                window.FormatResetShort(nowUtc));
        }
    }

    private static AiProviderUsage ReadClaudeUsage(DateTimeOffset nowUtc)
    {
        const string plan = "Claude Code • Local statusLine";
        const string url = "https://claude.ai/settings/usage";
        string cachePath = GetClaudeCachePath();

        if (!File.Exists(cachePath))
        {
            return new AiProviderUsage(
                Id: "claude",
                Name: "Claude",
                PlanSubtitle: plan,
                HasData: false,
                FiveHour: null,
                Weekly: null,
                UpdatedAtUtc: null,
                UnavailableSubtitle: "Not configured • Run 'Set up Claude usage' below",
                UnavailableCardText: "RUN 'SET UP CLAUDE USAGE' BELOW",
                DetailsNote: $"Local cache `{cachePath}` was not found. Select **Set up Claude usage** below to copy the status-line script and `~/.claude/settings.json` snippet.",
                Url: url);
        }

        try
        {
            string json = ReadAllTextShared(cachePath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return BuildClaudeNoData(plan, url, cachePath);
            }

            var updatedAt = TryParseTimestamp(root, "updated_at")
                            ?? new DateTimeOffset(File.GetLastWriteTimeUtc(cachePath), TimeSpan.Zero);

            var rateLimits = root.TryGetProperty("rate_limits", out var rl) && rl.ValueKind == JsonValueKind.Object
                ? rl
                : root;

            AiLimitWindow? fiveHour = null;
            if (rateLimits.TryGetProperty("five_hour", out var fh) && fh.ValueKind == JsonValueKind.Object)
            {
                fiveHour = ParseWindow(fh, "5h", "5-Hour Window", nowUtc, fallbackTimestampUtc: updatedAt);
            }

            AiLimitWindow? sevenDay = null;
            if (rateLimits.TryGetProperty("seven_day", out var sd) && sd.ValueKind == JsonValueKind.Object)
            {
                sevenDay = ParseWindow(sd, "Wk", "Weekly Limit", nowUtc, fallbackTimestampUtc: updatedAt);
            }

            if (fiveHour is null && sevenDay is null)
            {
                return BuildClaudeNoData(plan, url, cachePath);
            }

            return new AiProviderUsage(
                Id: "claude",
                Name: "Claude",
                PlanSubtitle: plan,
                HasData: true,
                FiveHour: fiveHour,
                Weekly: sevenDay,
                UpdatedAtUtc: updatedAt,
                UnavailableSubtitle: string.Empty,
                UnavailableCardText: string.Empty,
                DetailsNote: $"Source: `{cachePath}`",
                Url: url);
        }
        catch
        {
            return BuildClaudeNoData(plan, url, cachePath);
        }
    }

    private static AiProviderUsage BuildClaudeNoData(string plan, string url, string cachePath) => new(
        Id: "claude",
        Name: "Claude",
        PlanSubtitle: plan,
        HasData: false,
        FiveHour: null,
        Weekly: null,
        UpdatedAtUtc: null,
        UnavailableSubtitle: "No rate-limit data yet • Run a prompt in Claude Code",
        UnavailableCardText: "NO RATE-LIMIT DATA YET",
        DetailsNote: $"Found `{cachePath}` without rate-limit windows. Rate limits appear after the first API response for Pro/Max/Ultra subscribers.",
        Url: url);

    private const string AgyPlanPrefix = "Antigravity CLI";
    private const string AgyUrl = "https://antigravity.google";

    /// <summary>
    /// Runs one CLI usage read in the background at most every <see cref="CliRefreshInterval"/> and keeps
    /// the last good result, so callers never wait on the CLI.
    /// </summary>
    private sealed class BackgroundCliReader(Func<AiProviderUsage> fetch, Action onUpdated)
    {
        private readonly object _lock = new();
        private AiProviderUsage? _lastGood;
        private AiProviderUsage? _lastStatus;
        private DateTimeOffset _lastAttemptUtc = DateTimeOffset.MinValue;
        private int _fetching;

        /// <summary>Returns the last good reading and the last failure status, starting a new read when due.</summary>
        public (AiProviderUsage? Good, AiProviderUsage? Status) Get(DateTimeOffset nowUtc, bool forceRefresh)
        {
            AiProviderUsage? good;
            AiProviderUsage? status;
            bool due;
            lock (_lock)
            {
                good = _lastGood;
                status = _lastStatus;
                due = forceRefresh || nowUtc - _lastAttemptUtc >= CliRefreshInterval;
            }

            if (due)
            {
                Start();
            }

            return (good, status);
        }

        private void Start()
        {
            if (Interlocked.Exchange(ref _fetching, 1) == 1)
            {
                return;
            }

            lock (_lock)
            {
                _lastAttemptUtc = DateTimeOffset.UtcNow;
            }

            _ = Task.Run(() =>
            {
                try
                {
                    var result = fetch();
                    lock (_lock)
                    {
                        if (result.HasData)
                        {
                            _lastGood = result;
                            _lastStatus = null;
                        }
                        else
                        {
                            // Keep showing the last good numbers (with their age) if a later read fails
                            _lastStatus = result;
                        }
                    }
                }
                catch
                {
                    // Never let a failed CLI run take the extension down
                }
                finally
                {
                    Interlocked.Exchange(ref _fetching, 0);
                }

                onUpdated();
            });
        }
    }

    private readonly record struct CliRun(bool Started, bool TimedOut, int ExitCode, string Stdout);

    private static CliRun RunCli(string exe, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        foreach (string arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var proc = Process.Start(psi);
        if (proc is null)
        {
            return new CliRun(false, false, -1, string.Empty);
        }

        // Read both streams while waiting so a full pipe can never block the CLI
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();

        if (!proc.WaitForExit((int)CliTimeout.TotalMilliseconds))
        {
            try
            {
                proc.Kill(entireProcessTree: true);
            }
            catch
            {
                // Already gone
            }

            return new CliRun(true, true, -1, string.Empty);
        }

        string stdout = stdoutTask.GetAwaiter().GetResult();
        _ = stderrTask.GetAwaiter().GetResult();
        return new CliRun(true, false, proc.ExitCode, stdout);
    }

    private static string? FindOnPath(string exeName, long minBytes = 0)
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        foreach (string dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                string candidate = Path.Combine(dir, exeName);
                if (File.Exists(candidate) && new FileInfo(candidate).Length >= minBytes)
                {
                    return candidate;
                }
            }
            catch
            {
                // Skip malformed PATH entries
            }
        }

        return null;
    }

    private static AiProviderUsage RollCached(AiProviderUsage usage, DateTimeOffset nowUtc) => usage with
    {
        FiveHour = RollWindow(usage.FiveHour, nowUtc),
        Weekly = RollWindow(usage.Weekly, nowUtc),
    };

    // A cached window whose reset time has passed is full again
    private static AiLimitWindow? RollWindow(AiLimitWindow? window, DateTimeOffset nowUtc)
    {
        if (window is null || window.ResetPassed || window.ResetsAtUtc is null || window.ResetsAtUtc.Value > nowUtc)
        {
            return window;
        }

        return window with { RemainingPercent = 100, ResetPassed = true };
    }

    // ---------------- Claude: `claude -p /usage`, with the status-line file as a fallback ----------------

    private const string ClaudeCliPlan = "Claude Code CLI • /usage";
    private const string ClaudeCliNotFound = "claude CLI not found";

    // Claude usage covers the whole subscription (Claude app, web and Claude Code), so reading it from the
    // CLI keeps it current even when you only use the desktop app. The status-line cache can be fresher
    // right after a terminal session, so the newer of the two wins.
    private AiProviderUsage GetClaudeUsage(DateTimeOffset nowUtc, bool forceRefresh)
    {
        var fromStatusLine = ReadClaudeUsage(nowUtc);
        var (good, status) = _claudeReader.Get(nowUtc, forceRefresh);

        if (good is not null)
        {
            var fromCli = RollCached(good, nowUtc);
            return fromStatusLine.HasData && fromStatusLine.UpdatedAtUtc > fromCli.UpdatedAtUtc ? fromStatusLine : fromCli;
        }

        if (fromStatusLine.HasData)
        {
            return fromStatusLine;
        }

        if (status is null)
        {
            return BuildClaudeCliStatus(
                "Checking /usage in Claude Code…",
                "CHECKING /USAGE",
                "DockPulse is reading your limits with `claude -p /usage`. This takes a few seconds.");
        }

        // No CLI: fall back to the status-line setup hint
        return string.Equals(status.UnavailableSubtitle, ClaudeCliNotFound, StringComparison.Ordinal)
            ? fromStatusLine
            : status;
    }

    private static AiProviderUsage FetchClaudeCliUsage()
    {
        string? exe = FindClaudeExecutable();
        if (exe is null)
        {
            return BuildClaudeCliStatus(
                ClaudeCliNotFound,
                "CLAUDE CLI NOT FOUND",
                "Install Claude Code (`npm install -g @anthropic-ai/claude-code`) and sign in once, or set up the status line below.");
        }

        // --no-session-persistence: /usage is a local command, and this keeps it out of your session history
        var run = RunCli(exe, ["-p", "/usage", "--output-format", "json", "--no-session-persistence"]);
        if (!run.Started)
        {
            return BuildClaudeCliStatus("Couldn't start Claude Code", "COULDN'T START CLAUDE", $"Starting `{exe}` failed.");
        }

        if (run.TimedOut)
        {
            return BuildClaudeCliStatus(
                "Claude /usage timed out",
                "CLAUDE /USAGE TIMED OUT",
                $"`claude -p /usage` did not answer within {(int)CliTimeout.TotalSeconds} seconds. DockPulse will try again in a few minutes.");
        }

        if (run.ExitCode != 0)
        {
            return BuildClaudeCliStatus(
                "Couldn't read /usage from Claude Code",
                "COULDN'T READ /USAGE",
                $"`claude -p /usage` exited with code {run.ExitCode}. Run `claude` once in a terminal to check that you're signed in.");
        }

        return ParseClaudeUsage(run.Stdout, DateTimeOffset.UtcNow)
               ?? BuildClaudeCliStatus(
                   "No plan limits in Claude /usage",
                   "NO PLAN LIMITS",
                   "`claude -p /usage` didn't list session or weekly limits. They appear for Claude Pro, Max and Ultra sign-ins (not API keys).");
    }

    private static string? FindClaudeExecutable()
    {
        // The npm package's bin\claude.exe is a tiny placeholder until its install finishes, hence the size check
        const long MinRealExe = 1_000_000;
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] candidates =
        [
            Path.Combine(home, ".local", "bin", "claude.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe"),
        ];

        foreach (string candidate in candidates)
        {
            try
            {
                if (File.Exists(candidate) && new FileInfo(candidate).Length >= MinRealExe)
                {
                    return candidate;
                }
            }
            catch
            {
                // Try the next location
            }
        }

        return FindOnPath("claude.exe", MinRealExe);
    }

    [GeneratedRegex(@"^\s*Current session:\s*(?<pct>\d+(?:\.\d+)?)\s*%\s*used(?:\s*[·•-]\s*resets\s+(?<when>[^(\r\n]+?)\s*(?:\((?<tz>[^)\r\n]+)\))?)?\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ClaudeSessionLine();

    [GeneratedRegex(@"^\s*Current week \(all models\):\s*(?<pct>\d+(?:\.\d+)?)\s*%\s*used(?:\s*[·•-]\s*resets\s+(?<when>[^(\r\n]+?)\s*(?:\((?<tz>[^)\r\n]+)\))?)?\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ClaudeWeekAllModelsLine();

    [GeneratedRegex(@"^\s*Current week[^:\r\n]*:\s*(?<pct>\d+(?:\.\d+)?)\s*%\s*used(?:\s*[·•-]\s*resets\s+(?<when>[^(\r\n]+?)\s*(?:\((?<tz>[^)\r\n]+)\))?)?\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ClaudeWeekAnyLine();

    // `claude -p /usage --output-format json` prints {"local_command":"usage","result":"..."} where result has lines like
    //   Current session: 88% used · resets Nov 27, 12:49am (Asia/Kabul)
    //   Current week (all models): 43% used · resets Nov 30, 5:29pm (Asia/Kabul)
    internal static AiProviderUsage? ParseClaudeUsage(string stdout, DateTimeOffset nowUtc)
    {
        string? text = null;
        string[] lines = stdout.Split('\n');
        for (int li = lines.Length - 1; li >= 0 && text is null; li--)
        {
            string line = lines[li].Trim();
            if (!line.StartsWith('{'))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.String)
                {
                    text = result.GetString();
                }
            }
            catch
            {
                // Not the result line
            }
        }

        // Plain-text output (no --output-format json) is the result text itself
        text ??= stdout;

        AiLimitWindow? fiveHour = ParseClaudeWindow(ClaudeSessionLine().Match(text), "5h", "5-Hour Window", nowUtc);
        var weekMatch = ClaudeWeekAllModelsLine().Match(text);
        if (!weekMatch.Success)
        {
            weekMatch = ClaudeWeekAnyLine().Match(text);
        }

        AiLimitWindow? weekly = ParseClaudeWindow(weekMatch, "Wk", "Weekly Limit", nowUtc);
        if (fiveHour is null && weekly is null)
        {
            return null;
        }

        return new AiProviderUsage(
            Id: "claude",
            Name: "Claude",
            PlanSubtitle: ClaudeCliPlan,
            HasData: true,
            FiveHour: fiveHour,
            Weekly: weekly,
            UpdatedAtUtc: nowUtc,
            UnavailableSubtitle: string.Empty,
            UnavailableCardText: string.Empty,
            DetailsNote: "Source: `claude -p /usage`",
            Url: "https://claude.ai/settings/usage");
    }

    private static AiLimitWindow? ParseClaudeWindow(Match match, string windowShort, string label, DateTimeOffset nowUtc)
    {
        if (!match.Success ||
            !double.TryParse(match.Groups["pct"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double used))
        {
            return null;
        }

        DateTimeOffset? resetsAt = match.Groups["when"].Success
            ? ParseClaudeResetTime(match.Groups["when"].Value, match.Groups["tz"].Success ? match.Groups["tz"].Value : null, nowUtc)
            : null;
        bool resetPassed = resetsAt is not null && resetsAt.Value <= nowUtc;
        int remaining = resetPassed ? 100 : Math.Clamp((int)Math.Round(100.0 - used), 0, 100);
        return new AiLimitWindow(windowShort, label, remaining, resetsAt, resetPassed);
    }

    // Reset times look like "Nov 27, 12:49am" or "5:29pm" in the CLI's time zone ("Asia/Kabul"), without a year
    internal static DateTimeOffset? ParseClaudeResetTime(string when, string? timeZoneId, DateTimeOffset nowUtc)
    {
        string text = when.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        var tz = TimeZoneInfo.Local;
        if (!string.IsNullOrWhiteSpace(timeZoneId) &&
            TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId.Trim(), out var found))
        {
            tz = found;
        }

        string[] withDate = ["MMM d, h:mmtt", "MMM d, htt", "MMM d, h:mm tt", "MMM d, h tt", "MMM d h:mmtt", "MMM d htt", "MMM d"];
        string[] timeOnly = ["h:mmtt", "htt", "h:mm tt", "h tt", "H:mm"];
        var nowInTz = TimeZoneInfo.ConvertTime(nowUtc, tz).DateTime;
        DateTime local;

        if (DateTime.TryParseExact(text, withDate, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed))
        {
            local = new DateTime(nowInTz.Year, parsed.Month, parsed.Day, parsed.Hour, parsed.Minute, 0, DateTimeKind.Unspecified);
            if (local < nowInTz.AddDays(-2))
            {
                local = local.AddYears(1); // e.g. "Jan 2" seen on Dec 30
            }
        }
        else if (DateTime.TryParseExact(text, timeOnly, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out parsed))
        {
            local = new DateTime(nowInTz.Year, nowInTz.Month, nowInTz.Day, parsed.Hour, parsed.Minute, 0, DateTimeKind.Unspecified);
            if (local < nowInTz)
            {
                local = local.AddDays(1);
            }
        }
        else
        {
            return null;
        }

        try
        {
            return new DateTimeOffset(local, tz.GetUtcOffset(local)).ToUniversalTime();
        }
        catch
        {
            return null;
        }
    }

    private static AiProviderUsage BuildClaudeCliStatus(string subtitle, string cardText, string note) => new(
        Id: "claude",
        Name: "Claude",
        PlanSubtitle: ClaudeCliPlan,
        HasData: false,
        FiveHour: null,
        Weekly: null,
        UpdatedAtUtc: null,
        UnavailableSubtitle: subtitle,
        UnavailableCardText: cardText,
        DetailsNote: note,
        Url: "https://claude.ai/settings/usage");

    // ---------------- Antigravity: `agy -p /usage` ----------------

    // Returns the last Antigravity reading at once and, when it is due, starts a background
    // `agy -p /usage` run; Updated fires when that run finishes so the band can redraw.
    private AiProviderUsage GetAntigravityUsage(DateTimeOffset nowUtc, bool forceRefresh)
    {
        var (good, status) = _agyReader.Get(nowUtc, forceRefresh);
        if (good is not null)
        {
            return RollCached(good, nowUtc);
        }

        return status ?? BuildAntigravityStatus(
            "Checking /usage in agy…",
            "CHECKING /USAGE IN AGY",
            "DockPulse is reading your limits with `agy -p /usage`. This takes a few seconds.");
    }

    private static AiProviderUsage FetchAgyUsage()
    {
        string? exe = FindAgyExecutable();
        if (exe is null)
        {
            return BuildAntigravityStatus(
                "agy CLI not found",
                "AGY CLI NOT FOUND",
                "Install the Antigravity CLI (`agy`) and sign in once. DockPulse reads your limits from its `/usage` command.");
        }

        var run = RunCli(exe, ["-p", "/usage", "--output-format", "json", "--print-timeout", "25s"]);
        if (!run.Started)
        {
            return BuildAntigravityStatus(
                "Couldn't start agy",
                "COULDN'T START AGY",
                $"Starting `{exe}` failed.");
        }

        if (run.TimedOut)
        {
            return BuildAntigravityStatus(
                "agy /usage timed out",
                "AGY /USAGE TIMED OUT",
                $"`agy -p /usage` did not answer within {(int)CliTimeout.TotalSeconds} seconds. DockPulse will try again in a few minutes.");
        }

        string stdout = run.Stdout;

        if (run.ExitCode == 2)
        {
            return BuildAntigravityStatus(
                "No quota with an API key",
                "NO QUOTA WITH API KEY",
                "agy is using a `GEMINI_API_KEY`, which has no account quota to show. Check the key's usage in AI Studio.");
        }

        if (run.ExitCode != 0)
        {
            return BuildAntigravityStatus(
                "Couldn't read /usage from agy",
                "COULDN'T READ /USAGE",
                $"`agy -p /usage` exited with code {run.ExitCode}. Run `agy` once in a terminal to check that you're signed in.");
        }

        return ParseAgyUsage(stdout, DateTimeOffset.UtcNow)
               ?? BuildAntigravityStatus(
                   "Couldn't read /usage from agy",
                   "COULDN'T READ /USAGE",
                   "`agy -p /usage` answered, but not in a format DockPulse understands. A newer agy may have changed it.");
    }

    private static string? FindAgyExecutable()
    {
        string local = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "agy", "bin", "agy.exe");
        return File.Exists(local) ? local : FindOnPath("agy.exe");
    }

    private sealed record AgyGroup(string Name, AiLimitWindow? FiveHour, AiLimitWindow? Weekly)
    {
        public int Tightest => Math.Min(FiveHour?.RemainingPercent ?? 100, Weekly?.RemainingPercent ?? 100);
    }

    // `agy -p /usage --output-format json` prints {"response": "<tab-separated table>", "command": {"data": {"groups": [...]}}}.
    // Each group (e.g. "Gemini Models", "Claude and GPT models") has a 5h and a weekly bucket with
    // remaining_fraction and reset_time; a reset_time of 1970 means the window has not started (full).
    internal static AiProviderUsage? ParseAgyUsage(string stdout, DateTimeOffset nowUtc)
    {
        var groups = new List<AgyGroup>();

        string[] lines = stdout.Split('\n');
        for (int li = lines.Length - 1; li >= 0; li--)
        {
            string line = lines[li].Trim();
            if (!line.StartsWith('{'))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                if (root.TryGetProperty("command", out var command) && command.ValueKind == JsonValueKind.Object &&
                    command.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object &&
                    data.TryGetProperty("groups", out var groupArray) && groupArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var group in groupArray.EnumerateArray())
                    {
                        string name = group.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                            ? n.GetString() ?? "Models"
                            : "Models";
                        AiLimitWindow? fiveHour = null;
                        AiLimitWindow? weekly = null;

                        if (group.TryGetProperty("buckets", out var buckets) && buckets.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var bucket in buckets.EnumerateArray())
                            {
                                string window = bucket.TryGetProperty("window", out var w) && w.ValueKind == JsonValueKind.String
                                    ? w.GetString() ?? string.Empty
                                    : string.Empty;
                                if (!TryReadDouble(bucket, "remaining_fraction", out double fraction))
                                {
                                    continue;
                                }

                                var parsed = BuildAgyWindow(window, fraction * 100.0, TryParseTimestamp(bucket, "reset_time"), nowUtc);
                                if (parsed is null)
                                {
                                    continue;
                                }

                                if (parsed.WindowShort == "5h")
                                {
                                    fiveHour = parsed;
                                }
                                else
                                {
                                    weekly = parsed;
                                }
                            }
                        }

                        if (fiveHour is not null || weekly is not null)
                        {
                            groups.Add(new AgyGroup(name, fiveHour, weekly));
                        }
                    }
                }
                else if (root.TryGetProperty("response", out var response) && response.ValueKind == JsonValueKind.String)
                {
                    groups.AddRange(ParseAgyUsageTable(response.GetString() ?? string.Empty, nowUtc));
                }
            }
            catch
            {
                continue;
            }

            if (groups.Count > 0)
            {
                break;
            }
        }

        if (groups.Count == 0)
        {
            // Plain-text output (no --output-format json): Group<TAB>Window name<TAB>NN%<TAB>reset ISO time
            groups.AddRange(ParseAgyUsageTable(stdout, nowUtc));
        }

        if (groups.Count == 0)
        {
            return null;
        }

        // Show the group closest to its limit; ties keep agy's order (Gemini first)
        var shown = groups[0];
        foreach (var group in groups.Skip(1))
        {
            if (group.Tightest < shown.Tightest)
            {
                shown = group;
            }
        }

        return new AiProviderUsage(
            Id: "antigravity",
            Name: "Antigravity",
            PlanSubtitle: $"{AgyPlanPrefix} • {shown.Name}",
            HasData: true,
            FiveHour: shown.FiveHour,
            Weekly: shown.Weekly,
            UpdatedAtUtc: nowUtc,
            UnavailableSubtitle: string.Empty,
            UnavailableCardText: string.Empty,
            DetailsNote: "Source: `agy -p /usage`",
            Url: AgyUrl);
    }

    private static List<AgyGroup> ParseAgyUsageTable(string text, DateTimeOffset nowUtc)
    {
        var byName = new Dictionary<string, (AiLimitWindow? FiveHour, AiLimitWindow? Weekly)>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (string rawLine in text.Split('\n'))
        {
            string[] parts = rawLine.Trim().Split('\t');
            if (parts.Length < 4)
            {
                continue;
            }

            string pct = parts[2].Trim().TrimEnd('%');
            if (!double.TryParse(pct, NumberStyles.Float, CultureInfo.InvariantCulture, out double remaining))
            {
                continue;
            }

            string windowName = parts[1];
            string window = windowName.Contains("Five Hour", StringComparison.OrdinalIgnoreCase) ||
                            windowName.Contains("5h", StringComparison.OrdinalIgnoreCase)
                ? "5h"
                : windowName.Contains("Week", StringComparison.OrdinalIgnoreCase) ? "weekly" : string.Empty;

            DateTimeOffset? reset = DateTimeOffset.TryParse(
                parts[3].Trim(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var dto)
                ? dto
                : null;

            var parsed = BuildAgyWindow(window, remaining, reset, nowUtc);
            if (parsed is null)
            {
                continue;
            }

            string name = parts[0].Trim();
            if (!byName.TryGetValue(name, out var entry))
            {
                order.Add(name);
                entry = (null, null);
            }

            byName[name] = parsed.WindowShort == "5h" ? (parsed, entry.Weekly) : (entry.FiveHour, parsed);
        }

        return order.Select(n => new AgyGroup(n, byName[n].FiveHour, byName[n].Weekly)).ToList();
    }

    private static AiLimitWindow? BuildAgyWindow(string window, double remainingPercent, DateTimeOffset? resetsAt, DateTimeOffset nowUtc)
    {
        bool isFiveHour = string.Equals(window, "5h", StringComparison.OrdinalIgnoreCase);
        bool isWeekly = string.Equals(window, "weekly", StringComparison.OrdinalIgnoreCase);
        if ((!isFiveHour && !isWeekly) || double.IsNaN(remainingPercent) || double.IsInfinity(remainingPercent))
        {
            return null;
        }

        // agy reports 1970-01-01 for a window that has not started yet
        if (resetsAt is not null && resetsAt.Value.Year < 2000)
        {
            resetsAt = null;
        }

        bool resetPassed = resetsAt is not null && resetsAt.Value <= nowUtc;
        int remaining = resetPassed ? 100 : Math.Clamp((int)Math.Round(remainingPercent), 0, 100);

        return isFiveHour
            ? new AiLimitWindow("5h", "5-Hour Window", remaining, resetsAt, resetPassed)
            : new AiLimitWindow("Wk", "Weekly Limit", remaining, resetsAt, resetPassed);
    }

    private static AiProviderUsage BuildAntigravityStatus(string subtitle, string cardText, string note) => new(
        Id: "antigravity",
        Name: "Antigravity",
        PlanSubtitle: $"{AgyPlanPrefix} • /usage",
        HasData: false,
        FiveHour: null,
        Weekly: null,
        UpdatedAtUtc: null,
        UnavailableSubtitle: subtitle,
        UnavailableCardText: cardText,
        DetailsNote: note,
        Url: AgyUrl);

    private static AiProviderUsage ReadCodexUsage(DateTimeOffset nowUtc)
    {
        const string plan = "Codex CLI • Local sessions";
        const string url = "https://chatgpt.com/codex/settings/usage";
        string sessionsDir = GetCodexSessionsPath();

        if (!Directory.Exists(sessionsDir))
        {
            return BuildCodexNoData(plan, url, sessionsDir);
        }

        try
        {
            var candidates = EnumerateNewestRolloutFiles(sessionsDir, MaxCodexFilesToScan);
            foreach (var file in candidates)
            {
                var parsed = TryParseCodexRolloutFile(file, plan, url, nowUtc);
                if (parsed is not null)
                {
                    return parsed;
                }
            }
        }
        catch
        {
            // Fall through to No data on directory/IO errors
        }

        return BuildCodexNoData(plan, url, sessionsDir);
    }

    private static AiProviderUsage BuildCodexNoData(string plan, string url, string sessionsDir) => new(
        Id: "codex",
        Name: "Codex",
        PlanSubtitle: plan,
        HasData: false,
        FiveHour: null,
        Weekly: null,
        UpdatedAtUtc: null,
        UnavailableSubtitle: "No local Codex sessions found",
        UnavailableCardText: "NO LOCAL CODEX SESSIONS",
        DetailsNote: $"No `rate_limits` events found under `{sessionsDir}`.",
        Url: url);

    private static List<FileInfo> EnumerateNewestRolloutFiles(string sessionsDir, int maxFiles)
    {
        var collected = new List<FileInfo>(maxFiles);

        var yearDirs = new DirectoryInfo(sessionsDir)
            .GetDirectories()
            .OrderByDescending(d => d.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var yearDir in yearDirs)
        {
            DirectoryInfo[] monthDirs;
            try
            {
                monthDirs = yearDir.GetDirectories();
            }
            catch
            {
                continue;
            }

            foreach (var monthDir in monthDirs.OrderByDescending(d => d.Name, StringComparer.OrdinalIgnoreCase))
            {
                DirectoryInfo[] dayDirs;
                try
                {
                    dayDirs = monthDir.GetDirectories();
                }
                catch
                {
                    continue;
                }

                foreach (var dayDir in dayDirs.OrderByDescending(d => d.Name, StringComparer.OrdinalIgnoreCase))
                {
                    FileInfo[] files;
                    try
                    {
                        files = dayDir.GetFiles("rollout-*.jsonl");
                    }
                    catch
                    {
                        continue;
                    }

                    foreach (var file in files.OrderByDescending(f => f.LastWriteTimeUtc))
                    {
                        collected.Add(file);
                        if (collected.Count >= maxFiles)
                        {
                            return collected;
                        }
                    }
                }
            }
        }

        return collected;
    }

    private static AiProviderUsage? TryParseCodexRolloutFile(
        FileInfo file,
        string plan,
        string url,
        DateTimeOffset nowUtc)
    {
        try
        {
            string[] lines = ReadAllTextShared(file.FullName).Split('\n');
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                string line = lines[i];
                if (string.IsNullOrWhiteSpace(line) ||
                    !line.Contains("rate_limits", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    if (!TryFindCodexRateLimitsObject(root, out var rateLimits))
                    {
                        continue;
                    }

                    var eventTime = TryParseTimestamp(root, "timestamp")
                                    ?? new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);

                    AiLimitWindow? primary = null;
                    if (rateLimits.TryGetProperty("primary", out var pElem) && pElem.ValueKind == JsonValueKind.Object)
                    {
                        primary = ParseWindow(pElem, "5h", "5-Hour Window", nowUtc, fallbackTimestampUtc: eventTime);
                    }

                    AiLimitWindow? secondary = null;
                    if (rateLimits.TryGetProperty("secondary", out var sElem) && sElem.ValueKind == JsonValueKind.Object)
                    {
                        secondary = ParseWindow(sElem, "Wk", "Weekly Limit", nowUtc, fallbackTimestampUtc: eventTime);
                    }

                    if (primary is null && secondary is null)
                    {
                        continue;
                    }

                    return new AiProviderUsage(
                        Id: "codex",
                        Name: "Codex",
                        PlanSubtitle: plan,
                        HasData: true,
                        FiveHour: primary,
                        Weekly: secondary,
                        UpdatedAtUtc: eventTime,
                        UnavailableSubtitle: string.Empty,
                        UnavailableCardText: string.Empty,
                        DetailsNote: $"Source: `{file.Name}`",
                        Url: url);
                }
                catch
                {
                    // Skip malformed JSONL lines
                }
            }
        }
        catch
        {
            // Ignore unreadable session file
        }

        return null;
    }

    // Codex keeps its active rollout file open for writing, and the status-line script rewrites the
    // Claude cache at any moment. File.ReadAllText/ReadAllLines only allow FileShare.Read, which fails
    // with a sharing violation while another process holds a write handle, so read with full sharing.
    private static string ReadAllTextShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static bool TryFindCodexRateLimitsObject(JsonElement root, out JsonElement rateLimits)
    {
        if (root.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object)
        {
            if (payload.TryGetProperty("rate_limits", out rateLimits) && rateLimits.ValueKind == JsonValueKind.Object)
            {
                return true;
            }

            if (payload.TryGetProperty("info", out var info) &&
                info.ValueKind == JsonValueKind.Object &&
                info.TryGetProperty("rate_limits", out rateLimits) &&
                rateLimits.ValueKind == JsonValueKind.Object)
            {
                return true;
            }
        }

        if (root.TryGetProperty("rate_limits", out rateLimits) && rateLimits.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        rateLimits = default;
        return false;
    }

    private static AiLimitWindow? ParseWindow(
        JsonElement obj,
        string windowShort,
        string defaultLabel,
        DateTimeOffset nowUtc,
        DateTimeOffset fallbackTimestampUtc)
    {
        if (!TryReadDouble(obj, "used_percentage", out double usedPct) &&
            !TryReadDouble(obj, "used_percent", out usedPct))
        {
            return null;
        }

        if (double.IsNaN(usedPct) || double.IsInfinity(usedPct))
        {
            return null;
        }

        usedPct = Math.Clamp(usedPct, 0.0, 100.0);

        DateTimeOffset? resetsAt = TryParseTimestamp(obj, "resets_at")
                                   ?? TryParseTimestamp(obj, "reset_at");

        if (resetsAt is null)
        {
            if (TryReadDouble(obj, "resets_in_seconds", out double sec) ||
                TryReadDouble(obj, "reset_after_seconds", out sec))
            {
                if (sec >= 0 && !double.IsNaN(sec) && !double.IsInfinity(sec))
                {
                    resetsAt = fallbackTimestampUtc.AddSeconds(sec);
                }
            }
        }

        bool resetPassed = resetsAt is not null && resetsAt.Value <= nowUtc;
        int remainingPct = resetPassed
            ? 100
            : Math.Clamp((int)Math.Round(100.0 - usedPct), 0, 100);

        string label = defaultLabel;
        if (TryReadDouble(obj, "window_minutes", out double winMins) && winMins > 0)
        {
            if (Math.Abs(winMins - 300.0) < 1.0)
            {
                label = "5-Hour Window";
            }
            else if (Math.Abs(winMins - 10080.0) < 1.0)
            {
                label = "Weekly Limit";
            }
            else if (winMins >= 1440.0)
            {
                int days = (int)Math.Round(winMins / 1440.0);
                label = $"{days}-Day Limit";
            }
            else if (winMins >= 60.0)
            {
                int hours = (int)Math.Round(winMins / 60.0);
                label = $"{hours}-Hour Window";
            }
        }

        return new AiLimitWindow(windowShort, label, remainingPct, resetsAt, resetPassed);
    }

    private static DateTimeOffset? TryParseTimestamp(JsonElement obj, string propName)
    {
        if (!obj.TryGetProperty(propName, out var prop))
        {
            return null;
        }

        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDouble(out double rawNum))
        {
            return UnixNumberToDateTimeOffset(rawNum);
        }

        if (prop.ValueKind == JsonValueKind.String)
        {
            string? text = prop.GetString();
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double num))
            {
                return UnixNumberToDateTimeOffset(num);
            }

            if (DateTimeOffset.TryParse(
                    text,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var dto))
            {
                return dto;
            }
        }

        return null;
    }

    private static DateTimeOffset? UnixNumberToDateTimeOffset(double rawNum)
    {
        if (double.IsNaN(rawNum) || double.IsInfinity(rawNum) || rawNum <= 0)
        {
            return null;
        }

        try
        {
            // Distinguish milliseconds (>= 1e12) from seconds
            long seconds = rawNum >= 1_000_000_000_000.0
                ? (long)Math.Round(rawNum / 1000.0)
                : (long)Math.Round(rawNum);

            return DateTimeOffset.FromUnixTimeSeconds(seconds);
        }
        catch
        {
            return null;
        }
    }

    private static bool TryReadDouble(JsonElement obj, string propName, out double value)
    {
        value = 0;
        if (!obj.TryGetProperty(propName, out var prop))
        {
            return false;
        }

        if (prop.ValueKind == JsonValueKind.Number)
        {
            return prop.TryGetDouble(out value);
        }

        if (prop.ValueKind == JsonValueKind.String)
        {
            return double.TryParse(prop.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        return false;
    }

    public static string FormatDataAge(DateTimeOffset? updatedAtUtc, DateTimeOffset nowUtc)
    {
        if (updatedAtUtc is null)
        {
            return "updated just now";
        }

        var age = nowUtc - updatedAtUtc.Value;
        if (age < TimeSpan.FromMinutes(1))
        {
            return "updated just now";
        }

        if (age < TimeSpan.FromHours(1))
        {
            int mins = Math.Max(1, (int)Math.Floor(age.TotalMinutes));
            return $"updated {mins} min ago";
        }

        if (age < TimeSpan.FromDays(1))
        {
            int hours = (int)Math.Floor(age.TotalHours);
            int mins = age.Minutes;
            return mins > 0 ? $"updated {hours}h {mins}m ago" : $"updated {hours}h ago";
        }

        int days = Math.Max(1, (int)Math.Floor(age.TotalDays));
        return $"updated {days}d ago";
    }

    public static string GetClaudeCachePath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "dockpulse-usage.json");

    public static string GetClaudeScriptPathForwardSlashes()
    {
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).Replace('\\', '/').TrimEnd('/');
        return $"{userProfile}/.claude/dockpulse-statusline.ps1";
    }

    public static string GetClaudeSettingsJsonPathForwardSlashes()
    {
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).Replace('\\', '/').TrimEnd('/');
        return $"{userProfile}/.claude/settings.json";
    }

    public static string GetCodexSessionsPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions");

    public static string GetClaudeStatusLineScript() =>
        """
        $raw = [Console]::In.ReadToEnd()
        if ([string]::IsNullOrWhiteSpace($raw)) { exit 0 }
        try {
            $data = $raw | ConvertFrom-Json
            if ($null -ne $data.rate_limits) {
                $out = [ordered]@{
                    updated_at = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
                    five_hour  = $data.rate_limits.five_hour
                    seven_day  = $data.rate_limits.seven_day
                }
                $cachePath = Join-Path $env:USERPROFILE ".claude\dockpulse-usage.json"
                $out | ConvertTo-Json -Compress -Depth 4 | Set-Content -Path $cachePath -Encoding UTF8
            }
            if ($null -ne $data.model -and $null -ne $data.model.display_name) {
                Write-Output $data.model.display_name
            }
        } catch {
            # Ignore parse errors so Claude Code statusLine never fails
        }
        """;

    public static string GetClaudeSettingsSnippet()
    {
        string scriptPath = GetClaudeScriptPathForwardSlashes();
        return $$"""
        {
          "statusLine": {
            "type": "command",
            "command": "powershell -NoProfile -ExecutionPolicy Bypass -File \"{{scriptPath}}\""
          }
        }
        """;
    }

    public static string GetClaudeSetupClipboardText() =>
        $"""
        # 1. Save this script to: {GetClaudeScriptPathForwardSlashes()}
        {GetClaudeStatusLineScript()}

        # 2. Add this to: {GetClaudeSettingsJsonPathForwardSlashes()}
        {GetClaudeSettingsSnippet()}
        """;

    public static string GetClaudeSetupMarkdown()
    {
        string scriptPath = GetClaudeScriptPathForwardSlashes();
        string settingsPath = GetClaudeSettingsJsonPathForwardSlashes();
        string cachePath = GetClaudeCachePath().Replace('\\', '/');

        return
            $"DockPulse reads Claude Code's local usage cache at `{cachePath}` and never edits `~/.claude/settings.json` or reads credentials.\n\n" +
            $"**1. Save the status-line script** to `{scriptPath}`:\n\n" +
            $"```powershell\n{GetClaudeStatusLineScript()}\n```\n\n" +
            $"**2. Add `statusLine` to** `{settingsPath}`:\n\n" +
            $"```json\n{GetClaudeSettingsSnippet()}\n```\n\n" +
            "Press **Enter** on this row to copy both snippets to your clipboard. Limits appear after your next Claude Code response (Pro/Max/Ultra).";
    }
}

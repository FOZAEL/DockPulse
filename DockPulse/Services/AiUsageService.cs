using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
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

public sealed class AiUsageService
{
    private const int MaxCodexFilesToScan = 15;

    // agy contacts Google for every /usage call, so run it at most this often (manual refresh skips the wait)
    private static readonly TimeSpan AgyRefreshInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan AgyTimeout = TimeSpan.FromSeconds(30);

    private readonly object _agyLock = new();
    private AiProviderUsage? _agyLastGood;
    private AiProviderUsage? _agyLastStatus;
    private DateTimeOffset _agyLastAttemptUtc = DateTimeOffset.MinValue;
    private int _agyFetching;

    /// <summary>Raised on a thread-pool thread when a background Antigravity read finishes.</summary>
    public event Action? Updated;

    public AiUsageSnapshot GetUsageSnapshot(bool forceRefresh = false)
    {
        var nowUtc = DateTimeOffset.UtcNow;

        var claude = ReadClaudeUsage(nowUtc);
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

    // Returns the last Antigravity reading at once and, when it is due, starts a background
    // `agy -p /usage` run; Updated fires when that run finishes so the band can redraw.
    private AiProviderUsage GetAntigravityUsage(DateTimeOffset nowUtc, bool forceRefresh)
    {
        AiProviderUsage? good;
        AiProviderUsage? status;
        bool due;
        lock (_agyLock)
        {
            good = _agyLastGood;
            status = _agyLastStatus;
            due = forceRefresh || nowUtc - _agyLastAttemptUtc >= AgyRefreshInterval;
        }

        if (due)
        {
            StartAgyRefresh();
        }

        if (good is not null)
        {
            // A cached window whose reset time has passed is full again
            return good with
            {
                FiveHour = RollWindow(good.FiveHour, nowUtc),
                Weekly = RollWindow(good.Weekly, nowUtc),
            };
        }

        return status ?? BuildAntigravityStatus(
            "Checking /usage in agy…",
            "CHECKING /USAGE IN AGY",
            "DockPulse is reading your limits with `agy -p /usage`. This takes a few seconds.");
    }

    private static AiLimitWindow? RollWindow(AiLimitWindow? window, DateTimeOffset nowUtc)
    {
        if (window is null || window.ResetPassed || window.ResetsAtUtc is null || window.ResetsAtUtc.Value > nowUtc)
        {
            return window;
        }

        return window with { RemainingPercent = 100, ResetPassed = true };
    }

    private void StartAgyRefresh()
    {
        if (Interlocked.Exchange(ref _agyFetching, 1) == 1)
        {
            return;
        }

        lock (_agyLock)
        {
            _agyLastAttemptUtc = DateTimeOffset.UtcNow;
        }

        _ = Task.Run(() =>
        {
            try
            {
                var result = FetchAgyUsage();
                lock (_agyLock)
                {
                    if (result.HasData)
                    {
                        _agyLastGood = result;
                        _agyLastStatus = null;
                    }
                    else
                    {
                        // Keep showing the last good numbers (with their age) if a later read fails
                        _agyLastStatus = result;
                    }
                }
            }
            catch
            {
                // Never let a failed agy run take the extension down
            }
            finally
            {
                Interlocked.Exchange(ref _agyFetching, 0);
            }

            try
            {
                Updated?.Invoke();
            }
            catch
            {
                // Ignore subscriber errors
            }
        });
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
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add("/usage");
        psi.ArgumentList.Add("--output-format");
        psi.ArgumentList.Add("json");
        psi.ArgumentList.Add("--print-timeout");
        psi.ArgumentList.Add("25s");

        using var proc = Process.Start(psi);
        if (proc is null)
        {
            return BuildAntigravityStatus(
                "Couldn't start agy",
                "COULDN'T START AGY",
                $"Starting `{exe}` failed.");
        }

        // Read both streams while waiting so a full pipe can never block agy
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();

        if (!proc.WaitForExit((int)AgyTimeout.TotalMilliseconds))
        {
            try
            {
                proc.Kill(entireProcessTree: true);
            }
            catch
            {
                // Already gone
            }

            return BuildAntigravityStatus(
                "agy /usage timed out",
                "AGY /USAGE TIMED OUT",
                $"`agy -p /usage` did not answer within {(int)AgyTimeout.TotalSeconds} seconds. DockPulse will try again in a few minutes.");
        }

        string stdout = stdoutTask.GetAwaiter().GetResult();
        _ = stderrTask.GetAwaiter().GetResult();

        if (proc.ExitCode == 2)
        {
            return BuildAntigravityStatus(
                "No quota with an API key",
                "NO QUOTA WITH API KEY",
                "agy is using a `GEMINI_API_KEY`, which has no account quota to show. Check the key's usage in AI Studio.");
        }

        if (proc.ExitCode != 0)
        {
            return BuildAntigravityStatus(
                "Couldn't read /usage from agy",
                "COULDN'T READ /USAGE",
                $"`agy -p /usage` exited with code {proc.ExitCode}. Run `agy` once in a terminal to check that you're signed in.");
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
        if (File.Exists(local))
        {
            return local;
        }

        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        foreach (string dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                string candidate = Path.Combine(dir, "agy.exe");
                if (File.Exists(candidate))
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

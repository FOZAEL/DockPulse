using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

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

    public AiUsageSnapshot GetUsageSnapshot()
    {
        var nowUtc = DateTimeOffset.UtcNow;

        var claude = ReadClaudeUsage(nowUtc);
        var antigravity = BuildAntigravityUsage();
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

    private static AiProviderUsage BuildAntigravityUsage() => new(
        Id: "antigravity",
        Name: "Antigravity",
        PlanSubtitle: "Antigravity CLI & IDE • No local source",
        HasData: false,
        FiveHour: null,
        Weekly: null,
        UpdatedAtUtc: null,
        UnavailableSubtitle: "Check /usage in agy",
        UnavailableCardText: "CHECK /USAGE IN AGY",
        DetailsNote: "Antigravity does not expose a local usage file. Run `/usage` in `agy` to view your current limits.",
        Url: "https://antigravity.google");

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

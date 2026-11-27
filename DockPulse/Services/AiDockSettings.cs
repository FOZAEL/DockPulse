using System;
using System.IO;
using DockPulse.Helpers;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace DockPulse.Services;

public sealed record AiDockDisplay(
    string Mode,
    string ChoiceLabel,
    string? ProviderId,
    IconInfo Icon,
    string Title,
    string Subtitle);

/// <summary>
/// Persists the AI Usage Dock display choice (<c>dock=auto|claude|antigravity|codex</c> in <c>aiusage.cfg</c>)
/// and holds the in-memory Compare Providers window selection (5h vs Weekly).
/// </summary>
public sealed class AiDockSettings
{
    public const string AutoMode = "auto";
    public const string AutoLabel = "Tightest limit (auto)";
    public const string CursorUrl = "https://cursor.com/dashboard?tab=usage";

    private readonly object _lock = new();
    private readonly string _configPath;
    private string _dockMode;
    private bool _showWeeklyCompare;

    public AiDockSettings()
    {
        _configPath = DockPulseHelpers.GetSettingsFilePath("aiusage.cfg");
        _dockMode = LoadMode();
    }

    public event Action? Changed;

    public event Action? CompareWindowChanged;

    public string DockMode
    {
        get
        {
            lock (_lock)
            {
                return _dockMode;
            }
        }
    }

    public bool ShowWeeklyCompare
    {
        get
        {
            lock (_lock)
            {
                return _showWeeklyCompare;
            }
        }
    }

    public void SetDockMode(string? mode)
    {
        string normalized = NormalizeMode(mode);
        bool changed;
        lock (_lock)
        {
            changed = !string.Equals(_dockMode, normalized, StringComparison.Ordinal);
            _dockMode = normalized;
            try
            {
                File.WriteAllText(_configPath, $"dock={normalized}{Environment.NewLine}");
            }
            catch
            {
                // Keep the new selection for this session even if it can't be saved
            }
        }

        if (changed)
        {
            RaiseEvent(Changed);
        }
    }

    public void SetShowWeeklyCompare(bool weekly)
    {
        bool changed;
        lock (_lock)
        {
            changed = _showWeeklyCompare != weekly;
            _showWeeklyCompare = weekly;
        }

        if (changed)
        {
            RaiseEvent(CompareWindowChanged);
        }
    }

    public static AiDockDisplay ResolveDockDisplay(AiUsageSnapshot snapshot, string mode, DateTimeOffset nowUtc)
    {
        string normalized = NormalizeMode(mode);

        if (!string.Equals(normalized, AutoMode, StringComparison.Ordinal))
        {
            AiProviderUsage? chosen = null;
            foreach (var p in snapshot.Providers)
            {
                if (string.Equals(p.Id, normalized, StringComparison.OrdinalIgnoreCase))
                {
                    chosen = p;
                    break;
                }
            }

            if (chosen is not null)
            {
                if (!chosen.HasData || (chosen.FiveHour is null && chosen.Weekly is null))
                {
                    return new AiDockDisplay(
                        Mode: chosen.Id,
                        ChoiceLabel: chosen.Name,
                        ProviderId: chosen.Id,
                        Icon: AiLogos.GetIcon(chosen.Id),
                        Title: chosen.Name,
                        Subtitle: "No data");
                }

                AiLimitWindow window = (chosen.FiveHour, chosen.Weekly) switch
                {
                    ({ } fh, { } wk) => wk.RemainingPercent < fh.RemainingPercent ? wk : fh,
                    ({ } fh, null) => fh,
                    (null, { } wk) => wk,
                    _ => throw new InvalidOperationException(),
                };

                string reset = window.FormatResetShort(nowUtc);
                string subtitle = string.Equals(reset, "Reset", StringComparison.Ordinal)
                    ? $"{window.WindowShort} • Reset"
                    : $"{window.WindowShort} • resets {reset}";

                return new AiDockDisplay(
                    Mode: chosen.Id,
                    ChoiceLabel: chosen.Name,
                    ProviderId: chosen.Id,
                    Icon: AiLogos.GetIcon(chosen.Id),
                    Title: $"{chosen.Name} {window.RemainingPercent}%",
                    Subtitle: subtitle);
            }
        }

        if (snapshot.Tightest is null)
        {
            return new AiDockDisplay(
                Mode: AutoMode,
                ChoiceLabel: AutoLabel,
                ProviderId: null,
                Icon: new IconInfo("\uE99A"),
                Title: "AI Usage",
                Subtitle: "No data");
        }

        var tight = snapshot.Tightest;
        string tightSubtitle = string.Equals(tight.ResetShort, "Reset", StringComparison.Ordinal)
            ? $"{tight.WindowShort} • Reset"
            : $"{tight.WindowShort} • resets {tight.ResetShort}";

        return new AiDockDisplay(
            Mode: AutoMode,
            ChoiceLabel: AutoLabel,
            ProviderId: tight.ProviderId,
            Icon: AiLogos.GetIcon(tight.ProviderId),
            Title: $"{tight.ProviderName} {tight.RemainingPercent}%",
            Subtitle: tightSubtitle);
    }

    private static string NormalizeMode(string? raw) =>
        (raw ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "claude" => "claude",
            "antigravity" => "antigravity",
            "codex" => "codex",
            _ => AutoMode,
        };

    private string LoadMode()
    {
        try
        {
            if (!File.Exists(_configPath))
            {
                return AutoMode;
            }

            foreach (string line in File.ReadAllLines(_configPath))
            {
                if (line.StartsWith("dock=", StringComparison.OrdinalIgnoreCase))
                {
                    return NormalizeMode(line["dock=".Length..]);
                }
            }
        }
        catch
        {
            // Fall back to default auto mode
        }

        return AutoMode;
    }

    private static void RaiseEvent(Action? handler)
    {
        try
        {
            handler?.Invoke();
        }
        catch
        {
            // Ignore subscriber errors
        }
    }
}

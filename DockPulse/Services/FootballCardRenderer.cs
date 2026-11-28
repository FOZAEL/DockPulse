using System;
using System.Collections.Generic;
using System.Globalization;

namespace DockPulse.Services;

/// <summary>
/// The match card in the Football page's details pane, laid out like a scoreboard row: status pill,
/// home name and crest, kickoff time or score, away crest and name.
/// </summary>
public static class FootballCardRenderer
{
    public const int Width = 320;
    public const int Height = 76;

    public static byte[] RenderMatchPng(FootballMatch match, AiLogos.LogoBitmap? homeCrest, AiLogos.LogoBitmap? awayCrest)
    {
        // 1x: the details pane shows Markdown images at their real pixel size and clips anything wider
        var c = new GdiCanvas(Width, Height, scale: 1);
        c.FillCard(10, 0x3b, 0x3b, 0x3b);
        const int cy = Height / 2;

        // Status pill: live minute in mint, FT in grey, nothing before kickoff
        if (match.State != FootballMatchState.Scheduled)
        {
            bool live = match.State == FootballMatchState.Live;
            string label = live ? ShortStatus(match.StatusDetail) : "FT";
            int pw = Math.Max(26, c.MeasureText(label, 10, 700) + 14);
            if (live)
            {
                c.FillRoundRect(10, cy - 10, pw, 20, 10, 0x5e, 0xf2, 0xc2, 1f);
                c.DrawTextCentered(label, 10 + (pw / 2f), cy + 4, 10, 700, 0x0b, 0x2a, 0x20, 1f);
            }
            else
            {
                c.FillRoundRect(10, cy - 10, pw, 20, 10, 0x5a, 0x5a, 0x5a, 1f);
                c.DrawTextCentered(label, 10 + (pw / 2f), cy + 4, 10, 700, 0xff, 0xff, 0xff, 1f);
            }
        }

        // Centre: kickoff time or score
        const int midX = 172;
        string centre = match.State == FootballMatchState.Scheduled
            ? match.Kickoff.ToString("HH:mm", CultureInfo.InvariantCulture)
            : $"{match.HomeScore} - {match.AwayScore}";
        c.DrawTextCentered(centre, midX, cy + 6, match.State == FootballMatchState.Scheduled ? 15 : 17, 700, 0xff, 0xff, 0xff, 1f);

        // Crests either side of the centre (a plain disc until the crest has downloaded)
        DrawCrest(c, homeCrest, midX - 40, cy);
        DrawCrest(c, awayCrest, midX + 40, cy);

        // Names outside the crests, wrapping to two lines for long club names
        DrawName(c, match.Home.ShortName, midX - 58, cy, 78, alignRight: true);
        DrawName(c, match.Away.ShortName, midX + 58, cy, 84, alignRight: false);

        return c.EncodePng();
    }

    /// <summary>"67'" or "HT" from ESPN's short detail (which can also be longer text).</summary>
    public static string ShortStatus(string detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return "LIVE";
        }

        string trimmed = detail.Trim();
        return trimmed.Length <= 7 ? trimmed : "LIVE";
    }

    private static void DrawCrest(GdiCanvas c, AiLogos.LogoBitmap? crest, float cx, float cy)
    {
        if (crest is { } bitmap)
        {
            c.DrawImage(bitmap.Rgba, bitmap.Width, bitmap.Height, cx - 12, cy - 12, 24);
        }
        else
        {
            c.FillCircle(cx, cy, 11, 0x5a, 0x5a, 0x5a, 1f);
        }
    }

    private static void DrawName(GdiCanvas c, string name, int edgeX, int cy, int maxWidth, bool alignRight)
    {
        var lines = Wrap(c, name, maxWidth);
        for (int i = 0; i < lines.Count; i++)
        {
            int baseline = (int)Math.Round(cy + 4.5 + ((i - ((lines.Count - 1) / 2.0)) * 15));
            if (alignRight)
            {
                c.DrawTextRight(lines[i], edgeX, baseline, 12, 700, 0xff, 0xff, 0xff, 1f);
            }
            else
            {
                c.DrawText(lines[i], edgeX, baseline, 12, 700, 0xff, 0xff, 0xff, 1f);
            }
        }
    }

    private static List<string> Wrap(GdiCanvas c, string text, int maxWidth)
    {
        var lines = new List<string> { string.Empty };
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string current = lines[^1];
            string attempt = current.Length == 0 ? word : current + " " + word;
            if (current.Length == 0 || c.MeasureText(attempt, 12, 700) <= maxWidth)
            {
                lines[^1] = attempt;
            }
            else if (lines.Count < 2)
            {
                lines.Add(word);
            }
            else
            {
                lines[^1] = attempt; // third line would not fit: keep it on the second
            }
        }

        return lines;
    }
}

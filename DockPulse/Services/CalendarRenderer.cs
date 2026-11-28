using System;
using System.Collections.Generic;

namespace DockPulse.Services;

/// <summary>
/// Draws the Calendar card (blue gradient, month carousel, one week with a pill on today, the two other
/// calendars in the footer) as a PNG. Text uses Windows' Segoe UI through GDI so it looks like the rest
/// of Windows; shapes are drawn with anti-aliased signed distances. Rendered at 2x for sharp text.
/// </summary>
public static class CalendarRenderer
{
    // A wide card: Command Palette scales it to the page width, so it spans the panel like the buttons below
    public const int Width = 800;
    public const int Height = 244;
    private const int Left = 24;

    public sealed record WeekDay(DateTime Date, int Day, bool InCurrentMonth, bool IsToday, string Letter);

    public sealed record CardData(
        int Year,
        string Month,
        IReadOnlyList<string> NextMonths,
        IReadOnlyList<WeekDay> Week,
        string FooterLeftLabel,
        string FooterLeft,
        string FooterRightLabel,
        string FooterRight);

    public static byte[] RenderPng(CardData data)
    {
        var c = new GdiCanvas(Width, Height);
        var inv = System.Globalization.CultureInfo.InvariantCulture;

        // Background: vertical gradient inside a rounded card
        c.FillRoundRectGradient(0, 0, Width, Height, 16, (0x40, 0x50, 0x8a), (0x28, 0x33, 0x6a));

        // Year, top left
        c.DrawText(data.Year.ToString(inv), Left, 34, 18, 600, 255, 255, 255, 1f);

        // Month carousel: current month, then the next ones faded and cut off at the right edge
        const int baseY = 100; // leaves a clear gap under the year
        int curW = c.MeasureText(data.Month, 38, 700);
        c.DrawText(data.Month, Left, baseY, 38, 700, 255, 255, 255, 1f);
        c.FillCircle(Left + (curW / 2f), baseY - 44, 2.5f, 255, 255, 255, 1f);
        float x = Left + curW + 40;
        foreach (var next in data.NextMonths)
        {
            if (x >= Width)
            {
                break;
            }

            c.DrawText(next, (int)x, baseY, 32, 700, 255, 255, 255, 0.22f);
            x += c.MeasureText(next, 32, 700) + 40;
        }

        // Week strip across the full width, with a pill on today
        float colW = (Width - (Left * 2)) / 7f;
        for (int i = 0; i < data.Week.Count && i < 7; i++)
        {
            var d = data.Week[i];
            float cx = Left + (colW * i) + (colW / 2);
            if (d.IsToday)
            {
                c.FillRoundRect(cx - 26, 118, 52, 86, 26, 0x6c, 0x7b, 0xff, 1f);
            }

            c.DrawTextCentered(d.Letter, cx, 146, 13, 600, 255, 255, 255, d.IsToday ? 0.85f : 0.5f);
            c.DrawTextCentered(d.Day.ToString(inv), cx, 188, 23, d.IsToday ? 700 : 500, 255, 255, 255, d.InCurrentMonth || d.IsToday ? 1f : 0.45f);
        }

        // Footer: today in the two other calendars
        c.FillRect(Left, 214, Width - (Left * 2), 1, 255, 255, 255, 0.12f);
        int leftLabelW = c.MeasureText(data.FooterLeftLabel, 11, 600);
        int rightLabelW = c.MeasureText(data.FooterRightLabel, 11, 600);
        c.DrawText(data.FooterLeftLabel, Left, 235, 11, 600, 255, 255, 255, 0.45f);
        c.DrawText(data.FooterRightLabel, Width - Left - rightLabelW, 235, 11, 600, 255, 255, 255, 0.45f);
        c.DrawText(data.FooterLeft, Left + leftLabelW + 10, 235, 14, 600, 255, 255, 255, 1f);
        int rightW = c.MeasureText(data.FooterRight, 14, 600);
        c.DrawText(data.FooterRight, Width - Left - rightLabelW - 10 - rightW, 235, 14, 600, 255, 255, 255, 1f);

        return c.EncodePng();
    }
}

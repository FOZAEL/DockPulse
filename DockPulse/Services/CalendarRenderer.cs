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
    public const int Width = 480;
    public const int Height = 270;
    private const int Left = 20; // the year starts here, the same left edge as the buttons under the card

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

        // Background: vertical gradient inside a rounded card
        c.FillRoundRectGradient(0, 0, Width, Height, 14, (0x40, 0x50, 0x8a), (0x28, 0x33, 0x6a));

        // Year, top left
        c.DrawText(data.Year.ToString(System.Globalization.CultureInfo.InvariantCulture), Left, 38, 17, 600, 255, 255, 255, 1f);

        // Month carousel
        const int baseY = 94;
        int curW = c.MeasureText(data.Month, 34, 700);
        c.DrawText(data.Month, Left, baseY, 34, 700, 255, 255, 255, 1f);
        c.FillCircle(Left + (curW / 2f), baseY - 40, 2.5f, 255, 255, 255, 1f);
        float x = Left + curW + 36;
        foreach (var next in data.NextMonths)
        {
            if (x >= Width)
            {
                break;
            }

            c.DrawText(next, (int)x, baseY, 30, 700, 255, 255, 255, 0.22f);
            x += c.MeasureText(next, 30, 700) + 36;
        }

        // Week strip
        float colW = (Width - (Left * 2)) / 7f;
        for (int i = 0; i < data.Week.Count && i < 7; i++)
        {
            var d = data.Week[i];
            float cx = Left + (colW * i) + (colW / 2);
            if (d.IsToday)
            {
                c.FillRoundRect(cx - 23, 122, 46, 90, 23, 0x6c, 0x7b, 0xff, 1f);
            }

            c.DrawTextCentered(d.Letter, cx, 150, 12, 600, 255, 255, 255, d.IsToday ? 0.85f : 0.5f);
            c.DrawTextCentered(
                d.Day.ToString(System.Globalization.CultureInfo.InvariantCulture),
                cx,
                192,
                21,
                d.IsToday ? 700 : 500,
                255,
                255,
                255,
                d.InCurrentMonth || d.IsToday ? 1f : 0.45f);
        }

        // Footer: today in the two other calendars
        c.FillRect(Left, 228, Width - (Left * 2), 1, 255, 255, 255, 0.12f);
        int leftLabelW = c.MeasureText(data.FooterLeftLabel, 10, 600);
        int rightLabelW = c.MeasureText(data.FooterRightLabel, 10, 600);
        c.DrawText(data.FooterLeftLabel, Left, 252, 10, 600, 255, 255, 255, 0.45f);
        c.DrawText(data.FooterRightLabel, Width - Left - rightLabelW, 252, 10, 600, 255, 255, 255, 0.45f);
        c.DrawText(data.FooterLeft, Left + leftLabelW + 8, 252, 13, 600, 255, 255, 255, 1f);
        int rightW = c.MeasureText(data.FooterRight, 13, 600);
        c.DrawText(data.FooterRight, Width - Left - rightLabelW - 8 - rightW, 252, 13, 600, 255, 255, 255, 1f);

        return c.EncodePng();
    }
}

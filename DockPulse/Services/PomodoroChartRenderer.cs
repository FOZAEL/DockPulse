using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace DockPulse.Services;

public static class PomodoroChartRenderer
{
    public const int TimerWidth = 480;
    public const int TimerHeight = 340;
    public const int StatsWidth = 600;
    public const int StatsHeight = 220;

    private readonly record struct Rgba(byte R, byte G, byte B, byte A = 255);

    private static readonly Rgba CardBg = new(28, 32, 40);
    private static readonly Rgba CardBorder = new(52, 59, 74);
    private static readonly Rgba GridLine = new(44, 50, 64);
    private static readonly Rgba AxisLine = new(68, 76, 94);
    private static readonly Rgba TrackRing = new(45, 53, 69);
    private static readonly Rgba TrackBar = new(36, 41, 52);
    private static readonly Rgba TextPrimary = new(241, 245, 249);
    private static readonly Rgba TextSecondary = new(156, 168, 188);
    private static readonly Rgba FocusRing = new(56, 189, 248);
    private static readonly Rgba BreakRing = new(52, 211, 153);
    private static readonly Rgba BarNormal = new(56, 189, 248);
    private static readonly Rgba BarToday = new(125, 211, 252);

    public static byte[] RenderTimerCardPng(
        PomodoroSnapshot snap,
        int width = TimerWidth,
        int height = TimerHeight)
    {
        var canvas = new PomoCanvas(width, height);
        canvas.FillAntiAliasedRoundedRect(0, 0, width, height, 12.0, CardBg, CardBorder);

        canvas.DrawText("POMODORO", 24, 18, TextPrimary, scale: 2);

        string statusTag = snap.IsRunning
            ? "RUNNING"
            : (snap.RemainingSeconds < snap.TotalSeconds ? "PAUSED" : "READY");
        string rightLabel = $"{snap.PhaseSubtitle.ToUpperInvariant()}  •  {statusTag}";
        int rightW = PomoCanvas.MeasureText(rightLabel, scale: 1);
        canvas.DrawText(rightLabel, width - 24 - rightW, 24, TextSecondary, scale: 1);

        int cx = width / 2;
        const int cy = 186;
        const double radius = 108.0;
        const double halfThickness = 3.5;

        var ringColor = snap.Phase == PomodoroPhase.Focus ? FocusRing : BreakRing;
        double frac = snap.TotalSeconds > 0
            ? Math.Clamp((double)snap.RemainingSeconds / snap.TotalSeconds, 0.0, 1.0)
            : 0.0;

        canvas.DrawRingArc(cx, cy, radius, halfThickness, 0.0, Math.PI * 2.0, TrackRing);
        if (frac > 0.0)
        {
            double sweep = Math.PI * 2.0 * frac;
            canvas.DrawRingArc(cx, cy, radius, halfThickness, -Math.PI / 2.0, sweep, ringColor);
        }

        string clockText = PomodoroService.FormatMmSs(snap.RemainingSeconds);
        const int clockScale = 6;
        int clockW = PomoCanvas.MeasureText(clockText, clockScale);
        int clockH = 7 * clockScale;
        canvas.DrawText(clockText, cx - (clockW / 2), cy - 12 - (clockH / 2), TextPrimary, scale: clockScale);

        int count = Math.Max(1, snap.SessionsPerCycle);
        int dashW = count <= 4 ? 32 : Math.Max(18, 144 / count);
        const int gap = 8;
        int totalDashW = (count * dashW) + ((count - 1) * gap);
        int dashX0 = cx - (totalDashW / 2);
        const int dashY = cy + 34;

        double elapsedFocus = snap.Phase == PomodoroPhase.Focus && snap.TotalSeconds > 0
            ? Math.Clamp(1.0 - ((double)snap.RemainingSeconds / snap.TotalSeconds), 0.18, 1.0)
            : 0.0;
        int completedCount = snap.Phase == PomodoroPhase.LongBreak
            ? count
            : (snap.Phase == PomodoroPhase.ShortBreak ? snap.SessionIndex : snap.SessionIndex - 1);

        for (int i = 0; i < count; i++)
        {
            int x = dashX0 + (i * (dashW + gap));
            canvas.FillAntiAliasedRoundedRect(x, dashY, dashW, 5, 2.5, TrackRing, null);

            double fillRatio = 0.0;
            if (i < completedCount)
            {
                fillRatio = 1.0;
            }
            else if (snap.Phase == PomodoroPhase.Focus && i == snap.SessionIndex - 1)
            {
                fillRatio = elapsedFocus;
            }

            if (fillRatio > 0.0)
            {
                int fw = Math.Max(6, (int)Math.Round(dashW * fillRatio));
                canvas.FillAntiAliasedRoundedRect(x, dashY, fw, 5, 2.5, ringColor, null);
            }
        }

        return EncodePng(canvas.RgbaBytes, width, height);
    }

    public static byte[] RenderStatsChartPng(
        PomodoroSnapshot snap,
        int width = StatsWidth,
        int height = StatsHeight)
    {
        var canvas = new PomoCanvas(width, height);
        canvas.FillAntiAliasedRoundedRect(0, 0, width, height, 12.0, CardBg, CardBorder);

        int weekTotal = snap.Last7Days.Sum(d => d.Sessions);
        const int padL = 18;
        const int padR = 18;

        canvas.DrawText("POMODORO SESSIONS (7 DAYS)", padL, 14, TextPrimary, scale: 2);
        string sub = $"TODAY: {snap.TodaySessions} SESSIONS ({snap.TodaySessions * snap.FocusMinutes}M)  |  WEEK: {weekTotal} SESSIONS";
        canvas.DrawText(sub, padL, 34, TextSecondary, scale: 1);

        const int plotL = 44;
        int plotR = width - padR;
        const int plotT = 58;
        int plotB = height - 32;
        int plotW = plotR - plotL;
        int plotH = plotB - plotT;

        int maxS = Math.Max(4, snap.Last7Days.Max(d => d.Sessions));
        int yMax = ((maxS + 1) / 2) * 2;
        int yMid = yMax / 2;

        canvas.DrawDashedHLine(plotL, plotR, plotT, GridLine);
        canvas.DrawDashedHLine(plotL, plotR, plotT + (plotH / 2), GridLine);
        canvas.DrawHLine(plotL, plotR, plotB, AxisLine);

        DrawRightAlignedLabel(canvas, yMax.ToString(CultureInfo.InvariantCulture), plotL - 8, plotT - 4, TextSecondary);
        DrawRightAlignedLabel(canvas, yMid.ToString(CultureInfo.InvariantCulture), plotL - 8, plotT + (plotH / 2) - 4, TextSecondary);
        DrawRightAlignedLabel(canvas, "0", plotL - 8, plotB - 4, TextSecondary);

        int count = snap.Last7Days.Count;
        if (count > 0)
        {
            double slot = (double)plotW / count;
            int barW = Math.Min(34, (int)Math.Floor(slot * 0.52));

            for (int i = 0; i < count; i++)
            {
                var d = snap.Last7Days[i];
                int cx = plotL + (int)Math.Round((i + 0.5) * slot);
                int left = cx - (barW / 2);

                canvas.FillAntiAliasedRoundedRect(left, plotT + 2, barW, plotH - 2, 4.0, TrackBar, null);

                if (d.Sessions > 0)
                {
                    int bh = Math.Max(6, (int)Math.Round(((double)d.Sessions / yMax) * (plotH - 2)));
                    canvas.FillAntiAliasedRoundedRect(left, plotB - bh, barW, bh, 4.0, d.IsToday ? BarToday : BarNormal, null);

                    string countStr = d.Sessions.ToString(CultureInfo.InvariantCulture);
                    int cw = PomoCanvas.MeasureText(countStr, scale: 1);
                    canvas.DrawText(countStr, cx - (cw / 2), Math.Max(plotT, plotB - bh - 13), TextPrimary, scale: 1);
                }

                string dayLbl = d.IsToday ? $"{d.Label.ToUpperInvariant()}*" : d.Label.ToUpperInvariant();
                int dw = PomoCanvas.MeasureText(dayLbl, scale: 1);
                canvas.DrawText(dayLbl, cx - (dw / 2), plotB + 9, d.IsToday ? TextPrimary : TextSecondary, scale: 1);
            }
        }

        return EncodePng(canvas.RgbaBytes, width, height);
    }

    private static void DrawRightAlignedLabel(PomoCanvas canvas, string text, int rightX, int y, Rgba color)
    {
        int w = PomoCanvas.MeasureText(text, scale: 1);
        canvas.DrawText(text, Math.Max(4, rightX - w), y, color, scale: 1);
    }

    private static byte[] EncodePng(byte[] rgba, int width, int height)
    {
        using var output = new MemoryStream(width * height / 6);

        ReadOnlySpan<byte> pngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
        output.Write(pngSignature);

        Span<byte> ihdr = stackalloc byte[13];
        WriteBigEndian(ihdr, 0, (uint)width);
        WriteBigEndian(ihdr, 4, (uint)height);
        ihdr[8] = 8;
        ihdr[9] = 6;
        ihdr[10] = 0;
        ihdr[11] = 0;
        ihdr[12] = 0;
        WriteChunk(output, "IHDR"u8, ihdr);

        int stride = width * 4;
        byte[] compressed;
        using (var zlibOut = new MemoryStream())
        {
            using (var zlib = new ZLibStream(zlibOut, CompressionLevel.Optimal, leaveOpen: true))
            {
                byte[] scanline = new byte[stride + 1];
                for (int y = 0; y < height; y++)
                {
                    scanline[0] = 1;
                    int rowStart = y * stride;
                    for (int x = 0; x < stride; x++)
                    {
                        byte cur = rgba[rowStart + x];
                        byte left = x >= 4 ? rgba[rowStart + x - 4] : (byte)0;
                        scanline[x + 1] = (byte)(cur - left);
                    }

                    zlib.Write(scanline, 0, scanline.Length);
                }
            }

            compressed = zlibOut.ToArray();
        }

        WriteChunk(output, "IDAT"u8, compressed);
        WriteChunk(output, "IEND"u8, ReadOnlySpan<byte>.Empty);
        return output.ToArray();
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> lenBytes = stackalloc byte[4];
        WriteBigEndian(lenBytes, 0, (uint)data.Length);
        output.Write(lenBytes);
        output.Write(type);
        output.Write(data);

        uint crc = ComputeCrc32(type, data);
        Span<byte> crcBytes = stackalloc byte[4];
        WriteBigEndian(crcBytes, 0, crc);
        output.Write(crcBytes);
    }

    private static void WriteBigEndian(Span<byte> buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static readonly uint[] Crc32Table = BuildCrc32Table();

    private static uint[] BuildCrc32Table()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320U ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }

    private static uint ComputeCrc32(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFU;
        foreach (byte b in type)
        {
            crc = Crc32Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        foreach (byte b in data)
        {
            crc = Crc32Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFU;
    }

    private sealed class PomoCanvas
    {
        public int Width { get; }

        public int Height { get; }

        public byte[] RgbaBytes { get; }

        public PomoCanvas(int width, int height)
        {
            Width = width;
            Height = height;
            RgbaBytes = new byte[width * height * 4];
        }

        public void BlendPixel(int x, int y, Rgba color, double coverage = 1.0)
        {
            if ((uint)x >= (uint)Width || (uint)y >= (uint)Height || coverage <= 0.0)
            {
                return;
            }

            int srcA = (int)Math.Round(color.A * Math.Clamp(coverage, 0.0, 1.0));
            if (srcA <= 0)
            {
                return;
            }

            int idx = ((y * Width) + x) * 4;
            if (srcA >= 255)
            {
                RgbaBytes[idx] = color.R;
                RgbaBytes[idx + 1] = color.G;
                RgbaBytes[idx + 2] = color.B;
                RgbaBytes[idx + 3] = 255;
                return;
            }

            int dstA = RgbaBytes[idx + 3];
            if (dstA == 0)
            {
                RgbaBytes[idx] = color.R;
                RgbaBytes[idx + 1] = color.G;
                RgbaBytes[idx + 2] = color.B;
                RgbaBytes[idx + 3] = (byte)srcA;
                return;
            }

            int invSrcA = 255 - srcA;
            int outA = srcA + ((dstA * invSrcA) / 255);
            if (outA <= 0)
            {
                return;
            }

            RgbaBytes[idx] = (byte)(((color.R * srcA) + ((RgbaBytes[idx] * dstA * invSrcA) / 255)) / outA);
            RgbaBytes[idx + 1] = (byte)(((color.G * srcA) + ((RgbaBytes[idx + 1] * dstA * invSrcA) / 255)) / outA);
            RgbaBytes[idx + 2] = (byte)(((color.B * srcA) + ((RgbaBytes[idx + 2] * dstA * invSrcA) / 255)) / outA);
            RgbaBytes[idx + 3] = (byte)Math.Min(255, outA);
        }

        public void DrawHLine(int x1, int x2, int y, Rgba color)
        {
            for (int x = x1; x <= x2; x++)
            {
                BlendPixel(x, y, color);
            }
        }

        public void DrawDashedHLine(int x1, int x2, int y, Rgba color)
        {
            for (int x = x1; x <= x2; x++)
            {
                if (((x - x1) % 6) < 3)
                {
                    BlendPixel(x, y, color);
                }
            }
        }

        public void FillRect(int x, int y, int w, int h, Rgba color)
        {
            int xStart = Math.Max(0, x);
            int yStart = Math.Max(0, y);
            int xEnd = Math.Min(Width, x + w);
            int yEnd = Math.Min(Height, y + h);

            for (int py = yStart; py < yEnd; py++)
            {
                for (int px = xStart; px < xEnd; px++)
                {
                    BlendPixel(px, py, color);
                }
            }
        }

        public void FillAntiAliasedRoundedRect(int x, int y, int w, int h, double radius, Rgba fill, Rgba? border)
        {
            double r = Math.Min(radius, Math.Min(w, h) / 2.0);
            double halfW = w / 2.0;
            double halfH = h / 2.0;
            double cx = x + halfW;
            double cy = y + halfH;
            double bx = Math.Max(0.0, halfW - r);
            double by = Math.Max(0.0, halfH - r);

            int xStart = Math.Max(0, x - 1);
            int yStart = Math.Max(0, y - 1);
            int xEnd = Math.Min(Width - 1, x + w);
            int yEnd = Math.Min(Height - 1, y + h);

            for (int py = yStart; py <= yEnd; py++)
            {
                double qy = Math.Abs((py + 0.5) - cy) - by;
                for (int px = xStart; px <= xEnd; px++)
                {
                    double qx = Math.Abs((px + 0.5) - cx) - bx;
                    double ox = Math.Max(qx, 0.0);
                    double oy = Math.Max(qy, 0.0);
                    double sdf = Math.Sqrt((ox * ox) + (oy * oy)) + Math.Min(Math.Max(qx, qy), 0.0) - r;

                    double fillCov = Math.Clamp(0.5 - sdf, 0.0, 1.0);
                    if (fillCov > 0.0)
                    {
                        BlendPixel(px, py, fill, fillCov);
                    }

                    if (border is Rgba bCol)
                    {
                        double borderDist = Math.Abs(sdf + 0.5);
                        double borderCov = Math.Clamp(1.0 - borderDist, 0.0, 1.0);
                        if (borderCov > 0.0)
                        {
                            BlendPixel(px, py, bCol, borderCov);
                        }
                    }
                }
            }
        }

        public void DrawRingArc(
            int cx,
            int cy,
            double radius,
            double halfThickness,
            double startRad,
            double sweepRad,
            Rgba color)
        {
            if (sweepRad <= 0.0)
            {
                return;
            }

            bool fullCircle = sweepRad >= (Math.PI * 2.0) - 0.0001;
            double outerBound = radius + halfThickness + 2.0;
            int minX = Math.Max(0, (int)Math.Floor(cx - outerBound));
            int maxX = Math.Min(Width - 1, (int)Math.Ceiling(cx + outerBound));
            int minY = Math.Max(0, (int)Math.Floor(cy - outerBound));
            int maxY = Math.Min(Height - 1, (int)Math.Ceiling(cy + outerBound));

            double endRad = startRad + sweepRad;
            double capStartX = cx + (Math.Cos(startRad) * radius);
            double capStartY = cy + (Math.Sin(startRad) * radius);
            double capEndX = cx + (Math.Cos(endRad) * radius);
            double capEndY = cy + (Math.Sin(endRad) * radius);

            for (int py = minY; py <= maxY; py++)
            {
                double dy = (py + 0.5) - cy;
                for (int px = minX; px <= maxX; px++)
                {
                    double dx = (px + 0.5) - cx;
                    double rDist = Math.Sqrt((dx * dx) + (dy * dy));
                    double radialDelta = Math.Abs(rDist - radius);

                    double cov = 0.0;
                    if (fullCircle)
                    {
                        cov = Math.Clamp(halfThickness + 0.5 - radialDelta, 0.0, 1.0);
                    }
                    else
                    {
                        double angle = Math.Atan2(dy, dx);
                        double rel = angle - startRad;
                        while (rel < 0.0)
                        {
                            rel += Math.PI * 2.0;
                        }

                        while (rel >= Math.PI * 2.0)
                        {
                            rel -= Math.PI * 2.0;
                        }

                        if (rel <= sweepRad)
                        {
                            cov = Math.Clamp(halfThickness + 0.5 - radialDelta, 0.0, 1.0);
                        }
                        else
                        {
                            double dsx = (px + 0.5) - capStartX;
                            double dsy = (py + 0.5) - capStartY;
                            double dStart = Math.Sqrt((dsx * dsx) + (dsy * dsy));

                            double dex = (px + 0.5) - capEndX;
                            double dey = (py + 0.5) - capEndY;
                            double dEnd = Math.Sqrt((dex * dex) + (dey * dey));

                            double capDist = Math.Min(dStart, dEnd);
                            cov = Math.Clamp(halfThickness + 0.5 - capDist, 0.0, 1.0);
                        }
                    }

                    if (cov > 0.0)
                    {
                        BlendPixel(px, py, color, cov);
                    }
                }
            }
        }

        public static int MeasureText(string text, int scale)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0;
            }

            return ((text.Length * 6) - 1) * scale;
        }

        public void DrawText(string text, int x, int y, Rgba color, int scale = 1)
        {
            int cursorX = x;
            foreach (char rawChar in text)
            {
                char ch = char.ToUpperInvariant(rawChar);
                if (Glyphs.TryGetValue(ch, out var rows))
                {
                    for (int row = 0; row < 7; row++)
                    {
                        byte bits = rows[row];
                        for (int col = 0; col < 5; col++)
                        {
                            if ((bits & (1 << (4 - col))) != 0)
                            {
                                FillRect(cursorX + (col * scale), y + (row * scale), scale, scale, color);
                            }
                        }
                    }
                }

                cursorX += 6 * scale;
            }
        }

        private static readonly Dictionary<char, byte[]> Glyphs = new()
        {
            [' '] = [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00],
            ['0'] = [0x0E, 0x11, 0x13, 0x15, 0x19, 0x11, 0x0E],
            ['1'] = [0x04, 0x0C, 0x04, 0x04, 0x04, 0x04, 0x0E],
            ['2'] = [0x0E, 0x11, 0x01, 0x06, 0x08, 0x10, 0x1F],
            ['3'] = [0x0E, 0x11, 0x01, 0x06, 0x01, 0x11, 0x0E],
            ['4'] = [0x02, 0x06, 0x0A, 0x12, 0x1F, 0x02, 0x02],
            ['5'] = [0x1F, 0x10, 0x1E, 0x01, 0x01, 0x11, 0x0E],
            ['6'] = [0x06, 0x08, 0x10, 0x1E, 0x11, 0x11, 0x0E],
            ['7'] = [0x1F, 0x01, 0x02, 0x04, 0x08, 0x08, 0x08],
            ['8'] = [0x0E, 0x11, 0x11, 0x0E, 0x11, 0x11, 0x0E],
            ['9'] = [0x0E, 0x11, 0x11, 0x0F, 0x01, 0x02, 0x0C],
            ['A'] = [0x0E, 0x11, 0x11, 0x1F, 0x11, 0x11, 0x11],
            ['B'] = [0x1E, 0x11, 0x11, 0x1E, 0x11, 0x11, 0x1E],
            ['C'] = [0x0E, 0x11, 0x10, 0x10, 0x10, 0x11, 0x0E],
            ['D'] = [0x1C, 0x12, 0x11, 0x11, 0x11, 0x12, 0x1C],
            ['E'] = [0x1F, 0x10, 0x10, 0x1E, 0x10, 0x10, 0x1F],
            ['F'] = [0x1F, 0x10, 0x10, 0x1E, 0x10, 0x10, 0x10],
            ['G'] = [0x0E, 0x11, 0x10, 0x17, 0x11, 0x11, 0x0F],
            ['H'] = [0x11, 0x11, 0x11, 0x1F, 0x11, 0x11, 0x11],
            ['I'] = [0x0E, 0x04, 0x04, 0x04, 0x04, 0x04, 0x0E],
            ['J'] = [0x07, 0x02, 0x02, 0x02, 0x12, 0x12, 0x0C],
            ['K'] = [0x11, 0x12, 0x14, 0x18, 0x14, 0x12, 0x11],
            ['L'] = [0x10, 0x10, 0x10, 0x10, 0x10, 0x10, 0x1F],
            ['M'] = [0x11, 0x1B, 0x15, 0x15, 0x11, 0x11, 0x11],
            ['N'] = [0x11, 0x19, 0x15, 0x13, 0x11, 0x11, 0x11],
            ['O'] = [0x0E, 0x11, 0x11, 0x11, 0x11, 0x11, 0x0E],
            ['P'] = [0x1E, 0x11, 0x11, 0x1E, 0x10, 0x10, 0x10],
            ['Q'] = [0x0E, 0x11, 0x11, 0x11, 0x15, 0x12, 0x0D],
            ['R'] = [0x1E, 0x11, 0x11, 0x1E, 0x14, 0x12, 0x11],
            ['S'] = [0x0E, 0x11, 0x10, 0x0E, 0x01, 0x11, 0x0E],
            ['T'] = [0x1F, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04],
            ['U'] = [0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x0E],
            ['V'] = [0x11, 0x11, 0x11, 0x11, 0x0A, 0x0A, 0x04],
            ['W'] = [0x11, 0x11, 0x11, 0x15, 0x15, 0x1B, 0x11],
            ['X'] = [0x11, 0x11, 0x0A, 0x04, 0x0A, 0x11, 0x11],
            ['Y'] = [0x11, 0x11, 0x0A, 0x04, 0x04, 0x04, 0x04],
            ['Z'] = [0x1F, 0x01, 0x02, 0x04, 0x08, 0x10, 0x1F],
            ['.'] = [0x00, 0x00, 0x00, 0x00, 0x00, 0x0C, 0x0C],
            [','] = [0x00, 0x00, 0x00, 0x00, 0x04, 0x04, 0x08],
            [':'] = [0x00, 0x04, 0x04, 0x00, 0x04, 0x04, 0x00],
            ['-'] = [0x00, 0x00, 0x00, 0x1F, 0x00, 0x00, 0x00],
            ['/'] = [0x01, 0x01, 0x02, 0x04, 0x08, 0x10, 0x10],
            ['('] = [0x02, 0x04, 0x08, 0x08, 0x08, 0x04, 0x02],
            [')'] = [0x08, 0x04, 0x02, 0x02, 0x02, 0x04, 0x08],
            ['*'] = [0x00, 0x15, 0x0E, 0x1F, 0x0E, 0x15, 0x00],
            ['|'] = [0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04],
            ['•'] = [0x00, 0x00, 0x06, 0x06, 0x00, 0x00, 0x00],
        };
    }
}

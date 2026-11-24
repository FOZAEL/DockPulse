using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace DockPulse.Services;

/// <summary>
/// Pure C# RGBA PNG renderer for the minimal segmented radial bar Prayer Schedule card,
/// sharing the exact dark-slate, sky-cyan, and soft-violet design palette of NetworkChartRenderer.
/// </summary>
public static class PrayerChartRenderer
{
    public const int DefaultWidth = 820;
    public const int DefaultHeight = 218;

    private readonly record struct Rgba(byte R, byte G, byte B, byte A = 255);

    private readonly record struct MilestonePoint(string Name, int Minutes, string ArrowSuffix);

    private static readonly Rgba CardBg = new(28, 32, 40);
    private static readonly Rgba CardBorder = new(52, 59, 74);
    private static readonly Rgba GridLine = new(44, 50, 64);
    private static readonly Rgba AxisLine = new(68, 76, 94);
    private static readonly Rgba TextPrimary = new(241, 245, 249);
    private static readonly Rgba TextSecondary = new(156, 168, 188);
    private static readonly Rgba CyanAccent = new(56, 189, 248);
    private static readonly Rgba CyanHighlight = new(125, 211, 252);
    private static readonly Rgba VioletAccent = new(167, 139, 250);
    private static readonly Rgba VioletHighlight = new(196, 181, 253);
    private static readonly Rgba VioletBarMuted = new(167, 139, 250, 115);
    private static readonly Rgba GrayBar = new(75, 85, 101);
    private static readonly Rgba GrayPointer = new(100, 116, 139);
    private static readonly Rgba GrayLabel = new(118, 131, 150);
    private static readonly Rgba PillFill = new(56, 189, 248, 34);
    private static readonly Rgba PillBorder = new(56, 189, 248, 135);
    private static readonly Rgba TargetBarGlow = new(56, 189, 248, 82);

    private static readonly int[] MilestoneBarIndices = [3, 13, 24, 36, 47, 57];

    public static byte[] RenderScheduleCardPng(
        PrayerScheduleSnapshot snapshot,
        int width = DefaultWidth,
        int height = DefaultHeight)
    {
        var canvas = new RadialCanvas(width, height);

        // 1. Rounded card surface & 1px border (identical to NetworkChartRenderer)
        canvas.FillAntiAliasedRoundedRect(0, 0, width, height, 12.0, CardBg, CardBorder);

        // 2. Top header row + legend swatches
        const int padLeft = 18;
        const int padRight = 18;

        canvas.DrawText("PRAYER SCHEDULE", padLeft, 14, TextPrimary, scale: 2);
        string subtitle = $"{snapshot.Region.DisplayName.ToUpperInvariant()}  |  {snapshot.UtcOffsetLabel}  |  LOCAL {snapshot.RegionNow:HH:mm}";
        canvas.DrawText(subtitle, padLeft, 34, TextSecondary, scale: 1);

        (Rgba Color, string Label)[] legends =
        [
            (GrayPointer, "PASSED"),
            (CyanAccent, "COUNTDOWN"),
            (VioletAccent, "UPCOMING"),
        ];

        const int swatchSize = 8;
        const int swatchGap = 5;
        const int legendGap = 12;
        int lx = width - padRight;
        for (int i = legends.Length - 1; i >= 0; i--)
        {
            int tw = RadialCanvas.MeasureText(legends[i].Label, scale: 1);
            lx -= tw;
            canvas.DrawText(legends[i].Label, lx, 16, TextPrimary, scale: 1);
            lx -= swatchGap + swatchSize;
            canvas.FillAntiAliasedRoundedRect(lx, 16, swatchSize, swatchSize, 2.0, legends[i].Color, null);
            lx -= legendGap;
        }

        // Subtle dashed grid lines + horizon baseline
        int cx = width / 2;
        const int baseY = 198;
        canvas.DrawDashedHLine(padLeft, width - padRight, 96, GridLine);
        canvas.DrawDashedHLine(padLeft, width - padRight, 148, GridLine);
        canvas.DrawHLine(padLeft, width - padRight, baseY, AxisLine);

        // Extract the 6 daily milestones (Fajr, Sunrise, Dhuhr, Asr, Maghrib, Isha)
        var milestones = ExtractSixMilestones(snapshot.TodayEntries);
        double nowMinutes = (snapshot.RegionNow.Hour * 60.0) + snapshot.RegionNow.Minute + (snapshot.RegionNow.Second / 60.0);

        const int totalBars = 60; // 0..60 -> 61 radial bars spaced every 3 degrees (180°..0°)
        Span<(int K, double T)> anchors = stackalloc (int, double)[8];
        anchors[0] = (0, 0.0);
        for (int i = 0; i < 6; i++)
        {
            anchors[i + 1] = (MilestoneBarIndices[i], milestones[i].Minutes);
        }

        anchors[7] = (totalBars, 1440.0);

        double nowK = 0.0;
        for (int i = 0; i < anchors.Length - 1; i++)
        {
            if (nowMinutes >= anchors[i].T && nowMinutes <= anchors[i + 1].T)
            {
                double span = Math.Max(1.0, anchors[i + 1].T - anchors[i].T);
                nowK = anchors[i].K + (((nowMinutes - anchors[i].T) / span) * (anchors[i + 1].K - anchors[i].K));
                break;
            }
        }

        string targetName = snapshot.CardPrayerName;
        int targetMilestoneIdx = -1;
        for (int i = 0; i < milestones.Count; i++)
        {
            if (string.Equals(milestones[i].Name, targetName, StringComparison.OrdinalIgnoreCase))
            {
                targetMilestoneIdx = i;
                break;
            }
        }

        int targetK = targetMilestoneIdx >= 0 ? MilestoneBarIndices[targetMilestoneIdx] : -1;

        const double rIn = 108.0;
        const double rOut = 122.0;
        const double rPtrIn = 103.0;
        const double rPtrOut = 136.0;

        // Ambient radial cyan glow inside the semicircle
        canvas.FillSemicircleGlow(cx, baseY, rOut, CyanAccent, maxAlpha: 20);

        // 3. Draw all 61 anti-aliased radial bars around the semicircle
        for (int k = 0; k <= totalBars; k++)
        {
            double degAngle = 180.0 - (k * 3.0);
            double rad = degAngle * (Math.PI / 180.0);
            double cos = Math.Cos(rad);
            double sin = Math.Sin(rad);

            int mIdx = Array.IndexOf(MilestoneBarIndices, k);
            bool isPointer = mIdx >= 0;
            bool isTargetPtr = isPointer && mIdx == targetMilestoneIdx;
            bool isPassed = snapshot.NextPrayerIsTomorrow
                ? !isTargetPtr
                : (k <= nowK && !(isTargetPtr && snapshot.IsExactPrayerMinute));
            bool isCountdownWindow = !snapshot.NextPrayerIsTomorrow && k > nowK && targetK >= 0 && k <= targetK;

            double inner = isPointer ? rPtrIn : rIn;
            double outer = isPointer ? (isTargetPtr ? rPtrOut + 3.0 : rPtrOut) : rOut;

            double x0 = cx + (cos * inner);
            double y0 = baseY - (sin * inner);
            double x1 = cx + (cos * outer);
            double y1 = baseY - (sin * outer);

            if (isTargetPtr)
            {
                canvas.DrawCapsuleLine(x0, y0, x1, y1, halfThickness: 4.2, TargetBarGlow);
                canvas.DrawCapsuleLine(x0, y0, x1, y1, halfThickness: 2.0, CyanHighlight);
            }
            else if (isPassed)
            {
                canvas.DrawCapsuleLine(x0, y0, x1, y1, halfThickness: isPointer ? 1.6 : 1.2, isPointer ? GrayPointer : GrayBar);
            }
            else if (isCountdownWindow)
            {
                canvas.DrawCapsuleLine(x0, y0, x1, y1, halfThickness: 1.25, CyanAccent);
            }
            else
            {
                canvas.DrawCapsuleLine(x0, y0, x1, y1, halfThickness: isPointer ? 1.6 : 1.2, isPointer ? VioletHighlight : VioletBarMuted);
            }
        }

        // 4. Attach prayer name + time label to each of the 6 pointer bars
        for (int idx = 0; idx < milestones.Count; idx++)
        {
            var m = milestones[idx];
            int k = MilestoneBarIndices[idx];
            double degAngle = 180.0 - (k * 3.0);
            double rad = degAngle * (Math.PI / 180.0);
            double cos = Math.Cos(rad);
            double sin = Math.Sin(rad);

            bool isTarget = idx == targetMilestoneIdx;
            bool isPassed = snapshot.NextPrayerIsTomorrow
                ? !isTarget
                : (m.Minutes <= nowMinutes && !(isTarget && snapshot.IsExactPrayerMinute));
            bool onLeft = degAngle > 90.0;

            int tipX = (int)Math.Round(cx + (cos * (rPtrOut + 7.0)));
            int tipY = (int)Math.Round(baseY - (sin * (rPtrOut + 5.0)));

            string timeStr = FormatMinutes(m.Minutes);
            string labelText = $"{m.Name.ToUpperInvariant()}{m.ArrowSuffix}  {timeStr}";

            int tw = RadialCanvas.MeasureText(labelText, scale: 1);
            const int pillPadX = 8;
            const int pillH = 19;
            int boxW = tw + (pillPadX * 2);
            int boxX = onLeft ? (tipX - boxW) : tipX;
            int boxY = tipY - (pillH / 2);

            Rgba textCol;
            if (isTarget)
            {
                canvas.FillAntiAliasedRoundedRect(boxX, boxY, boxW, pillH, pillH / 2.0, PillFill, PillBorder);
                textCol = CyanHighlight;
            }
            else if (isPassed)
            {
                textCol = GrayLabel;
            }
            else
            {
                textCol = TextPrimary;
            }

            canvas.DrawText(labelText, boxX + pillPadX, boxY + 6, textCol, scale: 1);
        }

        // 5. Center hero countdown inside the barred semicircle
        string centerTitle = targetName.ToUpperInvariant();
        int centerTitleW = RadialCanvas.MeasureText(centerTitle, scale: 3);
        canvas.DrawText(centerTitle, cx - (centerTitleW / 2), baseY - 86, TextPrimary, scale: 3);

        int totalUntilMins = Math.Max(0, (int)Math.Ceiling(snapshot.TimeUntilNext.TotalMinutes));
        string pillText = snapshot.IsExactPrayerMinute
            ? $"NOW  •  {snapshot.RegionNow:HH:mm}"
            : $"TIME LEFT  •  {totalUntilMins / 60}H {(totalUntilMins % 60).ToString("00", CultureInfo.InvariantCulture)}M";

        int cdTextW = RadialCanvas.MeasureText(pillText, scale: 1);
        int cdPillW = Math.Max(150, cdTextW + 24);
        const int cdPillH = 23;
        int cdPillX = cx - (cdPillW / 2);
        int cdPillY = baseY - 53;
        canvas.FillAntiAliasedRoundedRect(cdPillX, cdPillY, cdPillW, cdPillH, cdPillH / 2.0, PillFill, PillBorder);
        canvas.DrawText(pillText, cx - (cdTextW / 2), cdPillY + 8, CyanHighlight, scale: 1);

        string bottomCaption = snapshot.IsExactPrayerMinute
            ? "PRAYER TIME REACHED"
            : $"STARTS AT {snapshot.NextPrayerTime:HH:mm}{(snapshot.NextPrayerIsTomorrow ? " (TOMORROW)" : string.Empty)}";
        int capW = RadialCanvas.MeasureText(bottomCaption, scale: 1);
        canvas.DrawText(bottomCaption, cx - (capW / 2), baseY - 19, TextSecondary, scale: 1);

        return EncodePng(canvas.RgbaBytes, width, height);
    }

    private static List<MilestonePoint> ExtractSixMilestones(IReadOnlyList<PrayerEntry> entries)
    {
        PrayerEntry FindOrFallback(string name, int fallbackIdx) =>
            entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? entries[Math.Clamp(fallbackIdx, 0, entries.Count - 1)];

        static int ToMins(PrayerEntry e) => (e.Time.Hour * 60) + e.Time.Minute;

        var fajr = FindOrFallback("Fajr", 0);
        var sunrise = FindOrFallback("Sunrise", 1);
        var dhuhr = FindOrFallback("Dhuhr", 2);
        var asr = FindOrFallback("Asr", 3);
        var maghrib = FindOrFallback("Maghrib", entries.Count - 2);
        var isha = FindOrFallback("Isha", entries.Count - 1);

        return
        [
            new("Fajr", ToMins(fajr), string.Empty),
            new("Sunrise", ToMins(sunrise), " ↑"),
            new("Dhuhr", ToMins(dhuhr), string.Empty),
            new("Asr", ToMins(asr), string.Empty),
            new("Maghrib", ToMins(maghrib), " ↓"),
            new("Isha", ToMins(isha), string.Empty),
        ];
    }

    private static string FormatMinutes(int totalMinutes)
    {
        int wrapped = ((totalMinutes % 1440) + 1440) % 1440;
        return $"{(wrapped / 60).ToString("00", CultureInfo.InvariantCulture)}:{(wrapped % 60).ToString("00", CultureInfo.InvariantCulture)}";
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
        ihdr[9] = 6; // RGBA
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
                    scanline[0] = 1; // Sub filter
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

    private sealed class RadialCanvas
    {
        public int Width { get; }

        public int Height { get; }

        public byte[] RgbaBytes { get; }

        public RadialCanvas(int width, int height)
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
                        // 1px centered inner border along sdf in [-1.0, 0.0]
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

        public void FillSemicircleGlow(int cx, int baseY, double maxRadius, Rgba tint, byte maxAlpha)
        {
            int xStart = Math.Max(0, (int)Math.Floor(cx - maxRadius));
            int xEnd = Math.Min(Width - 1, (int)Math.Ceiling(cx + maxRadius));
            int yStart = Math.Max(0, (int)Math.Floor(baseY - maxRadius));
            int yEnd = Math.Min(Height - 1, baseY);

            double glowCenterY = baseY - 28.0;
            for (int py = yStart; py <= yEnd; py++)
            {
                double dySemi = (py + 0.5) - baseY;
                double dyGlow = (py + 0.5) - glowCenterY;
                for (int px = xStart; px <= xEnd; px++)
                {
                    double dx = (px + 0.5) - cx;
                    if ((dx * dx) + (dySemi * dySemi) > maxRadius * maxRadius)
                    {
                        continue;
                    }

                    double dist = Math.Sqrt((dx * dx) + (dyGlow * dyGlow));
                    double t = 1.0 - Math.Clamp(dist / maxRadius, 0.0, 1.0);
                    if (t > 0.0)
                    {
                        byte alpha = (byte)Math.Round(maxAlpha * t * t);
                        if (alpha > 0)
                        {
                            BlendPixel(px, py, tint with { A = alpha });
                        }
                    }
                }
            }
        }

        public void DrawCapsuleLine(double x0, double y0, double x1, double y1, double halfThickness, Rgba color)
        {
            int pad = (int)Math.Ceiling(halfThickness + 1.5);
            int minX = Math.Max(0, (int)Math.Floor(Math.Min(x0, x1)) - pad);
            int maxX = Math.Min(Width - 1, (int)Math.Ceiling(Math.Max(x0, x1)) + pad);
            int minY = Math.Max(0, (int)Math.Floor(Math.Min(y0, y1)) - pad);
            int maxY = Math.Min(Height - 1, (int)Math.Ceiling(Math.Max(y0, y1)) + pad);

            double vx = x1 - x0;
            double vy = y1 - y0;
            double lenSq = Math.Max(0.0001, (vx * vx) + (vy * vy));

            for (int py = minY; py <= maxY; py++)
            {
                double cy = py + 0.5;
                for (int px = minX; px <= maxX; px++)
                {
                    double cx = px + 0.5;
                    double t = Math.Clamp((((cx - x0) * vx) + ((cy - y0) * vy)) / lenSq, 0.0, 1.0);
                    double projX = x0 + (t * vx);
                    double projY = y0 + (t * vy);
                    double dx = cx - projX;
                    double dy = cy - projY;
                    double dist = Math.Sqrt((dx * dx) + (dy * dy));
                    double cov = Math.Clamp(halfThickness + 0.5 - dist, 0.0, 1.0);
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
            ['%'] = [0x18, 0x19, 0x02, 0x04, 0x08, 0x13, 0x03],
            ['+'] = [0x00, 0x04, 0x04, 0x1F, 0x04, 0x04, 0x00],
            ['|'] = [0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04],
            ['_'] = [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x1F],
            ['•'] = [0x00, 0x00, 0x06, 0x06, 0x00, 0x00, 0x00],
            ['↑'] = [0x04, 0x0E, 0x15, 0x04, 0x04, 0x04, 0x04],
            ['↓'] = [0x04, 0x04, 0x04, 0x04, 0x15, 0x0E, 0x04],
        };
    }
}

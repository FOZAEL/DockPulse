using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;

namespace DockPulse.Services;

public static class NetworkChartRenderer
{
    public const int DefaultWidth = 600;
    public const int DefaultHeight = 260;

    private readonly record struct Rgba(byte R, byte G, byte B, byte A = 255);

    private readonly record struct ChartPalette(
        Rgba CardBg,
        Rgba CardBorder,
        Rgba GridLine,
        Rgba AxisLine,
        Rgba TrackElapsed,
        Rgba TrackFuture,
        Rgba TextPrimary,
        Rgba TextSecondary,
        Rgba DownloadBar,
        Rgba DownloadHighlight,
        Rgba UploadBar,
        Rgba UploadHighlight,
        Rgba ActiveMarker);

    private static readonly ChartPalette DarkPalette = new(
        CardBg: new(28, 32, 40),
        CardBorder: new(52, 59, 74),
        GridLine: new(44, 50, 64),
        AxisLine: new(68, 76, 94),
        TrackElapsed: new(36, 41, 52),
        TrackFuture: new(31, 35, 44),
        TextPrimary: new(241, 245, 249),
        TextSecondary: new(156, 168, 188),
        DownloadBar: new(56, 189, 248),
        DownloadHighlight: new(125, 211, 252),
        UploadBar: new(167, 139, 250),
        UploadHighlight: new(196, 181, 253),
        ActiveMarker: new(56, 189, 248));

    private static readonly ChartPalette LightPalette = new(
        CardBg: new(248, 250, 252),
        CardBorder: new(218, 226, 236),
        GridLine: new(226, 232, 240),
        AxisLine: new(196, 206, 220),
        TrackElapsed: new(235, 240, 246),
        TrackFuture: new(242, 245, 249),
        TextPrimary: new(15, 23, 42),
        TextSecondary: new(100, 116, 139),
        DownloadBar: new(2, 132, 199),
        DownloadHighlight: new(14, 165, 233),
        UploadBar: new(124, 58, 237),
        UploadHighlight: new(139, 92, 246),
        ActiveMarker: new(2, 132, 199));

    public static byte[] RenderBarChartPng(
        string title,
        string subtitle,
        IReadOnlyList<UsageBucket> buckets,
        int activeBucketIndex,
        bool isDarkTheme,
        int width = DefaultWidth,
        int height = DefaultHeight)
    {
        var palette = isDarkTheme ? DarkPalette : LightPalette;
        var canvas = new PixelCanvas(width, height);

        // Rounded card background & subtle 1px border
        canvas.FillRoundedRect(0, 0, width, height, 12, palette.CardBg);
        canvas.DrawRoundedRectBorder(0, 0, width, height, 12, palette.CardBorder);

        // Totals for legend
        ulong totalDown = 0;
        ulong totalUp = 0;
        ulong maxBucket = 0;
        for (int i = 0; i < buckets.Count; i++)
        {
            totalDown += buckets[i].ReceivedBytes;
            totalUp += buckets[i].SentBytes;
            if (buckets[i].TotalBytes > maxBucket)
            {
                maxBucket = buckets[i].TotalBytes;
            }
        }

        // Header: Title on left, Subtitle below it, Legend on top-right (auto-scale title for narrow Details cards)
        const int padLeft = 18;
        const int padRight = 18;

        string upperTitle = title.ToUpperInvariant();
        string upText = $"UP {NetworkUsageService.FormatBytes(totalUp)}";
        string downText = $"DOWN {NetworkUsageService.FormatBytes(totalDown)}";

        int upTextWidth = PixelCanvas.MeasureText(upText, scale: 1);
        int downTextWidth = PixelCanvas.MeasureText(downText, scale: 1);
        const int swatchSize = 8;
        const int swatchGap = 5;
        const int legendGap = 12;

        int upX = width - padRight - upTextWidth;
        int upSwatchX = upX - swatchGap - swatchSize;
        int downX = upSwatchX - legendGap - downTextWidth;
        int downSwatchX = downX - swatchGap - swatchSize;

        int titleScale = (padLeft + PixelCanvas.MeasureText(upperTitle, scale: 2) + 12 <= downSwatchX) ? 2 : 1;
        int titleY = titleScale == 2 ? 14 : 16;
        int subtitleY = titleScale == 2 ? 34 : 30;

        // In narrow details-pane cards a long title (e.g. an adapter name) can't share its row with the legend
        // even at scale 1; then the legend gets its own row under the subtitle, just above the plot.
        bool legendOwnRow = titleScale == 1 && padLeft + PixelCanvas.MeasureText(upperTitle, scale: 1) + 12 > downSwatchX;

        canvas.DrawText(upperTitle, padLeft, titleY, palette.TextPrimary, scale: titleScale);
        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            canvas.DrawText(subtitle, padLeft, subtitleY, palette.TextSecondary, scale: 1);
        }

        int legendY = legendOwnRow ? 44 : 16;
        canvas.FillRoundedRect(downSwatchX, legendY + 1, swatchSize, swatchSize, 2, palette.DownloadBar);
        canvas.DrawText(downText, downX, legendY, palette.TextPrimary, scale: 1);

        canvas.FillRoundedRect(upSwatchX, legendY + 1, swatchSize, swatchSize, 2, palette.UploadBar);
        canvas.DrawText(upText, upX, legendY, palette.TextPrimary, scale: 1);

        // Plot bounds
        const int plotLeft = 68;
        int plotRight = width - padRight;
        const int plotTop = 58;
        int plotBottom = height - 34;
        int plotWidth = Math.Max(40, plotRight - plotLeft);
        int plotHeight = Math.Max(40, plotBottom - plotTop);

        // Compute clean Y-axis ceiling
        ulong yMax = ComputeNiceCeiling(maxBucket);
        ulong yMid = yMax / 2;

        // Horizontal gridlines and Y-axis tick labels (0%, 50%, 100%)
        DrawHorizontalGrid(canvas, plotLeft, plotRight, plotTop, palette.GridLine, dashed: true);
        DrawHorizontalGrid(canvas, plotLeft, plotRight, plotTop + (plotHeight / 2), palette.GridLine, dashed: true);
        canvas.DrawHLine(plotLeft, plotRight, plotBottom, palette.AxisLine);

        DrawRightAlignedLabel(canvas, FormatShortAxis(yMax), plotLeft - 8, plotTop - 4, palette.TextSecondary);
        DrawRightAlignedLabel(canvas, FormatShortAxis(yMid), plotLeft - 8, plotTop + (plotHeight / 2) - 4, palette.TextSecondary);
        DrawRightAlignedLabel(canvas, "0", plotLeft - 8, plotBottom - 4, palette.TextSecondary);

        // Draw bars
        int count = buckets.Count;
        if (count > 0)
        {
            double slotWidth = (double)plotWidth / count;
            int barWidth = Math.Clamp((int)Math.Floor(slotWidth * 0.66), 3, 22);

            for (int i = 0; i < count; i++)
            {
                int slotCenterX = plotLeft + (int)Math.Round((i + 0.5) * slotWidth);
                int barLeft = slotCenterX - (barWidth / 2);
                bool isActive = i == activeBucketIndex;
                bool isFuture = activeBucketIndex >= 0 && i > activeBucketIndex;

                // Subtle slot track
                canvas.FillRoundedRect(
                    barLeft,
                    plotTop + 2,
                    barWidth,
                    plotHeight - 2,
                    2,
                    isFuture ? palette.TrackFuture : palette.TrackElapsed);

                var bucket = buckets[i];
                if (bucket.TotalBytes > 0 && yMax > 0)
                {
                    int totalPx = (int)Math.Round((double)bucket.TotalBytes / yMax * (plotHeight - 2));
                    if (totalPx < 3)
                    {
                        totalPx = 3;
                    }

                    if (totalPx > plotHeight - 2)
                    {
                        totalPx = plotHeight - 2;
                    }

                    int downPx = bucket.ReceivedBytes > 0
                        ? (int)Math.Round((double)bucket.ReceivedBytes / bucket.TotalBytes * totalPx)
                        : 0;
                    int upPx = totalPx - downPx;

                    // Ensure both segments remain visible when both are non-zero
                    if (bucket.ReceivedBytes > 0 && downPx == 0 && totalPx >= 2)
                    {
                        downPx = 1;
                        upPx = totalPx - 1;
                    }

                    if (bucket.SentBytes > 0 && upPx == 0 && totalPx >= 2)
                    {
                        upPx = 1;
                        downPx = totalPx - 1;
                    }

                    int barTop = plotBottom - totalPx;
                    var downColor = isActive ? palette.DownloadHighlight : palette.DownloadBar;
                    var upColor = isActive ? palette.UploadHighlight : palette.UploadBar;

                    if (upPx > 0 && downPx > 0)
                    {
                        // Bottom segment: Download (flat top, flat bottom)
                        canvas.FillRect(barLeft, plotBottom - downPx, barWidth, downPx, downColor);

                        // Top segment: Upload (rounded top corners)
                        canvas.FillTopRoundedRect(barLeft, barTop, barWidth, upPx, 2, upColor);
                    }
                    else if (downPx > 0)
                    {
                        canvas.FillTopRoundedRect(barLeft, barTop, barWidth, downPx, 2, downColor);
                    }
                    else if (upPx > 0)
                    {
                        canvas.FillTopRoundedRect(barLeft, barTop, barWidth, upPx, 2, upColor);
                    }
                }

                // Active bucket indicator bar cleanly below axis
                if (isActive)
                {
                    canvas.FillRect(slotCenterX - Math.Max(2, barWidth / 3), plotBottom + 4, Math.Max(4, (barWidth * 2) / 3), 2, palette.ActiveMarker);
                }

                // X-axis tick labels at clean intervals
                if (ShouldDrawXLabel(i, count))
                {
                    string lbl = buckets[i].Label;
                    int lblW = PixelCanvas.MeasureText(lbl, scale: 1);
                    int lblX = Math.Clamp(slotCenterX - (lblW / 2), plotLeft, plotRight - lblW);
                    var lblColor = isActive ? palette.TextPrimary : palette.TextSecondary;
                    canvas.DrawText(lbl, lblX, plotBottom + 10, lblColor, scale: 1);
                }
            }
        }

        return EncodePng(canvas.RgbaBytes, width, height);
    }

    public static string RenderBarChartDataUri(
        string title,
        string subtitle,
        IReadOnlyList<UsageBucket> buckets,
        int activeBucketIndex,
        bool isDarkTheme = true,
        int width = DefaultWidth,
        int height = DefaultHeight)
    {
        var png = RenderBarChartPng(title, subtitle, buckets, activeBucketIndex, isDarkTheme, width, height);
        return $"data:image/png;base64,{Convert.ToBase64String(png)}";
    }

    private static bool ShouldDrawXLabel(int index, int count)
    {
        if (count <= 12)
        {
            return true;
        }

        if (count == 24)
        {
            return index is 0 or 4 or 8 or 12 or 16 or 20 or 23;
        }

        // Daily month view (28..31 days): 1, 5, 10, 15, 20, 25, last day
        int day = index + 1;
        return day == 1 || day == count || (day % 5 == 0 && (count - day) >= 2);
    }

    private static ulong ComputeNiceCeiling(ulong maxBytes)
    {
        if (maxBytes == 0)
        {
            return 10UL * 1024 * 1024; // 10 MB default scale when zero
        }

        // Choose binary unit (KB, MB, GB, TB) so numeric ceiling is clean
        ulong unit = 1024UL; // KB
        if (maxBytes >= 1024UL * 1024 * 1024 * 1024)
        {
            unit = 1024UL * 1024 * 1024 * 1024;
        }
        else if (maxBytes >= 1024UL * 1024 * 1024)
        {
            unit = 1024UL * 1024 * 1024;
        }
        else if (maxBytes >= 1024UL * 1024)
        {
            unit = 1024UL * 1024;
        }

        double inUnits = (double)maxBytes / unit;
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(inUnits, 0.1))));
        double normalized = inUnits / magnitude;

        double niceNorm = normalized switch
        {
            <= 1.0 => 1.0,
            <= 1.5 => 1.5,
            <= 2.0 => 2.0,
            <= 2.5 => 2.5,
            <= 4.0 => 4.0,
            <= 5.0 => 5.0,
            <= 8.0 => 8.0,
            _ => 10.0,
        };

        double niceValue = niceNorm * magnitude;
        return (ulong)Math.Ceiling(niceValue * unit);
    }

    private static string FormatShortAxis(ulong bytes)
    {
        if (bytes == 0)
        {
            return "0";
        }

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unitIndex = 0;

        while (value >= 1024d && unitIndex < units.Length - 1)
        {
            value /= 1024d;
            unitIndex++;
        }

        string fmt = value >= 100d ? "0" : (Math.Abs(value - Math.Round(value)) < 0.05 ? "0" : "0.0");
        return $"{value.ToString(fmt, CultureInfo.InvariantCulture)} {units[unitIndex]}";
    }

    private static void DrawHorizontalGrid(PixelCanvas canvas, int x1, int x2, int y, Rgba color, bool dashed)
    {
        if (!dashed)
        {
            canvas.DrawHLine(x1, x2, y, color);
            return;
        }

        for (int x = x1; x <= x2; x++)
        {
            if (((x - x1) % 6) < 3)
            {
                canvas.SetPixel(x, y, color);
            }
        }
    }

    private static void DrawRightAlignedLabel(PixelCanvas canvas, string text, int rightX, int y, Rgba color)
    {
        int w = PixelCanvas.MeasureText(text, scale: 1);
        canvas.DrawText(text, Math.Max(4, rightX - w), y, color, scale: 1);
    }

    // Minimal RFC 2083 RGBA PNG encoder using BCL ZLibStream (AOT & trim safe, zero dependencies)
    private static byte[] EncodePng(byte[] rgba, int width, int height)
    {
        using var output = new MemoryStream(width * height / 6);

        // PNG signature
        ReadOnlySpan<byte> pngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
        output.Write(pngSignature);

        // IHDR chunk
        Span<byte> ihdr = stackalloc byte[13];
        WriteBigEndian(ihdr, 0, (uint)width);
        WriteBigEndian(ihdr, 4, (uint)height);
        ihdr[8] = 8; // Bit depth
        ihdr[9] = 6; // Color type: RGBA
        ihdr[10] = 0; // Compression
        ihdr[11] = 0; // Filter
        ihdr[12] = 0; // Interlace
        WriteChunk(output, "IHDR"u8, ihdr);

        // IDAT payload: filter type 1 (Sub) per scanline improves chart PNG compression dramatically
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

    private sealed class PixelCanvas
    {
        public int Width { get; }

        public int Height { get; }

        public byte[] RgbaBytes { get; }

        public PixelCanvas(int width, int height)
        {
            Width = width;
            Height = height;
            RgbaBytes = new byte[width * height * 4];
        }

        public void SetPixel(int x, int y, Rgba color)
        {
            if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
            {
                return;
            }

            int idx = ((y * Width) + x) * 4;
            if (color.A == 255)
            {
                RgbaBytes[idx] = color.R;
                RgbaBytes[idx + 1] = color.G;
                RgbaBytes[idx + 2] = color.B;
                RgbaBytes[idx + 3] = 255;
            }
            else if (color.A > 0)
            {
                int a = color.A;
                int invA = 255 - a;
                RgbaBytes[idx] = (byte)(((color.R * a) + (RgbaBytes[idx] * invA)) / 255);
                RgbaBytes[idx + 1] = (byte)(((color.G * a) + (RgbaBytes[idx + 1] * invA)) / 255);
                RgbaBytes[idx + 2] = (byte)(((color.B * a) + (RgbaBytes[idx + 2] * invA)) / 255);
                RgbaBytes[idx + 3] = (byte)Math.Min(255, RgbaBytes[idx + 3] + a);
            }
        }

        public void DrawHLine(int x1, int x2, int y, Rgba color)
        {
            for (int x = x1; x <= x2; x++)
            {
                SetPixel(x, y, color);
            }
        }

        public void FillRect(int x, int y, int w, int h, Rgba color)
        {
            int xEnd = Math.Min(Width, x + w);
            int yEnd = Math.Min(Height, y + h);
            int xStart = Math.Max(0, x);
            int yStart = Math.Max(0, y);

            for (int py = yStart; py < yEnd; py++)
            {
                for (int px = xStart; px < xEnd; px++)
                {
                    SetPixel(px, py, color);
                }
            }
        }

        public void FillRoundedRect(int x, int y, int w, int h, int radius, Rgba color)
        {
            int r = Math.Min(radius, Math.Min(w, h) / 2);
            double rSq = r * r;

            for (int py = 0; py < h; py++)
            {
                for (int px = 0; px < w; px++)
                {
                    if (IsInsideRoundedCorner(px, py, w, h, r, rSq, topOnly: false))
                    {
                        SetPixel(x + px, y + py, color);
                    }
                }
            }
        }

        public void FillTopRoundedRect(int x, int y, int w, int h, int radius, Rgba color)
        {
            int r = Math.Min(radius, Math.Min(w / 2, h));
            double rSq = r * r;

            for (int py = 0; py < h; py++)
            {
                for (int px = 0; px < w; px++)
                {
                    if (IsInsideRoundedCorner(px, py, w, h, r, rSq, topOnly: true))
                    {
                        SetPixel(x + px, y + py, color);
                    }
                }
            }
        }

        public void DrawRoundedRectBorder(int x, int y, int w, int h, int radius, Rgba color)
        {
            int r = Math.Min(radius, Math.Min(w, h) / 2);
            double rOuter = r * r;
            double rInner = Math.Max(0, r - 1) * Math.Max(0, r - 1);

            for (int py = 0; py < h; py++)
            {
                for (int px = 0; px < w; px++)
                {
                    bool onEdge = px == 0 || py == 0 || px == w - 1 || py == h - 1;
                    bool inCorner = (px < r || px >= w - r) && (py < r || py >= h - r);

                    if (!inCorner)
                    {
                        if (onEdge)
                        {
                            SetPixel(x + px, y + py, color);
                        }
                    }
                    else
                    {
                        double dx = px < r ? (r - px - 0.5) : (px - (w - r) + 0.5);
                        double dy = py < r ? (r - py - 0.5) : (py - (h - r) + 0.5);
                        double dSq = (dx * dx) + (dy * dy);
                        if (dSq <= rOuter && dSq >= rInner)
                        {
                            SetPixel(x + px, y + py, color);
                        }
                    }
                }
            }
        }

        private static bool IsInsideRoundedCorner(int px, int py, int w, int h, int r, double rSq, bool topOnly)
        {
            if (r <= 0)
            {
                return true;
            }

            bool nearLeft = px < r;
            bool nearRight = px >= w - r;
            bool nearTop = py < r;
            bool nearBottom = !topOnly && py >= h - r;

            if ((nearLeft || nearRight) && (nearTop || nearBottom))
            {
                double dx = nearLeft ? (r - px - 0.5) : (px - (w - r) + 0.5);
                double dy = nearTop ? (r - py - 0.5) : (py - (h - r) + 0.5);
                return ((dx * dx) + (dy * dy)) <= rSq;
            }

            return true;
        }

        public static int MeasureText(string text, int scale)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0;
            }

            return (text.Length * 6 - 1) * scale;
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

        // Clean 5x7 crisp geometric bitmap glyphs (uppercase, digits, punctuation)
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
        };
    }
}

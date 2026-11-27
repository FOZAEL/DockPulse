using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace DockPulse.Services;

public static class NetworkLiveChartRenderer
{
    public const int DetailsWidth = 320;
    public const int DetailsHeight = 185;
    public const int DashboardWidth = 480;
    public const int DashboardHeight = 250;

    private readonly record struct Rgba(byte R, byte G, byte B, byte A = 255);

    private static readonly Rgba CardBg = new(28, 32, 40);
    private static readonly Rgba CardBorder = new(52, 59, 74);
    private static readonly Rgba GridLine = new(44, 50, 64);
    private static readonly Rgba AxisLine = new(68, 76, 94);
    private static readonly Rgba TextPrimary = new(241, 245, 249);
    private static readonly Rgba TextSecondary = new(156, 168, 188);
    private static readonly Rgba DownloadColor = new(56, 189, 248);
    private static readonly Rgba DownloadFill = new(56, 189, 248, 41); // ~16% alpha
    private static readonly Rgba UploadColor = new(167, 139, 250);
    private static readonly Rgba UploadFill = new(167, 139, 250, 41); // ~16% alpha

    public static string RenderLiveChartDataUri(
        NetworkLiveStats stats,
        int width = DetailsWidth,
        int height = DetailsHeight)
    {
        byte[] png = RenderLiveChartPng(stats, width, height);
        return $"data:image/png;base64,{Convert.ToBase64String(png)}";
    }

    public static byte[] RenderLiveChartPng(
        NetworkLiveStats stats,
        int width = DashboardWidth,
        int height = DashboardHeight)
    {
        var canvas = new PixelCanvas(width, height);

        canvas.FillRoundedRect(0, 0, width, height, 12, CardBg);
        canvas.DrawRoundedRectBorder(0, 0, width, height, 12, CardBorder);

        const int padL = 18;
        const int padR = 18;
        const string upperTitle = "LIVE SPEED";
        string subtitle =
            $"↓ {NetworkLiveService.FormatBps(stats.Current.DownBps)}  |  " +
            $"↑ {NetworkLiveService.FormatBps(stats.Current.UpBps)}  |  " +
            $"PEAK ↓ {NetworkLiveService.FormatBps(stats.PeakDownBps)}";

        const string upText = "UP";
        const string downText = "DOWN";
        int upW = PixelCanvas.MeasureText(upText, scale: 1);
        int downW = PixelCanvas.MeasureText(downText, scale: 1);
        const int sw = 8;
        const int swGap = 5;
        const int lgGap = 12;

        int upX = width - padR - upW;
        int upSwX = upX - swGap - sw;
        int downX = upSwX - lgGap - downW;
        int downSwX = downX - swGap - sw;

        int titleScale = (padL + PixelCanvas.MeasureText(upperTitle, scale: 2) + 12 <= downSwX) ? 2 : 1;
        int titleY = titleScale == 2 ? 14 : 16;
        int subtitleY = titleScale == 2 ? 34 : 30;
        bool legendOwnRow = titleScale == 1 && padL + PixelCanvas.MeasureText(upperTitle, scale: 1) + 12 > downSwX;
        int legendY = legendOwnRow ? 44 : 16;

        canvas.DrawText(upperTitle, padL, titleY, TextPrimary, scale: titleScale);
        canvas.DrawText(subtitle, padL, subtitleY, TextSecondary, scale: 1);

        canvas.FillRoundedRect(downSwX, legendY + 1, sw, sw, 2, DownloadColor);
        canvas.DrawText(downText, downX, legendY, TextPrimary, scale: 1);
        canvas.FillRoundedRect(upSwX, legendY + 1, sw, sw, 2, UploadColor);
        canvas.DrawText(upText, upX, legendY, TextPrimary, scale: 1);

        const int plotL = 68;
        int plotR = width - padR;
        const int plotT = 58;
        int plotB = height - 34;
        int plotW = Math.Max(40, plotR - plotL);
        int plotH = Math.Max(40, plotB - plotT);

        long maxVal = Math.Max(stats.PeakDownBps, stats.PeakUpBps);
        long yMax = NetworkLiveService.ComputeNiceBpsCeiling(maxVal);
        long yMid = yMax / 2;

        DrawDashedHLine(canvas, plotL, plotR, plotT, GridLine);
        DrawDashedHLine(canvas, plotL, plotR, plotT + (plotH / 2), GridLine);
        canvas.DrawHLine(plotL, plotR, plotB, AxisLine);

        DrawRightAlignedLabel(canvas, NetworkLiveService.FormatShortBpsAxis(yMax), plotL - 8, plotT - 4, TextSecondary);
        DrawRightAlignedLabel(canvas, NetworkLiveService.FormatShortBpsAxis(yMid), plotL - 8, plotT + (plotH / 2) - 4, TextSecondary);
        DrawRightAlignedLabel(canvas, "0", plotL - 8, plotB - 4, TextSecondary);

        if (stats.NoTraffic)
        {
            canvas.DrawLineAA(plotL, plotB - 1.0, plotR, plotB - 1.0, 1.8, DownloadColor);

            const string msg = "No traffic in the last 60 s";
            int mw = PixelCanvas.MeasureText(msg, scale: 1);
            int mx = plotL + (plotW / 2);
            int my = plotT + (plotH / 2) - 5;
            canvas.FillRoundedRect(mx - (mw / 2) - 6, my - 3, mw + 12, 15, 4, CardBg);
            canvas.DrawText(msg, mx - (mw / 2), my, TextSecondary, scale: 1);
        }
        else if (stats.Samples.Count > 0)
        {
            var samples = stats.Samples;
            int n = samples.Count;
            const int totalSlots = NetworkLiveService.MaxSamples;
            int startSlot = Math.Max(0, totalSlots - n);

            double[] xs = new double[n];
            double[] downYs = new double[n];
            double[] upYs = new double[n];

            for (int i = 0; i < n; i++)
            {
                int slot = startSlot + i;
                xs[i] = plotL + (slot / (double)(totalSlots - 1) * plotW);
                downYs[i] = plotB - (Math.Clamp(samples[i].DownBps / (double)yMax, 0.0, 1.0) * (plotH - 2));
                upYs[i] = plotB - (Math.Clamp(samples[i].UpBps / (double)yMax, 0.0, 1.0) * (plotH - 2));
            }

            DrawSeries(canvas, xs, downYs, plotB, DownloadColor, DownloadFill, lineWidth: 2.0);
            DrawSeries(canvas, xs, upYs, plotB, UploadColor, UploadFill, lineWidth: 1.6);
        }

        (double Frac, string Label, bool Active)[] xTicks =
        [
            (0.0, "60s", false),
            (0.25, "45s", false),
            (0.5, "30s", false),
            (0.75, "15s", false),
            (1.0, "now", true),
        ];

        foreach (var tick in xTicks)
        {
            double cx = plotL + (tick.Frac * plotW);
            int lw = PixelCanvas.MeasureText(tick.Label, scale: 1);
            int lx = Math.Clamp((int)Math.Round(cx - (lw / 2.0)), plotL, plotR - lw);
            canvas.DrawText(tick.Label, lx, plotB + 10, tick.Active ? TextPrimary : TextSecondary, scale: 1);
        }

        return EncodePng(canvas.RgbaBytes, width, height);
    }

    private static void DrawSeries(
        PixelCanvas canvas,
        double[] xs,
        double[] ys,
        int plotBottom,
        Rgba lineColor,
        Rgba fillColor,
        double lineWidth)
    {
        int n = xs.Length;
        if (n >= 2)
        {
            canvas.FillAreaUnderPolyline(xs, ys, plotBottom, fillColor);
            for (int i = 0; i < n - 1; i++)
            {
                canvas.DrawLineAA(xs[i], ys[i], xs[i + 1], ys[i + 1], lineWidth, lineColor);
            }
        }

        canvas.FillCircleAA(xs[n - 1], ys[n - 1], radius: 3.0, lineColor);
    }

    private static void DrawDashedHLine(PixelCanvas canvas, int x1, int x2, int y, Rgba color)
    {
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

        private void BlendPixel(int x, int y, Rgba color, double coverage)
        {
            if (coverage <= 0 || (uint)x >= (uint)Width || (uint)y >= (uint)Height)
            {
                return;
            }

            byte a = (byte)Math.Clamp(Math.Round(color.A * Math.Clamp(coverage, 0.0, 1.0)), 0, 255);
            if (a > 0)
            {
                SetPixel(x, y, color with { A = a });
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
            int xStart = Math.Max(0, x);
            int yStart = Math.Max(0, y);
            int xEnd = Math.Min(Width, x + w);
            int yEnd = Math.Min(Height, y + h);

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
                    if (IsInsideRoundedCorner(px, py, w, h, r, rSq))
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

        public void FillAreaUnderPolyline(double[] xs, double[] ys, int plotBottom, Rgba fillColor)
        {
            int n = xs.Length;
            if (n < 2)
            {
                return;
            }

            int xStart = Math.Max(0, (int)Math.Ceiling(xs[0]));
            int xEnd = Math.Min(Width - 1, (int)Math.Floor(xs[n - 1]));
            int seg = 0;

            for (int px = xStart; px <= xEnd; px++)
            {
                double xSample = px + 0.5;
                while (seg < n - 2 && xs[seg + 1] < xSample)
                {
                    seg++;
                }

                double x0 = xs[seg];
                double x1 = xs[seg + 1];
                double t = x1 > x0 ? Math.Clamp((xSample - x0) / (x1 - x0), 0.0, 1.0) : 0.0;
                double yTop = ys[seg] + (t * (ys[seg + 1] - ys[seg]));

                int firstFullY = (int)Math.Ceiling(yTop);
                int fracY = firstFullY - 1;
                double fracCov = firstFullY - yTop;
                if (fracY >= 0 && fracY < plotBottom && fracCov > 0.01)
                {
                    BlendPixel(px, fracY, fillColor, fracCov);
                }

                for (int py = Math.Max(0, firstFullY); py < plotBottom; py++)
                {
                    SetPixel(px, py, fillColor);
                }
            }
        }

        public void DrawLineAA(double x0, double y0, double x1, double y1, double width, Rgba color)
        {
            double halfW = width * 0.5;
            double pad = halfW + 1.0;

            int minX = Math.Max(0, (int)Math.Floor(Math.Min(x0, x1) - pad));
            int maxX = Math.Min(Width - 1, (int)Math.Ceiling(Math.Max(x0, x1) + pad));
            int minY = Math.Max(0, (int)Math.Floor(Math.Min(y0, y1) - pad));
            int maxY = Math.Min(Height - 1, (int)Math.Ceiling(Math.Max(y0, y1) + pad));

            double dx = x1 - x0;
            double dy = y1 - y0;
            double lenSq = (dx * dx) + (dy * dy);

            for (int py = minY; py <= maxY; py++)
            {
                double cy = py + 0.5;
                for (int px = minX; px <= maxX; px++)
                {
                    double cx = px + 0.5;
                    double t = lenSq > 1e-9
                        ? Math.Clamp((((cx - x0) * dx) + ((cy - y0) * dy)) / lenSq, 0.0, 1.0)
                        : 0.0;
                    double nx = x0 + (t * dx);
                    double ny = y0 + (t * dy);
                    double dist = Math.Sqrt(((cx - nx) * (cx - nx)) + ((cy - ny) * (cy - ny)));
                    double cov = halfW + 0.5 - dist;
                    if (cov > 0.0)
                    {
                        BlendPixel(px, py, color, cov);
                    }
                }
            }
        }

        public void FillCircleAA(double cx, double cy, double radius, Rgba color)
        {
            int minX = Math.Max(0, (int)Math.Floor(cx - radius - 1.0));
            int maxX = Math.Min(Width - 1, (int)Math.Ceiling(cx + radius + 1.0));
            int minY = Math.Max(0, (int)Math.Floor(cy - radius - 1.0));
            int maxY = Math.Min(Height - 1, (int)Math.Ceiling(cy + radius + 1.0));

            for (int py = minY; py <= maxY; py++)
            {
                double dy = (py + 0.5) - cy;
                for (int px = minX; px <= maxX; px++)
                {
                    double dx = (px + 0.5) - cx;
                    double dist = Math.Sqrt((dx * dx) + (dy * dy));
                    double cov = radius + 0.5 - dist;
                    if (cov > 0.0)
                    {
                        BlendPixel(px, py, color, cov);
                    }
                }
            }
        }

        private static bool IsInsideRoundedCorner(int px, int py, int w, int h, int r, double rSq)
        {
            if (r <= 0)
            {
                return true;
            }

            bool nearLeft = px < r;
            bool nearRight = px >= w - r;
            bool nearTop = py < r;
            bool nearBottom = py >= h - r;

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

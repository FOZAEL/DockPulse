using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace DockPulse.Services;

public static class AiUsageChartRenderer
{
    // The card is a Markdown image in the list's details pane, which shows images at their natural size and
    // clips anything wider (about 345 px at Command Palette's default size). 300 px still fits the longest
    // line ("ANTIGRAVITY CLI • CLAUDE AND GPT MODELS", ~233 px at 6 px per character) inside the padding.
    public const int DefaultWidth = 300;
    public const int DefaultHeight = 175;

    private readonly record struct Rgba(byte R, byte G, byte B, byte A = 255);

    private static readonly Rgba CardBg = new(28, 32, 40);
    private static readonly Rgba CardBorder = new(52, 59, 74);
    private static readonly Rgba TrackBg = new(36, 41, 52);
    private static readonly Rgba TextPrimary = new(241, 245, 249);
    private static readonly Rgba TextSecondary = new(156, 168, 188);
    private static readonly Rgba CyanBar = new(56, 189, 248);
    private static readonly Rgba VioletBar = new(167, 139, 250);
    private static readonly Rgba WarnBar = new(245, 158, 11);
    private static readonly Rgba DangerBar = new(244, 63, 94);
    private static readonly Rgba DangerText = new(251, 113, 133);

    public static string RenderProviderCardDataUri(
        AiProviderUsage provider,
        DateTimeOffset nowUtc,
        int width = DefaultWidth,
        int height = DefaultHeight)
    {
        byte[] png = RenderProviderCardPng(provider, nowUtc, width, height);
        return $"data:image/png;base64,{Convert.ToBase64String(png)}";
    }

    public static byte[] RenderProviderCardPng(
        AiProviderUsage provider,
        DateTimeOffset nowUtc,
        int width = DefaultWidth,
        int height = DefaultHeight)
    {
        var canvas = new CardCanvas(width, height);
        canvas.FillRoundedRect(0, 0, width, height, 12, CardBg);
        canvas.DrawRoundedRectBorder(0, 0, width, height, 12, CardBorder);

        const int padL = 18;
        const int padR = 18;
        int barW = width - padL - padR;

        canvas.DrawText($"{provider.Name.ToUpperInvariant()} USAGE", padL, 14, TextPrimary, scale: 2);
        canvas.DrawText(provider.PlanSubtitle.ToUpperInvariant(), padL, 34, TextSecondary, scale: 1);

        if (!provider.HasData)
        {
            canvas.FillRoundedRect(padL, 68, barW, 12, 6, TrackBg);
            canvas.FillRoundedRect(padL, 122, barW, 12, 6, TrackBg);
            string msg = provider.UnavailableCardText.ToUpperInvariant();
            int msgW = CardCanvas.MeasureText(msg, scale: 1);
            canvas.DrawText(msg, Math.Max(padL, (width - msgW) / 2), 95, TextSecondary, scale: 1);
            return EncodePng(canvas.RgbaBytes, width, height);
        }

        DrawWindowRow(canvas, provider.FiveHour, "5-HOUR WINDOW", CyanBar, rowY: 60, padL, width - padR, barW, nowUtc);
        DrawWindowRow(canvas, provider.Weekly, "WEEKLY LIMIT", VioletBar, rowY: 115, padL, width - padR, barW, nowUtc);

        return EncodePng(canvas.RgbaBytes, width, height);
    }

    private static void DrawWindowRow(
        CardCanvas canvas,
        AiLimitWindow? window,
        string fallbackLabel,
        Rgba defaultColor,
        int rowY,
        int padL,
        int rightEdge,
        int barW,
        DateTimeOffset nowUtc)
    {
        if (window is null)
        {
            canvas.DrawText(fallbackLabel, padL, rowY, TextPrimary, scale: 1);
            const string noData = "NO DATA";
            int ndW = CardCanvas.MeasureText(noData, scale: 1);
            canvas.DrawText(noData, rightEdge - ndW, rowY, TextSecondary, scale: 1);
            canvas.FillRoundedRect(padL, rowY + 15, barW, 11, 5, TrackBg);
            canvas.DrawText("WINDOW NOT REPORTED", padL, rowY + 31, TextSecondary, scale: 1);
            return;
        }

        int pct = window.RemainingPercent;
        Rgba fillCol = pct == 0 ? DangerBar : pct <= 25 ? WarnBar : defaultColor;
        Rgba pctCol = pct == 0 ? DangerText : TextPrimary;

        canvas.DrawText(window.Label.ToUpperInvariant(), padL, rowY, TextPrimary, scale: 1);
        string pctLabel = $"{pct}% REMAINING";
        int pctW = CardCanvas.MeasureText(pctLabel, scale: 1);
        canvas.DrawText(pctLabel, rightEdge - pctW, rowY, pctCol, scale: 1);

        int barY = rowY + 15;
        const int barH = 11;
        canvas.FillRoundedRect(padL, barY, barW, barH, 5, TrackBg);
        if (pct > 0)
        {
            int fw = Math.Max(barH, (int)Math.Round(barW * Math.Min(100, pct) / 100.0));
            canvas.FillRoundedRect(padL, barY, fw, barH, 5, fillCol);
        }

        string resetShort = window.FormatResetShort(nowUtc);
        string resetLine = string.Equals(resetShort, "Reset", StringComparison.Ordinal)
            ? "WINDOW RESET"
            : $"RESETS IN {resetShort.ToUpperInvariant()}";
        canvas.DrawText(resetLine, padL, barY + 16, TextSecondary, scale: 1);

        if (pct == 0)
        {
            const string hit = "LIMIT REACHED";
            int hitW = CardCanvas.MeasureText(hit, scale: 1);
            canvas.DrawText(hit, rightEdge - hitW, barY + 16, DangerText, scale: 1);
        }
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

    private sealed class CardCanvas
    {
        public int Width { get; }

        public int Height { get; }

        public byte[] RgbaBytes { get; }

        public CardCanvas(int width, int height)
        {
            Width = width;
            Height = height;
            RgbaBytes = new byte[width * height * 4];
        }

        private void SetPixel(int x, int y, Rgba c)
        {
            if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
            {
                return;
            }

            int idx = ((y * Width) + x) * 4;
            RgbaBytes[idx] = c.R;
            RgbaBytes[idx + 1] = c.G;
            RgbaBytes[idx + 2] = c.B;
            RgbaBytes[idx + 3] = c.A;
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
            if (w <= 0 || h <= 0)
            {
                return;
            }

            int r = Math.Min(radius, Math.Min(w / 2, h / 2));
            for (int py = 0; py < h; py++)
            {
                for (int px = 0; px < w; px++)
                {
                    if (IsInsideRoundedRect(px, py, w, h, r))
                    {
                        SetPixel(x + px, y + py, color);
                    }
                }
            }
        }

        public void DrawRoundedRectBorder(int x, int y, int w, int h, int radius, Rgba color)
        {
            int r = Math.Min(radius, Math.Min(w / 2, h / 2));
            for (int py = 0; py < h; py++)
            {
                for (int px = 0; px < w; px++)
                {
                    if (!IsInsideRoundedRect(px, py, w, h, r))
                    {
                        continue;
                    }

                    bool inner = px > 0 && px < w - 1 && py > 0 && py < h - 1 &&
                                 IsInsideRoundedRect(px - 1, py - 1, w - 2, h - 2, Math.Max(0, r - 1));
                    if (!inner)
                    {
                        SetPixel(x + px, y + py, color);
                    }
                }
            }
        }

        private static bool IsInsideRoundedRect(int px, int py, int w, int h, int r)
        {
            if (r <= 0)
            {
                return true;
            }

            int cx = px < r ? r : (px >= w - r ? w - r - 1 : px);
            int cy = py < r ? r : (py >= h - r ? h - r - 1 : py);
            int dx = px - cx;
            int dy = py - cy;
            return (dx * dx) + (dy * dy) <= r * r;
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
            ['\''] = [0x04, 0x04, 0x08, 0x00, 0x00, 0x00, 0x00],
            [':'] = [0x00, 0x04, 0x04, 0x00, 0x04, 0x04, 0x00],
            ['-'] = [0x00, 0x00, 0x00, 0x1F, 0x00, 0x00, 0x00],
            ['/'] = [0x01, 0x01, 0x02, 0x04, 0x08, 0x10, 0x10],
            ['('] = [0x02, 0x04, 0x08, 0x08, 0x08, 0x04, 0x02],
            [')'] = [0x08, 0x04, 0x02, 0x02, 0x02, 0x04, 0x08],
            ['%'] = [0x18, 0x19, 0x02, 0x04, 0x08, 0x13, 0x03],
            ['&'] = [0x0C, 0x12, 0x14, 0x08, 0x15, 0x12, 0x0D],
            ['+'] = [0x00, 0x04, 0x04, 0x1F, 0x04, 0x04, 0x00],
            ['|'] = [0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04],
            ['_'] = [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x1F],
            ['•'] = [0x00, 0x00, 0x06, 0x06, 0x00, 0x00, 0x00],
        };
    }
}

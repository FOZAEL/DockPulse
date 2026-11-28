using System;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace DockPulse.Services;

/// <summary>
/// RGBA drawing surface for the PNG cards (Calendar, Football). Shapes use anti-aliased signed distances,
/// text uses Windows' Segoe UI through GDI, drawn at 2x by default for sharp results.
/// Coordinates passed in are 1x units.
/// </summary>
internal sealed class GdiCanvas
{
    /// <summary>Pixels per 1x unit: 2 for sharp images shown with a max size, 1 for Markdown images,
    /// which Command Palette shows at their real pixel size.</summary>
    private readonly int Scale;

    private const string FontFamily = "Segoe UI";

    private readonly int _w;
    private readonly int _h;
    private readonly byte[] _rgba;
    private readonly float[] _cardMask;

    public GdiCanvas(int width, int height, int scale = 2)
    {
        Scale = Math.Clamp(scale, 1, 4);
        _w = width * Scale;
        _h = height * Scale;
        _rgba = new byte[_w * _h * 4];
        _cardMask = new float[_w * _h];
    }

    // ---- Shapes (coordinates in 1x units) ----

    public void FillRoundRectGradient(float x, float y, float w, float h, float r, (byte R, byte G, byte B) top, (byte R, byte G, byte B) bottom)
    {
        for (int py = 0; py < _h; py++)
        {
            float t = py / (float)(_h - 1);
            byte cr = Lerp(top.R, bottom.R, t), cg = Lerp(top.G, bottom.G, t), cb = Lerp(top.B, bottom.B, t);
            for (int px = 0; px < _w; px++)
            {
                float cov = RoundRectCoverage(px + 0.5f, py + 0.5f, x * Scale, y * Scale, w * Scale, h * Scale, r * Scale);
                if (cov <= 0)
                {
                    continue;
                }

                int i = ((py * _w) + px) * 4;
                _rgba[i] = cr;
                _rgba[i + 1] = cg;
                _rgba[i + 2] = cb;
                _rgba[i + 3] = (byte)(cov * 255);
                _cardMask[(py * _w) + px] = cov;
            }
        }
    }

    public void FillRoundRect(float x, float y, float w, float h, float r, byte cr, byte cg, byte cb, float a)
    {
        int x0 = Math.Max(0, (int)(x * Scale) - 1), x1 = Math.Min(_w, (int)((x + w) * Scale) + 2);
        int y0 = Math.Max(0, (int)(y * Scale) - 1), y1 = Math.Min(_h, (int)((y + h) * Scale) + 2);
        for (int py = y0; py < y1; py++)
        {
            for (int px = x0; px < x1; px++)
            {
                float cov = RoundRectCoverage(px + 0.5f, py + 0.5f, x * Scale, y * Scale, w * Scale, h * Scale, r * Scale);
                Blend(px, py, cr, cg, cb, cov * a);
            }
        }
    }

    public void FillCircle(float cx, float cy, float r, byte cr, byte cg, byte cb, float a) =>
        FillRoundRect(cx - r, cy - r, r * 2, r * 2, r, cr, cg, cb, a);

    public void FillRect(float x, float y, float w, float h, byte cr, byte cg, byte cb, float a) =>
        FillRoundRect(x, y, w, h, 0, cr, cg, cb, a);

    // ---- Text (baseline at y, 1x units) ----

    public int MeasureText(string text, int sizePx, int weight) =>
        (int)Math.Ceiling(GdiText.Measure(text, sizePx * Scale, weight, FontFamily) / (float)Scale);

    public void DrawTextCentered(string text, float cx, int baselineY, int sizePx, int weight, byte cr, byte cg, byte cb, float a)
    {
        int w = MeasureText(text, sizePx, weight);
        DrawText(text, (int)Math.Round(cx - (w / 2f)), baselineY, sizePx, weight, cr, cg, cb, a);
    }

    public void DrawText(string text, int x, int baselineY, int sizePx, int weight, byte cr, byte cg, byte cb, float a)
    {
        var mask = GdiText.Render(text, sizePx * Scale, weight, FontFamily);
        if (mask is null)
        {
            return;
        }

        int ox = (x * Scale) - (sizePx * Scale / 8); // GdiText.Render draws with a small left margin
        int oy = (baselineY * Scale) - mask.Baseline;
        for (int my = 0; my < mask.Height; my++)
        {
            int py = oy + my;
            if (py < 0 || py >= _h)
            {
                continue;
            }

            for (int mx = 0; mx < mask.Width; mx++)
            {
                int px = ox + mx;
                if (px < 0 || px >= _w)
                {
                    continue;
                }

                byte cov = mask.Coverage[(my * mask.Width) + mx];
                if (cov != 0)
                {
                    Blend(px, py, cr, cg, cb, cov / 255f * a);
                }
            }
        }
    }

    private void Blend(int px, int py, byte cr, byte cg, byte cb, float a)
    {
        if (a <= 0 || px < 0 || py < 0 || px >= _w || py >= _h)
        {
            return;
        }

        // Everything sits on the card: clip to its rounded shape
        a *= _cardMask[(py * _w) + px];
        if (a <= 0)
        {
            return;
        }

        int i = ((py * _w) + px) * 4;
        _rgba[i] = (byte)((cr * a) + (_rgba[i] * (1 - a)));
        _rgba[i + 1] = (byte)((cg * a) + (_rgba[i + 1] * (1 - a)));
        _rgba[i + 2] = (byte)((cb * a) + (_rgba[i + 2] * (1 - a)));
    }

    private static float RoundRectCoverage(float px, float py, float x, float y, float w, float h, float r)
    {
        float hw = w / 2, hh = h / 2;
        float qx = MathF.Abs(px - (x + hw)) - (hw - r);
        float qy = MathF.Abs(py - (y + hh)) - (hh - r);
        float outside = MathF.Sqrt((MathF.Max(qx, 0) * MathF.Max(qx, 0)) + (MathF.Max(qy, 0) * MathF.Max(qy, 0)));
        float dist = outside + MathF.Min(MathF.Max(qx, qy), 0) - r;
        return Math.Clamp(0.5f - dist, 0, 1);
    }

    private static byte Lerp(byte a, byte b, float t) => (byte)(a + ((b - a) * t));

    public byte[] EncodePng()
    {
        using var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        byte[] ihdr = new byte[13];
        WriteBigEndian(ihdr, 0, (uint)_w);
        WriteBigEndian(ihdr, 4, (uint)_h);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 6;  // RGBA
        WriteChunk(output, "IHDR"u8, ihdr);

        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
        {
            int stride = _w * 4;
            for (int y = 0; y < _h; y++)
            {
                z.WriteByte(0);
                z.Write(_rgba, y * stride, stride);
            }
        }

        WriteChunk(output, "IDAT"u8, raw.ToArray());
        WriteChunk(output, "IEND"u8, []);
        return output.ToArray();
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, byte[] data)
    {
        byte[] len = new byte[4];
        WriteBigEndian(len, 0, (uint)data.Length);
        output.Write(len);
        output.Write(type);
        output.Write(data);

        uint crc = 0xFFFFFFFF;
        foreach (byte b in type)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        foreach (byte b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        byte[] crcBytes = new byte[4];
        WriteBigEndian(crcBytes, 0, crc ^ 0xFFFFFFFF);
        output.Write(crcBytes);
    }

    private static void WriteBigEndian(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }

    /// <summary>Solid rounded card background; everything drawn later is clipped to it.</summary>
    public void FillCard(float r, byte cr, byte cg, byte cb) =>
        FillRoundRectGradient(0, 0, _w / (float)Scale, _h / (float)Scale, r, (cr, cg, cb), (cr, cg, cb));

    public void DrawTextRight(string text, int rightX, int baselineY, int sizePx, int weight, byte cr, byte cg, byte cb, float a)
    {
        int w = MeasureText(text, sizePx, weight);
        DrawText(text, rightX - w, baselineY, sizePx, weight, cr, cg, cb, a);
    }

    /// <summary>Draws an RGBA image into a square at (x, y), size in 1x units (bilinear, straight alpha).</summary>
    public void DrawImage(byte[] rgba, int srcW, int srcH, float x, float y, float size)
    {
        int dx0 = (int)(x * Scale), dy0 = (int)(y * Scale), ds = (int)(size * Scale);
        float scale = Math.Max(srcW, srcH) / (float)ds;
        float offX = (ds - (srcW / scale)) / 2, offY = (ds - (srcH / scale)) / 2;
        for (int py = 0; py < ds; py++)
        {
            for (int px = 0; px < ds; px++)
            {
                float sx = ((px - offX) + 0.5f) * scale - 0.5f, sy = ((py - offY) + 0.5f) * scale - 0.5f;
                if (sx < -0.5f || sy < -0.5f || sx > srcW - 0.5f || sy > srcH - 0.5f)
                {
                    continue;
                }

                int x0 = Math.Clamp((int)MathF.Floor(sx), 0, srcW - 1), y0 = Math.Clamp((int)MathF.Floor(sy), 0, srcH - 1);
                int x1 = Math.Min(x0 + 1, srcW - 1), y1 = Math.Min(y0 + 1, srcH - 1);
                float fx = Math.Clamp(sx - x0, 0, 1), fy = Math.Clamp(sy - y0, 0, 1);
                float r = 0, g = 0, b = 0, a = 0;
                Accumulate(x0, y0, (1 - fx) * (1 - fy));
                Accumulate(x1, y0, fx * (1 - fy));
                Accumulate(x0, y1, (1 - fx) * fy);
                Accumulate(x1, y1, fx * fy);
                if (a > 0.003f)
                {
                    Blend(dx0 + px, dy0 + py, (byte)(r / a), (byte)(g / a), (byte)(b / a), Math.Min(1, a));
                }

                void Accumulate(int ix, int iy, float wgt)
                {
                    int i = ((iy * srcW) + ix) * 4;
                    float al = rgba[i + 3] / 255f * wgt;
                    r += rgba[i] * al;
                    g += rgba[i + 1] * al;
                    b += rgba[i + 2] * al;
                    a += al;
                }
            }
        }
    }
}

/// <summary>Rasterizes one line of text with GDI (grayscale anti-aliasing) into a coverage mask.</summary>
internal static class GdiText
{
    public sealed record Mask(int Width, int Height, int Baseline, byte[] Coverage);

    private const int FW_NORMAL = 400;
    private const uint DEFAULT_CHARSET = 1;
    private const uint ANTIALIASED_QUALITY = 4;
    private const uint TA_BASELINE = 24;
    private const int OPAQUE = 2;

    public static int Measure(string text, int sizePx, int weight, string family)
    {
        IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
        if (dc == IntPtr.Zero)
        {
            return text.Length * sizePx / 2;
        }

        IntPtr font = CreateFont(sizePx, weight, family);
        IntPtr old = SelectObject(dc, font);
        try
        {
            return GetTextExtentPoint32W(dc, text, text.Length, out var size) ? size.Cx : text.Length * sizePx / 2;
        }
        finally
        {
            SelectObject(dc, old);
            DeleteObject(font);
            DeleteDC(dc);
        }
    }

    public static Mask? Render(string text, int sizePx, int weight, string family)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        int width = Measure(text, sizePx, weight, family) + (sizePx / 2);
        int height = sizePx * 2;
        int baseline = (int)(sizePx * 1.4);

        IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
        if (dc == IntPtr.Zero)
        {
            return null;
        }

        var bmi = new BITMAPINFOHEADER
        {
            Size = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            Width = width,
            Height = -height, // top-down
            Planes = 1,
            BitCount = 32,
        };

        IntPtr bitmap = CreateDIBSection(dc, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
        if (bitmap == IntPtr.Zero)
        {
            DeleteDC(dc);
            return null;
        }

        IntPtr oldBitmap = SelectObject(dc, bitmap);
        IntPtr font = CreateFont(sizePx, weight, family);
        IntPtr oldFont = SelectObject(dc, font);
        try
        {
            SetBkMode(dc, OPAQUE);
            SetBkColor(dc, 0x000000);
            SetTextColor(dc, 0xFFFFFF);
            SetTextAlign(dc, TA_BASELINE);
            TextOutW(dc, sizePx / 8, baseline, text, text.Length);
            GdiFlush();

            byte[] pixels = new byte[width * height * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            byte[] coverage = new byte[width * height];
            for (int i = 0; i < coverage.Length; i++)
            {
                // White text on black: any channel is the coverage (grayscale anti-aliasing)
                coverage[i] = pixels[(i * 4) + 1];
            }

            return new Mask(width, height, baseline, coverage);
        }
        finally
        {
            SelectObject(dc, oldFont);
            DeleteObject(font);
            SelectObject(dc, oldBitmap);
            DeleteObject(bitmap);
            DeleteDC(dc);
        }
    }

    private static IntPtr CreateFont(int sizePx, int weight, string family) =>
        CreateFontW(-sizePx, 0, 0, 0, weight <= 0 ? FW_NORMAL : weight, 0, 0, 0, DEFAULT_CHARSET, 0, 0, ANTIALIASED_QUALITY, 0, family);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int Cx;
        public int Cy;
    }

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr ho);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFontW(int cHeight, int cWidth, int cEscapement, int cOrientation, int cWeight, uint bItalic, uint bUnderline, uint bStrikeOut, uint iCharSet, uint iOutPrecision, uint iClipPrecision, uint iQuality, uint iPitchAndFamily, string pszFaceName);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern bool TextOutW(IntPtr hdc, int x, int y, string lpString, int c);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetTextExtentPoint32W(IntPtr hdc, string lpString, int c, out SIZE psizl);

    [DllImport("gdi32.dll")]
    private static extern int SetBkMode(IntPtr hdc, int mode);

    [DllImport("gdi32.dll")]
    private static extern uint SetBkColor(IntPtr hdc, uint color);

    [DllImport("gdi32.dll")]
    private static extern uint SetTextColor(IntPtr hdc, uint color);

    [DllImport("gdi32.dll")]
    private static extern uint SetTextAlign(IntPtr hdc, uint align);

    [DllImport("gdi32.dll")]
    private static extern bool GdiFlush();
}

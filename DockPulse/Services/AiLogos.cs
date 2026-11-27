using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Windows.ApplicationModel;

namespace DockPulse.Services;

/// <summary>
/// Provides theme-aware Command Palette icons and downscaled RGBA bitmaps for AI provider logos.
/// </summary>
public static class AiLogos
{
    private const string ClaudePath = "Assets\\AiLogos\\claude.png";
    private const string AntigravityPath = "Assets\\AiLogos\\antigravity.png";
    private const string CodexLightPath = "Assets\\AiLogos\\codex-light.png";
    private const string CodexDarkPath = "Assets\\AiLogos\\codex-dark.png";
    private const string CursorLightPath = "Assets\\AiLogos\\cursor-light.png";
    private const string CursorDarkPath = "Assets\\AiLogos\\cursor-dark.png";

    private const int MaxPngDimension = 4096;

    public readonly record struct LogoBitmap(byte[] Rgba, int Width, int Height);

    private static readonly ConcurrentDictionary<(string File, int Size), LogoBitmap?> ChartCache = new();
    private static readonly ConcurrentDictionary<string, LogoBitmap?> DecodedCache = new(StringComparer.OrdinalIgnoreCase);

    public static IconInfo GetIcon(string? providerId) =>
        (providerId ?? string.Empty).ToLowerInvariant() switch
        {
            "claude" => IconHelpers.FromRelativePath(ClaudePath),
            "antigravity" => IconHelpers.FromRelativePath(AntigravityPath),
            "codex" => IconHelpers.FromRelativePaths(CodexLightPath, CodexDarkPath),
            "cursor" => IconHelpers.FromRelativePaths(CursorLightPath, CursorDarkPath),
            _ => new IconInfo("\uE99A"),
        };

    /// <summary>
    /// Returns a dark-theme logo resized to <paramref name="size"/>x<paramref name="size"/> RGBA for chart cards.
    /// Never throws; returns false if the logo cannot be loaded or decoded.
    /// </summary>
    public static bool TryGetChartLogo(string? providerId, int size, out LogoBitmap bitmap)
    {
        bitmap = default;
        if (size <= 0)
        {
            return false;
        }

        string? relativePath = (providerId ?? string.Empty).ToLowerInvariant() switch
        {
            "claude" => ClaudePath,
            "antigravity" => AntigravityPath,
            "codex" => CodexDarkPath,
            "cursor" => CursorDarkPath,
            _ => null,
        };

        if (relativePath is null)
        {
            return false;
        }

        try
        {
            var cached = ChartCache.GetOrAdd((relativePath, size), static key => LoadAndScale(key.File, key.Size));
            if (cached is { } found)
            {
                bitmap = found;
                return true;
            }
        }
        catch
        {
            // Charts must never fail because of a missing or unreadable logo
        }

        return false;
    }

    private static LogoBitmap? LoadAndScale(string relativePath, int targetSize)
    {
        try
        {
            var decoded = DecodedCache.GetOrAdd(relativePath, static path => LoadAndDecode(path));
            if (decoded is not { } src)
            {
                return null;
            }

            if (src.Width == targetSize && src.Height == targetSize)
            {
                return src;
            }

            byte[] scaled = DownscaleAreaAveragePremultiplied(src.Rgba, src.Width, src.Height, targetSize, targetSize);
            return new LogoBitmap(scaled, targetSize, targetSize);
        }
        catch
        {
            return null;
        }
    }

    private static LogoBitmap? LoadAndDecode(string relativePath)
    {
        string? fullPath = ResolveAssetPath(relativePath);
        if (fullPath is null)
        {
            return null;
        }

        byte[] bytes = File.ReadAllBytes(fullPath);
        return TryDecodePng(bytes, out var rgba, out int width, out int height)
            ? new LogoBitmap(rgba, width, height)
            : null;
    }

    private static string? ResolveAssetPath(string relativePath)
    {
        try
        {
            string baseDir = AppContext.BaseDirectory;
            if (!string.IsNullOrEmpty(baseDir))
            {
                string candidate = Path.Combine(baseDir, relativePath);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }
        catch
        {
            // Fall through to package installed location
        }

        try
        {
            string installed = Package.Current.InstalledLocation.Path;
            if (!string.IsNullOrEmpty(installed))
            {
                string candidate = Path.Combine(installed, relativePath);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }
        catch
        {
            // Running unpackaged or Package.Current unavailable
        }

        return null;
    }

    // Minimal synchronous PNG decoder: bit depth 8, color type 6 (RGBA) or 2 (RGB), non-interlaced, all 5 filter types
    internal static bool TryDecodePng(byte[] data, out byte[] rgba, out int width, out int height)
    {
        rgba = [];
        width = 0;
        height = 0;

        if (data.Length < 33)
        {
            return false;
        }

        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (!data.AsSpan(0, 8).SequenceEqual(signature))
        {
            return false;
        }

        bool sawIhdr = false;
        byte colorType = 0;
        using var idat = new MemoryStream();

        int pos = 8;
        while (pos + 12 <= data.Length)
        {
            uint length = ReadBigEndianUInt32(data, pos);
            if (length > int.MaxValue || pos + 12L + length > data.Length)
            {
                return false;
            }

            int dataOffset = pos + 8;
            ReadOnlySpan<byte> chunkType = data.AsSpan(pos + 4, 4);

            if (chunkType.SequenceEqual("IHDR"u8))
            {
                if (sawIhdr || length != 13)
                {
                    return false;
                }

                uint w = ReadBigEndianUInt32(data, dataOffset);
                uint h = ReadBigEndianUInt32(data, dataOffset + 4);
                byte bitDepth = data[dataOffset + 8];
                colorType = data[dataOffset + 9];
                byte compression = data[dataOffset + 10];
                byte filter = data[dataOffset + 11];
                byte interlace = data[dataOffset + 12];

                if (w == 0 || h == 0 || w > MaxPngDimension || h > MaxPngDimension)
                {
                    return false;
                }

                if (bitDepth != 8 || (colorType != 6 && colorType != 2) || compression != 0 || filter != 0 || interlace != 0)
                {
                    return false;
                }

                width = (int)w;
                height = (int)h;
                sawIhdr = true;
            }
            else if (chunkType.SequenceEqual("IDAT"u8))
            {
                if (!sawIhdr)
                {
                    return false;
                }

                idat.Write(data, dataOffset, (int)length);
            }
            else if (chunkType.SequenceEqual("IEND"u8))
            {
                break;
            }

            pos = dataOffset + (int)length + 4; // skip chunk data + CRC
        }

        if (!sawIhdr || idat.Length == 0)
        {
            return false;
        }

        int bpp = colorType == 6 ? 4 : 3;
        int stride = width * bpp;
        int expectedRaw = height * (stride + 1);
        byte[] raw = new byte[expectedRaw];

        idat.Position = 0;
        using (var zlib = new ZLibStream(idat, CompressionMode.Decompress, leaveOpen: true))
        {
            int totalRead = 0;
            while (totalRead < expectedRaw)
            {
                int n = zlib.Read(raw, totalRead, expectedRaw - totalRead);
                if (n <= 0)
                {
                    return false;
                }

                totalRead += n;
            }
        }

        byte[] unfiltered = new byte[height * stride];
        for (int y = 0; y < height; y++)
        {
            int srcRow = y * (stride + 1);
            byte filterType = raw[srcRow];
            int dstRow = y * stride;
            int prevRow = dstRow - stride;

            for (int x = 0; x < stride; x++)
            {
                byte rawByte = raw[srcRow + 1 + x];
                byte a = x >= bpp ? unfiltered[dstRow + x - bpp] : (byte)0;
                byte b = y > 0 ? unfiltered[prevRow + x] : (byte)0;
                byte c = (x >= bpp && y > 0) ? unfiltered[prevRow + x - bpp] : (byte)0;

                byte recon = filterType switch
                {
                    0 => rawByte,
                    1 => unchecked((byte)(rawByte + a)),
                    2 => unchecked((byte)(rawByte + b)),
                    3 => unchecked((byte)(rawByte + ((a + b) >> 1))),
                    4 => unchecked((byte)(rawByte + PaethPredictor(a, b, c))),
                    _ => 0,
                };

                if (filterType > 4)
                {
                    return false;
                }

                unfiltered[dstRow + x] = recon;
            }
        }

        if (colorType == 6)
        {
            rgba = unfiltered;
            return true;
        }

        rgba = new byte[width * height * 4];
        int pixels = width * height;
        for (int i = 0; i < pixels; i++)
        {
            int s = i * 3;
            int d = i * 4;
            rgba[d] = unfiltered[s];
            rgba[d + 1] = unfiltered[s + 1];
            rgba[d + 2] = unfiltered[s + 2];
            rgba[d + 3] = 255;
        }

        return true;
    }

    private static byte PaethPredictor(byte a, byte b, byte c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc)
        {
            return a;
        }

        return pb <= pc ? b : c;
    }

    private static uint ReadBigEndianUInt32(byte[] data, int offset) =>
        ((uint)data[offset] << 24) |
        ((uint)data[offset + 1] << 16) |
        ((uint)data[offset + 2] << 8) |
        data[offset + 3];

    private static byte[] DownscaleAreaAveragePremultiplied(byte[] src, int srcW, int srcH, int dstW, int dstH)
    {
        byte[] dst = new byte[dstW * dstH * 4];
        double scaleX = (double)srcW / dstW;
        double scaleY = (double)srcH / dstH;

        for (int dy = 0; dy < dstH; dy++)
        {
            double sy0 = dy * scaleY;
            double sy1 = (dy + 1) * scaleY;
            int iy0 = Math.Clamp((int)Math.Floor(sy0), 0, srcH - 1);
            int iy1 = Math.Clamp((int)Math.Ceiling(sy1) - 1, 0, srcH - 1);

            for (int dx = 0; dx < dstW; dx++)
            {
                double sx0 = dx * scaleX;
                double sx1 = (dx + 1) * scaleX;
                int ix0 = Math.Clamp((int)Math.Floor(sx0), 0, srcW - 1);
                int ix1 = Math.Clamp((int)Math.Ceiling(sx1) - 1, 0, srcW - 1);

                double sumR = 0;
                double sumG = 0;
                double sumB = 0;
                double sumA = 0;
                double totalW = 0;

                for (int sy = iy0; sy <= iy1; sy++)
                {
                    double wy = Math.Min(sy + 1.0, sy1) - Math.Max((double)sy, sy0);
                    if (wy <= 0)
                    {
                        continue;
                    }

                    int rowOffset = sy * srcW * 4;
                    for (int sx = ix0; sx <= ix1; sx++)
                    {
                        double wx = Math.Min(sx + 1.0, sx1) - Math.Max((double)sx, sx0);
                        if (wx <= 0)
                        {
                            continue;
                        }

                        double w = wx * wy;
                        totalW += w;

                        int sIdx = rowOffset + (sx * 4);
                        double a = src[sIdx + 3] / 255.0;
                        double wa = w * a;
                        sumR += src[sIdx] * wa;
                        sumG += src[sIdx + 1] * wa;
                        sumB += src[sIdx + 2] * wa;
                        sumA += wa;
                    }
                }

                int dIdx = ((dy * dstW) + dx) * 4;
                if (totalW > 0 && sumA > 1e-6)
                {
                    double outA = Math.Clamp(sumA / totalW, 0.0, 1.0);
                    dst[dIdx] = (byte)Math.Clamp(Math.Round(sumR / sumA), 0, 255);
                    dst[dIdx + 1] = (byte)Math.Clamp(Math.Round(sumG / sumA), 0, 255);
                    dst[dIdx + 2] = (byte)Math.Clamp(Math.Round(sumB / sumA), 0, 255);
                    dst[dIdx + 3] = (byte)Math.Clamp(Math.Round(outA * 255.0), 0, 255);
                }
            }
        }

        return dst;
    }
}

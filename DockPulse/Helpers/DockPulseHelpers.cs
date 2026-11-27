using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Windows.Storage.Streams;

namespace DockPulse.Helpers;

internal enum TagTone
{
    Default,
    Accent,
    Ok,
    Warn,
    Danger,
}

internal static class DockPulseHelpers
{
    public static string GetSettingsFilePath(string fileName)
    {
        try
        {
            string dir = Utilities.BaseSettingsPath("DockPulse");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, fileName);
        }
        catch
        {
            string fallback = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DockPulse");
            Directory.CreateDirectory(fallback);
            return Path.Combine(fallback, fileName);
        }
    }

    public static void ShowInlineToast(string message)
    {
        try
        {
            new ToastStatusMessage(message).Show();
        }
        catch
        {
            // Ignore toast errors if host is unavailable
        }
    }

    public static Tag CreateTag(string text, TagTone tone = TagTone.Default, IconInfo? icon = null)
    {
        Tag tag = tone switch
        {
            TagTone.Accent => new Tag(text)
            {
                Foreground = ColorHelpers.FromRgb(56, 189, 248),
                Background = ColorHelpers.FromArgb(36, 56, 189, 248),
            },
            TagTone.Ok => new Tag(text)
            {
                Foreground = ColorHelpers.FromRgb(52, 211, 153),
                Background = ColorHelpers.FromArgb(36, 16, 185, 129),
            },
            TagTone.Warn => new Tag(text)
            {
                Foreground = ColorHelpers.FromRgb(251, 191, 36),
                Background = ColorHelpers.FromArgb(38, 245, 158, 11),
            },
            TagTone.Danger => new Tag(text)
            {
                Foreground = ColorHelpers.FromRgb(251, 113, 133),
                Background = ColorHelpers.FromArgb(38, 244, 63, 94),
            },
            _ => new Tag(text),
        };

        if (icon is not null)
        {
            tag.Icon = icon;
        }

        return tag;
    }

    public static async Task<(InMemoryRandomAccessStream Stream, IconData IconData)> CreatePngIconDataAsync(byte[] pngBytes)
    {
        var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(pngBytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }

        stream.Seek(0);
        return (stream, new IconData(RandomAccessStreamReference.CreateFromStream(stream)));
    }

    public static void DisposeStreams(IEnumerable<InMemoryRandomAccessStream> streams)
    {
        foreach (var stream in streams)
        {
            try
            {
                stream.Dispose();
            }
            catch
            {
                // Ignore disposal errors
            }
        }
    }

    public static bool TryReadString(JsonElement root, string name, out string value)
    {
        value = string.Empty;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var prop))
        {
            return false;
        }

        switch (prop.ValueKind)
        {
            case JsonValueKind.String:
                value = prop.GetString() ?? string.Empty;
                return true;
            case JsonValueKind.Number:
                value = prop.GetRawText();
                return true;
            case JsonValueKind.True:
                value = "true";
                return true;
            case JsonValueKind.False:
                value = "false";
                return true;
            default:
                return false;
        }
    }

    public static bool TryReadInt(JsonElement root, string name, out int value)
    {
        value = 0;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var prop))
        {
            return false;
        }

        if (prop.ValueKind == JsonValueKind.Number)
        {
            if (prop.TryGetInt32(out value))
            {
                return true;
            }

            if (prop.TryGetDouble(out double d) && !double.IsNaN(d) && !double.IsInfinity(d))
            {
                value = (int)Math.Round(d);
                return true;
            }

            return false;
        }

        if (prop.ValueKind == JsonValueKind.String)
        {
            string? raw = prop.GetString();
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            {
                return true;
            }

            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) &&
                !double.IsNaN(d) && !double.IsInfinity(d))
            {
                value = (int)Math.Round(d);
                return true;
            }
        }

        return false;
    }

    public static bool TryReadBool(JsonElement root, string name, out bool value)
    {
        value = false;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var prop))
        {
            return false;
        }

        switch (prop.ValueKind)
        {
            case JsonValueKind.True:
                value = true;
                return true;
            case JsonValueKind.False:
                value = false;
                return true;
            case JsonValueKind.String:
                string? s = prop.GetString();
                if (bool.TryParse(s, out value))
                {
                    return true;
                }

                if (string.Equals(s, "1", StringComparison.Ordinal))
                {
                    value = true;
                    return true;
                }

                if (string.Equals(s, "0", StringComparison.Ordinal))
                {
                    value = false;
                    return true;
                }

                return false;
            default:
                return false;
        }
    }
}

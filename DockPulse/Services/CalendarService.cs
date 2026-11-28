using System;
using System.Globalization;
using System.IO;
using System.Threading;
using DockPulse.Helpers;

namespace DockPulse.Services;

public enum CalendarKind
{
    Gregorian,
    Persian,
    Qamari,
}

public enum PersianMonthNames
{
    Afghan,
    Iranian,
}

public readonly record struct CalendarDate(int Year, int Month, int Day);

/// <summary>
/// Today's date in the Gregorian, Persian (Solar Hijri) and Qamari (Lunar Hijri, Umm al-Qura) calendars,
/// plus the Calendar band's settings (calendar.cfg): main calendar, Persian month names, Qamari adjustment.
/// </summary>
internal sealed class CalendarService : IDisposable
{
    public static readonly string[] GregorianMonths =
        ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];

    public static readonly string[] AfghanMonths =
        ["Hamal", "Sawr", "Jawza", "Saratan", "Asad", "Sonbola", "Mizan", "Aqrab", "Qaws", "Jadi", "Dalw", "Hut"];

    public static readonly string[] IranianMonths =
        ["Farvardin", "Ordibehesht", "Khordad", "Tir", "Mordad", "Shahrivar", "Mehr", "Aban", "Azar", "Dey", "Bahman", "Esfand"];

    public static readonly string[] QamariMonths =
        ["Muharram", "Safar", "Rabi al-Awwal", "Rabi al-Thani", "Jumada al-Ula", "Jumada al-Akhirah", "Rajab", "Shaban", "Ramadan", "Shawwal", "Dhu al-Qadah", "Dhu al-Hijjah"];

    private static readonly PersianCalendar Persian = new();
    private static readonly UmAlQuraCalendar UmAlQura = new();

    private readonly object _lock = new();
    private CalendarKind _main = CalendarKind.Gregorian;
    private PersianMonthNames _persianNames = PersianMonthNames.Afghan;
    private int _qamariAdjust;
    private readonly Timer _dayTimer;
    private DateTime _lastDay = DateTime.Today;

    /// <summary>Raised when a setting changes or the date rolls over at midnight.</summary>
    public event Action? Changed;

    public CalendarService()
    {
        Load();
        _dayTimer = new Timer(_ => CheckDayChanged(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    private void CheckDayChanged()
    {
        var today = DateTime.Today;
        if (today == _lastDay)
        {
            return;
        }

        _lastDay = today;
        RaiseChanged();
    }

    public void Dispose() => _dayTimer.Dispose();

    public CalendarKind Main
    {
        get { lock (_lock) { return _main; } }
    }

    public PersianMonthNames PersianNames
    {
        get { lock (_lock) { return _persianNames; } }
    }

    public int QamariAdjust
    {
        get { lock (_lock) { return _qamariAdjust; } }
    }

    public void SetMain(CalendarKind kind) => Update(() => _main = kind);

    public void SetPersianNames(PersianMonthNames names) => Update(() => _persianNames = names);

    public void SetQamariAdjust(int days) => Update(() => _qamariAdjust = Math.Clamp(days, -1, 1));

    public CalendarDate DateIn(CalendarKind kind, DateTime date) => DateIn(kind, date, QamariAdjust);

    public static CalendarDate DateIn(CalendarKind kind, DateTime date, int qamariAdjust)
    {
        date = date.Date;
        switch (kind)
        {
            case CalendarKind.Persian:
                return new CalendarDate(Persian.GetYear(date), Persian.GetMonth(date), Persian.GetDayOfMonth(date));
            case CalendarKind.Qamari:
                var shifted = date.AddDays(qamariAdjust);
                return new CalendarDate(UmAlQura.GetYear(shifted), UmAlQura.GetMonth(shifted), UmAlQura.GetDayOfMonth(shifted));
            default:
                return new CalendarDate(date.Year, date.Month, date.Day);
        }
    }

    public string[] MonthNames(CalendarKind kind) => MonthNames(kind, PersianNames);

    public static string[] MonthNames(CalendarKind kind, PersianMonthNames persianNames) => kind switch
    {
        CalendarKind.Persian => persianNames == PersianMonthNames.Iranian ? IranianMonths : AfghanMonths,
        CalendarKind.Qamari => QamariMonths,
        _ => GregorianMonths,
    };

    /// <summary>"2 Qaws 1405", "13 Jumada al-Akhirah 1448", "23 November 2026".</summary>
    public string Format(CalendarKind kind, DateTime date) => Format(kind, date, PersianNames, QamariAdjust);

    public static string Format(CalendarKind kind, DateTime date, PersianMonthNames persianNames, int qamariAdjust)
    {
        var d = DateIn(kind, date, qamariAdjust);
        return $"{d.Day} {MonthNames(kind, persianNames)[d.Month - 1]} {d.Year}";
    }

    public static string Label(CalendarKind kind) => kind switch
    {
        CalendarKind.Persian => "Persian (Solar Hijri)",
        CalendarKind.Qamari => "Qamari (Lunar Hijri)",
        _ => "Gregorian",
    };

    public static string ShortLabel(CalendarKind kind) => kind switch
    {
        CalendarKind.Persian => "PERSIAN",
        CalendarKind.Qamari => "QAMARI",
        _ => "GREGORIAN",
    };

    public static string IsoDate(DateTime date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string LongDate(DateTime date) => date.ToString("dddd, d MMMM yyyy", CultureInfo.InvariantCulture);

    public static int IsoWeek(DateTime date) => ISOWeek.GetWeekOfYear(date);

    public static DayOfWeek FirstDayOfWeek => CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;

    /// <summary>First day of the week containing <paramref name="date"/>, using Windows' first-day-of-week setting.</summary>
    public static DateTime WeekStart(DateTime date)
    {
        int lead = ((int)date.DayOfWeek - (int)FirstDayOfWeek + 7) % 7;
        return date.Date.AddDays(-lead);
    }

    private void Update(Action change)
    {
        lock (_lock)
        {
            change();
        }

        Save();
        RaiseChanged();
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch
        {
            // Ignore subscriber errors
        }
    }

    private static string FilePath => DockPulseHelpers.GetSettingsFilePath("calendar.cfg");

    private void Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return;
            }

            foreach (var raw in File.ReadAllLines(FilePath))
            {
                int eq = raw.IndexOf('=');
                if (eq <= 0)
                {
                    continue;
                }

                string key = raw[..eq].Trim().ToLowerInvariant();
                string value = raw[(eq + 1)..].Trim().ToLowerInvariant();
                switch (key)
                {
                    case "main":
                        _main = value switch { "persian" => CalendarKind.Persian, "qamari" => CalendarKind.Qamari, _ => CalendarKind.Gregorian };
                        break;
                    case "persian":
                        _persianNames = value == "iranian" ? PersianMonthNames.Iranian : PersianMonthNames.Afghan;
                        break;
                    case "qamari":
                        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int adj))
                        {
                            _qamariAdjust = Math.Clamp(adj, -1, 1);
                        }

                        break;
                }
            }
        }
        catch
        {
            // Ignore unreadable settings and keep defaults
        }
    }

    private void Save()
    {
        try
        {
            string main, persian;
            int adjust;
            lock (_lock)
            {
                main = _main.ToString().ToLowerInvariant();
                persian = _persianNames.ToString().ToLowerInvariant();
                adjust = _qamariAdjust;
            }

            File.WriteAllLines(FilePath,
            [
                $"main={main}",
                $"persian={persian}",
                $"qamari={adjust.ToString(CultureInfo.InvariantCulture)}",
            ]);
        }
        catch
        {
            // Ignore write errors
        }
    }
}

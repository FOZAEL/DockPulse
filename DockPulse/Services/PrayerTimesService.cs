using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace DockPulse.Services;

public enum AsrJuristicSchool
{
    Standard = 1, // 1x shadow length
    Hanafi = 2,   // 2x shadow length
}

public sealed record PrayerCalculationMethod(
    string Id,
    string Name,
    string ShortName,
    double FajrAngle,
    double IshaAngle,
    double IshaMinutesAfterMaghrib = 0,
    int DhuhrMinuteMargin = 1,
    double MaghribAngle = 0.833,
    bool UsesTwilightMaghrib = false);

public sealed record PrayerRegion(
    string Id,
    string City,
    string Country,
    double Latitude,
    double Longitude,
    string WindowsTimeZoneId,
    string DefaultMethodId,
    AsrJuristicSchool DefaultAsrSchool)
{
    public string DisplayName => $"{City}, {Country}";
}

public sealed record PrayerEntry(
    string Name,
    DateTimeOffset Time,
    bool IsObligatory,
    bool IsPassed,
    bool IsCurrent,
    bool IsNext);

public sealed record PrayerScheduleSnapshot(
    DateTimeOffset RegionNow,
    string UtcOffsetLabel,
    PrayerRegion Region,
    PrayerCalculationMethod Method,
    AsrJuristicSchool AsrSchool,
    bool NotificationsEnabled,
    IReadOnlyList<PrayerEntry> TodayEntries,
    PrayerEntry? ExactNowPrayer,
    string NextPrayerName,
    DateTimeOffset NextPrayerTime,
    bool NextPrayerIsTomorrow,
    TimeSpan TimeUntilNext)
{
    public string CardPrayerName => ExactNowPrayer?.Name ?? NextPrayerName;

    public bool IsExactPrayerMinute => ExactNowPrayer is not null;

    public string FormatCountdownShort()
    {
        if (ExactNowPrayer is not null)
        {
            return $"Now: {ExactNowPrayer.Name}";
        }

        int totalMinutes = Math.Max(0, (int)Math.Ceiling(TimeUntilNext.TotalMinutes));
        if (totalMinutes <= 0)
        {
            return $"Now: {NextPrayerName}";
        }

        if (totalMinutes < 60)
        {
            return $"{totalMinutes}m to {NextPrayerName}";
        }

        int hours = totalMinutes / 60;
        int minutes = totalMinutes % 60;
        return minutes == 0
            ? $"{hours}h to {NextPrayerName}"
            : $"{hours}h {minutes}m to {NextPrayerName}";
    }

    public string FormatDockSubtitle() =>
        $"{NextPrayerTime:HH:mm} • {Region.City}";

    public string FormatCountdownLong()
    {
        if (ExactNowPrayer is not null)
        {
            return "Starting now";
        }

        int totalMinutes = Math.Max(0, (int)Math.Ceiling(TimeUntilNext.TotalMinutes));
        if (totalMinutes <= 0)
        {
            return "Starting now";
        }

        if (totalMinutes < 60)
        {
            return $"in {totalMinutes}m";
        }

        int hours = totalMinutes / 60;
        int minutes = totalMinutes % 60;
        return minutes == 0 ? $"in {hours}h" : $"in {hours}h {minutes}m";
    }
}

/// <summary>
/// Offline astronomical prayer time calculator using standard solar position equations
/// (NOAA / Meeus) with 12 calculation conventions, Hanafi &amp; Standard Asr schools,
/// twilight Maghrib angles, high-latitude fallback, and Windows toast notifications.
/// </summary>
public sealed class PrayerTimesService
{
    public static readonly IReadOnlyList<PrayerCalculationMethod> Methods =
    [
        new("karachi", "University of Islamic Sciences, Karachi", "Karachi (18°/18°)", 18.0, 18.0),
        new("mwl", "Muslim World League", "MWL (18°/17°)", 18.0, 17.0),
        new("umm_al_qura", "Umm al-Qura University, Makkah", "Umm al-Qura (18.5°/90m)", 18.5, 0.0, IshaMinutesAfterMaghrib: 90),
        new("dubai", "Dubai / UAE (IACAD)", "Dubai (18.2°/18.2°)", 18.2, 18.2),
        new("egypt", "Egyptian General Authority of Survey", "Egypt (19.5°/17.5°)", 19.5, 17.5),
        new("isna", "Islamic Society of North America", "ISNA (15°/15°)", 15.0, 15.0),
        new("diyanet", "Diyanet Isleri Baskanligi (Turkey)", "Diyanet (18°/17°)", 18.0, 17.0),
        new("jakim", "JAKIM / MUIS / Kemenag (SE Asia)", "SE Asia (20°/18°)", 20.0, 18.0),
        new("kuwait", "Kuwait Ministry of Awqaf", "Kuwait (18°/17.5°)", 18.0, 17.5),
        new("qatar", "Qatar Awqaf", "Qatar (18°/90m)", 18.0, 0.0, IshaMinutesAfterMaghrib: 90),
        new("jafari", "Ithna-Ashari / Jafari (Leva Institute, Qum)", "Jafari (16°/4°/14°)", 16.0, 14.0, MaghribAngle: 4.0, UsesTwilightMaghrib: true),
        new("tehran", "Institute of Geophysics, University of Tehran", "Tehran (17.7°/4.5°/14°)", 17.7, 14.0, MaghribAngle: 4.5, UsesTwilightMaghrib: true),
    ];

    public static readonly IReadOnlyList<PrayerRegion> Regions =
    [
        // Afghanistan
        new("kabul", "Kabul", "Afghanistan", 34.5553, 69.2075, "Afghanistan Standard Time", "karachi", AsrJuristicSchool.Hanafi),
        new("herat", "Herat", "Afghanistan", 34.3529, 62.2040, "Afghanistan Standard Time", "karachi", AsrJuristicSchool.Hanafi),
        new("mazar", "Mazar-i-Sharif", "Afghanistan", 36.7090, 67.1109, "Afghanistan Standard Time", "karachi", AsrJuristicSchool.Hanafi),
        new("kandahar", "Kandahar", "Afghanistan", 31.6289, 65.7372, "Afghanistan Standard Time", "karachi", AsrJuristicSchool.Hanafi),
        new("jalalabad", "Jalalabad", "Afghanistan", 34.4265, 70.4515, "Afghanistan Standard Time", "karachi", AsrJuristicSchool.Hanafi),

        // Middle East & Gulf
        new("makkah", "Makkah", "Saudi Arabia", 21.4225, 39.8262, "Arab Standard Time", "umm_al_qura", AsrJuristicSchool.Standard),
        new("madinah", "Madinah", "Saudi Arabia", 24.4672, 39.6111, "Arab Standard Time", "umm_al_qura", AsrJuristicSchool.Standard),
        new("riyadh", "Riyadh", "Saudi Arabia", 24.7136, 46.6753, "Arab Standard Time", "umm_al_qura", AsrJuristicSchool.Standard),
        new("jeddah", "Jeddah", "Saudi Arabia", 21.5433, 39.1728, "Arab Standard Time", "umm_al_qura", AsrJuristicSchool.Standard),
        new("dubai", "Dubai", "UAE", 25.2048, 55.2708, "Arabian Standard Time", "dubai", AsrJuristicSchool.Standard),
        new("abudhabi", "Abu Dhabi", "UAE", 24.4539, 54.3773, "Arabian Standard Time", "dubai", AsrJuristicSchool.Standard),
        new("doha", "Doha", "Qatar", 25.2854, 51.5310, "Arab Standard Time", "qatar", AsrJuristicSchool.Standard),
        new("kuwait", "Kuwait City", "Kuwait", 29.3759, 47.9774, "Arab Standard Time", "kuwait", AsrJuristicSchool.Standard),
        new("muscat", "Muscat", "Oman", 23.5880, 58.3829, "Arabian Standard Time", "umm_al_qura", AsrJuristicSchool.Standard),
        new("amman", "Amman", "Jordan", 31.9454, 35.9284, "Jordan Standard Time", "egypt", AsrJuristicSchool.Standard),
        new("jerusalem", "Jerusalem", "Palestine", 31.7683, 35.2137, "Israel Standard Time", "egypt", AsrJuristicSchool.Standard),
        new("baghdad", "Baghdad", "Iraq", 33.3152, 44.3661, "Arabic Standard Time", "mwl", AsrJuristicSchool.Standard),
        new("najaf", "Najaf", "Iraq", 31.9960, 44.3148, "Arabic Standard Time", "jafari", AsrJuristicSchool.Standard),
        new("karbala", "Karbala", "Iraq", 32.6160, 44.0249, "Arabic Standard Time", "jafari", AsrJuristicSchool.Standard),
        new("tehran", "Tehran", "Iran", 35.6892, 51.3890, "Iran Standard Time", "tehran", AsrJuristicSchool.Standard),
        new("mashhad", "Mashhad", "Iran", 36.2605, 59.6168, "Iran Standard Time", "tehran", AsrJuristicSchool.Standard),
        new("qom", "Qom", "Iran", 34.6416, 50.8746, "Iran Standard Time", "jafari", AsrJuristicSchool.Standard),
        new("isfahan", "Isfahan", "Iran", 32.6546, 51.6680, "Iran Standard Time", "tehran", AsrJuristicSchool.Standard),
        new("manama", "Manama", "Bahrain", 26.2285, 50.5860, "Arab Standard Time", "umm_al_qura", AsrJuristicSchool.Standard),
        new("baku", "Baku", "Azerbaijan", 40.4093, 49.8671, "Azerbaijan Standard Time", "jafari", AsrJuristicSchool.Standard),
        new("beirut", "Beirut", "Lebanon", 33.8938, 35.5018, "Middle East Standard Time", "egypt", AsrJuristicSchool.Standard),
        new("istanbul", "Istanbul", "Turkey", 41.0082, 28.9784, "Turkey Standard Time", "diyanet", AsrJuristicSchool.Hanafi),
        new("ankara", "Ankara", "Turkey", 39.9334, 32.8597, "Turkey Standard Time", "diyanet", AsrJuristicSchool.Hanafi),

        // South & Central Asia
        new("islamabad", "Islamabad", "Pakistan", 33.6844, 73.0479, "Pakistan Standard Time", "karachi", AsrJuristicSchool.Hanafi),
        new("karachi", "Karachi", "Pakistan", 24.8607, 67.0011, "Pakistan Standard Time", "karachi", AsrJuristicSchool.Hanafi),
        new("lahore", "Lahore", "Pakistan", 31.5204, 74.3587, "Pakistan Standard Time", "karachi", AsrJuristicSchool.Hanafi),
        new("peshawar", "Peshawar", "Pakistan", 34.0151, 71.5249, "Pakistan Standard Time", "karachi", AsrJuristicSchool.Hanafi),
        new("delhi", "New Delhi", "India", 28.6139, 77.2090, "India Standard Time", "karachi", AsrJuristicSchool.Hanafi),
        new("mumbai", "Mumbai", "India", 19.0760, 72.8777, "India Standard Time", "karachi", AsrJuristicSchool.Hanafi),
        new("hyderabad", "Hyderabad", "India", 17.3850, 78.4867, "India Standard Time", "karachi", AsrJuristicSchool.Hanafi),
        new("dhaka", "Dhaka", "Bangladesh", 23.8103, 90.4125, "Bangladesh Standard Time", "karachi", AsrJuristicSchool.Hanafi),
        new("tashkent", "Tashkent", "Uzbekistan", 41.2995, 69.2401, "West Asia Standard Time", "karachi", AsrJuristicSchool.Hanafi),
        new("almaty", "Almaty", "Kazakhstan", 43.2220, 76.8512, "Central Asia Standard Time", "mwl", AsrJuristicSchool.Hanafi),

        // Southeast Asia
        new("kualalumpur", "Kuala Lumpur", "Malaysia", 3.1390, 101.6869, "Singapore Standard Time", "jakim", AsrJuristicSchool.Standard),
        new("singapore", "Singapore", "Singapore", 1.3521, 103.8198, "Singapore Standard Time", "jakim", AsrJuristicSchool.Standard),
        new("jakarta", "Jakarta", "Indonesia", -6.2088, 106.8456, "SE Asia Standard Time", "jakim", AsrJuristicSchool.Standard),

        // North Africa
        new("cairo", "Cairo", "Egypt", 30.0444, 31.2357, "Egypt Standard Time", "egypt", AsrJuristicSchool.Standard),
        new("casablanca", "Casablanca", "Morocco", 33.5731, -7.5898, "Morocco Standard Time", "mwl", AsrJuristicSchool.Standard),
        new("algiers", "Algiers", "Algeria", 36.7538, 3.0588, "W. Central Africa Standard Time", "mwl", AsrJuristicSchool.Standard),
        new("tunis", "Tunis", "Tunisia", 36.8065, 10.1815, "W. Central Africa Standard Time", "mwl", AsrJuristicSchool.Standard),

        // Europe
        new("london", "London", "United Kingdom", 51.5074, -0.1278, "GMT Standard Time", "mwl", AsrJuristicSchool.Standard),
        new("birmingham", "Birmingham", "United Kingdom", 52.4862, -1.8904, "GMT Standard Time", "mwl", AsrJuristicSchool.Standard),
        new("paris", "Paris", "France", 48.8566, 2.3522, "Romance Standard Time", "mwl", AsrJuristicSchool.Standard),
        new("berlin", "Berlin", "Germany", 52.5200, 13.4050, "W. Europe Standard Time", "mwl", AsrJuristicSchool.Hanafi),
        new("frankfurt", "Frankfurt", "Germany", 50.1109, 8.6821, "W. Europe Standard Time", "mwl", AsrJuristicSchool.Hanafi),
        new("amsterdam", "Amsterdam", "Netherlands", 52.3676, 4.9041, "W. Europe Standard Time", "mwl", AsrJuristicSchool.Standard),
        new("stockholm", "Stockholm", "Sweden", 59.3293, 18.0686, "W. Europe Standard Time", "mwl", AsrJuristicSchool.Standard),

        // North America & Australia
        new("newyork", "New York", "USA", 40.7128, -74.0060, "Eastern Standard Time", "isna", AsrJuristicSchool.Standard),
        new("dearborn", "Dearborn", "USA", 42.3223, -83.1763, "Eastern Standard Time", "isna", AsrJuristicSchool.Standard),
        new("chicago", "Chicago", "USA", 41.8781, -87.6298, "Central Standard Time", "isna", AsrJuristicSchool.Standard),
        new("houston", "Houston", "USA", 29.7604, -95.3698, "Central Standard Time", "isna", AsrJuristicSchool.Standard),
        new("losangeles", "Los Angeles", "USA", 34.0522, -118.2437, "Pacific Standard Time", "isna", AsrJuristicSchool.Standard),
        new("toronto", "Toronto", "Canada", 43.6532, -79.3832, "Eastern Standard Time", "isna", AsrJuristicSchool.Standard),
        new("vancouver", "Vancouver", "Canada", 49.2827, -123.1207, "Pacific Standard Time", "isna", AsrJuristicSchool.Standard),
        new("sydney", "Sydney", "Australia", -33.8688, 151.2093, "AUS Eastern Standard Time", "mwl", AsrJuristicSchool.Standard),
        new("melbourne", "Melbourne", "Australia", -37.8136, 144.9631, "AUS Eastern Standard Time", "mwl", AsrJuristicSchool.Standard),
    ];

    private readonly object _configLock = new();
    private readonly string _configPath;

    private PrayerRegion _selectedRegion;
    private PrayerCalculationMethod _selectedMethod;
    private AsrJuristicSchool _selectedAsrSchool;
    private bool _notificationsEnabled = true;
    private string _lastNotifiedKey = string.Empty;

    public event Action? ConfigurationChanged;

    public PrayerTimesService()
    {
        _configPath = GetConfigPath();

        _selectedRegion = DetectDefaultRegion();
        _selectedMethod = FindMethod(_selectedRegion.DefaultMethodId);
        _selectedAsrSchool = _selectedRegion.DefaultAsrSchool;

        LoadConfig();
    }

    public PrayerRegion SelectedRegion
    {
        get
        {
            lock (_configLock)
            {
                return _selectedRegion;
            }
        }
    }

    public PrayerCalculationMethod SelectedMethod
    {
        get
        {
            lock (_configLock)
            {
                return _selectedMethod;
            }
        }
    }

    public AsrJuristicSchool SelectedAsrSchool
    {
        get
        {
            lock (_configLock)
            {
                return _selectedAsrSchool;
            }
        }
    }

    public bool NotificationsEnabled
    {
        get
        {
            lock (_configLock)
            {
                return _notificationsEnabled;
            }
        }
    }

    public void SelectRegion(string regionId)
    {
        var region = Regions.FirstOrDefault(r => string.Equals(r.Id, regionId, StringComparison.OrdinalIgnoreCase));
        if (region is null)
        {
            return;
        }

        lock (_configLock)
        {
            _selectedRegion = region;
            _selectedMethod = FindMethod(region.DefaultMethodId);
            _selectedAsrSchool = region.DefaultAsrSchool;
            SaveConfigLocked();
        }

        ConfigurationChanged?.Invoke();
    }

    public void SelectMethod(string methodId)
    {
        var method = Methods.FirstOrDefault(m => string.Equals(m.Id, methodId, StringComparison.OrdinalIgnoreCase));
        if (method is null)
        {
            return;
        }

        lock (_configLock)
        {
            _selectedMethod = method;
            if (method.UsesTwilightMaghrib)
            {
                _selectedAsrSchool = AsrJuristicSchool.Standard;
            }

            SaveConfigLocked();
        }

        ConfigurationChanged?.Invoke();
    }

    public void ToggleAsrSchool()
    {
        lock (_configLock)
        {
            _selectedAsrSchool = _selectedAsrSchool == AsrJuristicSchool.Hanafi
                ? AsrJuristicSchool.Standard
                : AsrJuristicSchool.Hanafi;
            SaveConfigLocked();
        }

        ConfigurationChanged?.Invoke();
    }

    public void ToggleNotifications()
    {
        lock (_configLock)
        {
            _notificationsEnabled = !_notificationsEnabled;
            SaveConfigLocked();
        }

        ConfigurationChanged?.Invoke();
    }

    public PrayerScheduleSnapshot GetCurrentSchedule()
    {
        PrayerRegion region;
        PrayerCalculationMethod method;
        AsrJuristicSchool asrSchool;
        bool notificationsEnabled;

        lock (_configLock)
        {
            region = _selectedRegion;
            method = _selectedMethod;
            asrSchool = _selectedAsrSchool;
            notificationsEnabled = _notificationsEnabled;
        }

        var tz = ResolveTimeZone(region.WindowsTimeZoneId);
        var utcNow = DateTimeOffset.UtcNow;
        var regionNow = TimeZoneInfo.ConvertTime(utcNow, tz);
        string utcOffsetLabel = FormatUtcOffset(regionNow.Offset);

        var todayDate = new DateOnly(regionNow.Year, regionNow.Month, regionNow.Day);
        var todayTimes = CalculateDayTimes(todayDate, region, method, asrSchool, tz);

        int nextIdx = -1;
        int currentIdx = -1;

        for (int i = 0; i < todayTimes.Count; i++)
        {
            var entry = todayTimes[i];
            if (!entry.IsObligatory)
            {
                continue;
            }

            if (entry.Time > regionNow)
            {
                nextIdx = i;
                break;
            }

            currentIdx = i;
        }

        string nextName;
        DateTimeOffset nextTime;
        bool nextIsTomorrow = false;

        if (nextIdx >= 0)
        {
            nextName = todayTimes[nextIdx].Name;
            nextTime = todayTimes[nextIdx].Time;
        }
        else
        {
            // Past Isha: roll over cleanly to tomorrow's Fajr
            var tomorrowTimes = CalculateDayTimes(todayDate.AddDays(1), region, method, asrSchool, tz);
            nextName = tomorrowTimes[0].Name;
            nextTime = tomorrowTimes[0].Time;
            nextIsTomorrow = true;
        }

        PrayerEntry? exactNowPrayer = null;
        var finalEntries = new List<PrayerEntry>(todayTimes.Count);
        for (int i = 0; i < todayTimes.Count; i++)
        {
            var raw = todayTimes[i];
            bool isNext = !nextIsTomorrow && i == nextIdx;
            bool isCurrent = i == currentIdx;
            bool isPassed = raw.Time <= regionNow && !isCurrent;
            var finalized = raw with { IsPassed = isPassed, IsCurrent = isCurrent, IsNext = isNext };
            finalEntries.Add(finalized);

            if (finalized.IsObligatory)
            {
                double elapsedMinutes = (regionNow - finalized.Time).TotalMinutes;
                if (elapsedMinutes >= 0.0 && elapsedMinutes < 1.0)
                {
                    exactNowPrayer = finalized;
                }
            }
        }

        var remaining = nextTime - utcNow;
        if (remaining < TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
        }

        return new PrayerScheduleSnapshot(
            RegionNow: regionNow,
            UtcOffsetLabel: utcOffsetLabel,
            Region: region,
            Method: method,
            AsrSchool: asrSchool,
            NotificationsEnabled: notificationsEnabled,
            TodayEntries: finalEntries,
            ExactNowPrayer: exactNowPrayer,
            NextPrayerName: nextName,
            NextPrayerTime: nextTime,
            NextPrayerIsTomorrow: nextIsTomorrow,
            TimeUntilNext: remaining);
    }

    public void CheckAndSendNotification(PrayerScheduleSnapshot snapshot)
    {
        if (!snapshot.NotificationsEnabled)
        {
            return;
        }

        foreach (var entry in snapshot.TodayEntries)
        {
            if (!entry.IsObligatory)
            {
                continue;
            }

            double elapsedMinutes = (snapshot.RegionNow - entry.Time).TotalMinutes;
            if (elapsedMinutes < 0.0 || elapsedMinutes >= 2.0)
            {
                continue;
            }

            string notifyKey = $"{snapshot.Region.Id}|{entry.Time:yyyy-MM-dd}|{entry.Name}";
            lock (_configLock)
            {
                if (!_notificationsEnabled || string.Equals(_lastNotifiedKey, notifyKey, StringComparison.Ordinal))
                {
                    return;
                }

                _lastNotifiedKey = notifyKey;
                SaveConfigLocked();
            }

            ShowWindowsPrayerToast(entry.Name, entry.Time, snapshot.Region.DisplayName);
            return;
        }
    }

    public static void SendTestNotification(PrayerScheduleSnapshot snapshot)
    {
        ShowWindowsPrayerToast(snapshot.NextPrayerName, snapshot.NextPrayerTime, snapshot.Region.DisplayName);
    }

    private static void ShowWindowsPrayerToast(string prayerName, DateTimeOffset prayerTime, string regionDisplayName)
    {
        try
        {
            string title = SecurityElement.Escape($"{prayerName} Prayer Time — {prayerTime:HH:mm}") ?? prayerName;
            string body = SecurityElement.Escape($"It is now time for {prayerName} in {regionDisplayName}.") ?? regionDisplayName;

            string xml =
                "<toast>" +
                "<visual><binding template=\"ToastGeneric\">" +
                $"<text>{title}</text>" +
                $"<text>{body}</text>" +
                "</binding></visual>" +
                "<audio src=\"ms-winsoundevent:Notification.Default\"/>" +
                "</toast>";

            var doc = new XmlDocument();
            doc.LoadXml(xml);

            var toast = new ToastNotification(doc)
            {
                ExpirationTime = DateTimeOffset.Now.AddMinutes(30),
            };

            ToastNotificationManager.CreateToastNotifier().Show(toast);
        }
        catch
        {
            // Ignore notification errors if Windows Notifications are disabled at OS level
        }
    }

    public static string GetRegionUtcOffsetLabel(PrayerRegion region)
    {
        var tz = ResolveTimeZone(region.WindowsTimeZoneId);
        var offset = tz.GetUtcOffset(DateTimeOffset.UtcNow);
        return FormatUtcOffset(offset);
    }

    public static string FormatUtcOffset(TimeSpan offset)
    {
        char sign = offset < TimeSpan.Zero ? '-' : '+';
        TimeSpan abs = offset.Duration();
        return $"UTC{sign}{abs.Hours:00}:{abs.Minutes:00}";
    }

    public static string GetPrayerGlyph(string prayerName) => prayerName switch
    {
        "Dhuhr" or "Asr" or "Sunrise" or "Sunset" => "\uE706", // Sun
        _ => "\uE708",                                         // Crescent moon (Fajr, Maghrib, Isha)
    };

    public static string FormatAsrSchoolName(AsrJuristicSchool school) => school switch
    {
        AsrJuristicSchool.Hanafi => "Hanafi (2x shadow)",
        _ => "Standard (1x shadow)",
    };

    private static List<PrayerEntry> CalculateDayTimes(
        DateOnly date,
        PrayerRegion region,
        PrayerCalculationMethod method,
        AsrJuristicSchool asrSchool,
        TimeZoneInfo tz)
    {
        var localMidday = new DateTime(date.Year, date.Month, date.Day, 12, 0, 0, DateTimeKind.Unspecified);
        TimeSpan utcOffset = tz.GetUtcOffset(localMidday);
        double tzHours = utcOffset.TotalHours;

        double jd = JulianDate(date.Year, date.Month, date.Day) - (region.Longitude / (15.0 * 24.0));

        double sunrise = ComputeSunAngleTime(jd, region.Latitude, 0.833, 6.0 / 24.0, ccw: true);
        double sunset = ComputeSunAngleTime(jd, region.Latitude, 0.833, 18.0 / 24.0, ccw: false);
        double dhuhr = ComputeMidDay(jd, 12.0 / 24.0) + (method.DhuhrMinuteMargin / 60.0);
        double asr = ComputeAsrTime(jd, region.Latitude, (int)asrSchool, 13.0 / 24.0);
        double fajr = ComputeSunAngleTime(jd, region.Latitude, method.FajrAngle, 5.0 / 24.0, ccw: true);

        bool usesCustomMaghribAngle = method.MaghribAngle > 0.834;
        double maghrib = usesCustomMaghribAngle
            ? ComputeSunAngleTime(jd, region.Latitude, method.MaghribAngle, 18.0 / 24.0, ccw: false)
            : sunset;

        double isha = method.IshaMinutesAfterMaghrib > 0
            ? maghrib + (method.IshaMinutesAfterMaghrib / 60.0)
            : ComputeSunAngleTime(jd, region.Latitude, method.IshaAngle, 18.0 / 24.0, ccw: false);

        // Angle-based high-latitude fallback when sun does not reach twilight depression in midsummer
        double nightDuration = FixHour(sunrise - sunset);
        if (double.IsNaN(fajr))
        {
            fajr = sunrise - (method.FajrAngle / 60.0 * nightDuration);
        }

        if (usesCustomMaghribAngle && double.IsNaN(maghrib))
        {
            maghrib = sunset + (method.MaghribAngle / 60.0 * nightDuration);
        }

        if (method.IshaMinutesAfterMaghrib <= 0 && double.IsNaN(isha))
        {
            isha = sunset + (method.IshaAngle / 60.0 * nightDuration);
        }

        double shift = tzHours - (region.Longitude / 15.0);

        var entries = new List<PrayerEntry>(usesCustomMaghribAngle ? 7 : 6)
        {
            new("Fajr", ToDateTimeOffset(date, fajr + shift, utcOffset), IsObligatory: true, false, false, false),
            new("Sunrise", ToDateTimeOffset(date, sunrise + shift, utcOffset), IsObligatory: false, false, false, false),
            new("Dhuhr", ToDateTimeOffset(date, dhuhr + shift, utcOffset), IsObligatory: true, false, false, false),
            new("Asr", ToDateTimeOffset(date, asr + shift, utcOffset), IsObligatory: true, false, false, false),
        };

        if (usesCustomMaghribAngle)
        {
            entries.Add(new("Sunset", ToDateTimeOffset(date, sunset + shift, utcOffset), IsObligatory: false, false, false, false));
        }

        entries.Add(new("Maghrib", ToDateTimeOffset(date, maghrib + shift, utcOffset), IsObligatory: true, false, false, false));
        entries.Add(new("Isha", ToDateTimeOffset(date, isha + shift, utcOffset), IsObligatory: true, false, false, false));

        return entries;
    }

    private static DateTimeOffset ToDateTimeOffset(DateOnly date, double localHours, TimeSpan utcOffset)
    {
        double roundedHours = localHours + (0.5 / 60.0);
        int extraDays = (int)Math.Floor(roundedHours / 24.0);
        double wrapped = roundedHours - (extraDays * 24.0);
        int hour = Math.Clamp((int)Math.Floor(wrapped), 0, 23);
        int minute = Math.Clamp((int)Math.Floor((wrapped - hour) * 60.0), 0, 59);

        var targetDate = date.AddDays(extraDays);
        return new DateTimeOffset(targetDate.Year, targetDate.Month, targetDate.Day, hour, minute, 0, utcOffset);
    }

    private static double JulianDate(int year, int month, int day)
    {
        if (month <= 2)
        {
            year -= 1;
            month += 12;
        }

        double a = Math.Floor(year / 100.0);
        double b = 2.0 - a + Math.Floor(a / 4.0);
        return Math.Floor(365.25 * (year + 4716)) + Math.Floor(30.6001 * (month + 1)) + day + b - 1524.5;
    }

    private static void ComputeSunPosition(double jd, out double declDeg, out double eqTimeHours)
    {
        double d = jd - 2451545.0;
        double g = FixAngle(357.529 + (0.98560028 * d));
        double q = FixAngle(280.459 + (0.98564736 * d));
        double l = FixAngle(q + (1.915 * Math.Sin(DegToRad(g))) + (0.020 * Math.Sin(DegToRad(2.0 * g))));
        double e = 23.439 - (0.00000036 * d);

        double ra = RadToDeg(Math.Atan2(Math.Cos(DegToRad(e)) * Math.Sin(DegToRad(l)), Math.Cos(DegToRad(l)))) / 15.0;
        ra = FixHour(ra);

        declDeg = RadToDeg(Math.Asin(Math.Sin(DegToRad(e)) * Math.Sin(DegToRad(l))));
        eqTimeHours = (q / 15.0) - ra;
        if (eqTimeHours > 12.0)
        {
            eqTimeHours -= 24.0;
        }
        else if (eqTimeHours < -12.0)
        {
            eqTimeHours += 24.0;
        }
    }

    private static double ComputeMidDay(double jd, double dayFraction)
    {
        ComputeSunPosition(jd + dayFraction, out _, out double eqt);
        return FixHour(12.0 - eqt);
    }

    private static double ComputeSunAngleTime(double jd, double latitudeDeg, double depressionDeg, double dayFraction, bool ccw)
    {
        ComputeSunPosition(jd + dayFraction, out double declDeg, out _);
        double noon = ComputeMidDay(jd, dayFraction);

        double numerator = -Math.Sin(DegToRad(depressionDeg)) - (Math.Sin(DegToRad(declDeg)) * Math.Sin(DegToRad(latitudeDeg)));
        double denominator = Math.Cos(DegToRad(declDeg)) * Math.Cos(DegToRad(latitudeDeg));
        double cosT = numerator / denominator;

        if (cosT < -1.0 || cosT > 1.0)
        {
            return double.NaN;
        }

        double hourAngle = RadToDeg(Math.Acos(cosT)) / 15.0;
        return noon + (ccw ? -hourAngle : hourAngle);
    }

    private static double ComputeAsrTime(double jd, double latitudeDeg, int shadowFactor, double dayFraction)
    {
        ComputeSunPosition(jd + dayFraction, out double declDeg, out _);
        double depressionDeg = -RadToDeg(Math.Atan(1.0 / (shadowFactor + Math.Tan(DegToRad(Math.Abs(latitudeDeg - declDeg))))));
        return ComputeSunAngleTime(jd, latitudeDeg, depressionDeg, dayFraction, ccw: false);
    }

    private static double DegToRad(double deg) => deg * (Math.PI / 180.0);

    private static double RadToDeg(double rad) => rad * (180.0 / Math.PI);

    private static double FixAngle(double angle)
    {
        angle -= 360.0 * Math.Floor(angle / 360.0);
        return angle < 0 ? angle + 360.0 : angle;
    }

    private static double FixHour(double hour)
    {
        hour -= 24.0 * Math.Floor(hour / 24.0);
        return hour < 0 ? hour + 24.0 : hour;
    }

    private static TimeZoneInfo ResolveTimeZone(string windowsTimeZoneId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(windowsTimeZoneId);
        }
        catch
        {
            return TimeZoneInfo.Local;
        }
    }

    private static PrayerRegion DetectDefaultRegion()
    {
        string localTzId = TimeZoneInfo.Local.Id;
        var exactTzMatch = Regions.FirstOrDefault(r => string.Equals(r.WindowsTimeZoneId, localTzId, StringComparison.OrdinalIgnoreCase));
        if (exactTzMatch is not null)
        {
            return exactTzMatch;
        }

        TimeSpan localOffset = TimeZoneInfo.Local.BaseUtcOffset;
        foreach (var candidate in Regions)
        {
            var tz = ResolveTimeZone(candidate.WindowsTimeZoneId);
            if (tz.BaseUtcOffset == localOffset)
            {
                return candidate;
            }
        }

        return Regions[0];
    }

    private static PrayerCalculationMethod FindMethod(string methodId) =>
        Methods.FirstOrDefault(m => string.Equals(m.Id, methodId, StringComparison.OrdinalIgnoreCase)) ?? Methods[0];

    private static string GetConfigPath()
    {
        try
        {
            string dir = Utilities.BaseSettingsPath("DockPulse");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "prayer.cfg");
        }
        catch
        {
            string fallback = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DockPulse");
            Directory.CreateDirectory(fallback);
            return Path.Combine(fallback, "prayer.cfg");
        }
    }

    private void LoadConfig()
    {
        try
        {
            if (!File.Exists(_configPath))
            {
                return;
            }

            foreach (string rawLine in File.ReadAllLines(_configPath))
            {
                int sep = rawLine.IndexOf('=');
                if (sep <= 0)
                {
                    continue;
                }

                string key = rawLine[..sep].Trim();
                string value = rawLine[(sep + 1)..].Trim();

                if (string.Equals(key, "region", StringComparison.OrdinalIgnoreCase))
                {
                    var region = Regions.FirstOrDefault(r => string.Equals(r.Id, value, StringComparison.OrdinalIgnoreCase));
                    if (region is not null)
                    {
                        _selectedRegion = region;
                        _selectedMethod = FindMethod(region.DefaultMethodId);
                        _selectedAsrSchool = region.DefaultAsrSchool;
                    }
                }
                else if (string.Equals(key, "method", StringComparison.OrdinalIgnoreCase))
                {
                    var method = Methods.FirstOrDefault(m => string.Equals(m.Id, value, StringComparison.OrdinalIgnoreCase));
                    if (method is not null)
                    {
                        _selectedMethod = method;
                    }
                }
                else if (string.Equals(key, "asr", StringComparison.OrdinalIgnoreCase) &&
                         Enum.TryParse<AsrJuristicSchool>(value, ignoreCase: true, out var parsedSchool))
                {
                    _selectedAsrSchool = parsedSchool;
                }
                else if (string.Equals(key, "notifications", StringComparison.OrdinalIgnoreCase) &&
                         bool.TryParse(value, out bool parsedNotifications))
                {
                    _notificationsEnabled = parsedNotifications;
                }
                else if (string.Equals(key, "last_notified", StringComparison.OrdinalIgnoreCase))
                {
                    _lastNotifiedKey = value;
                }
            }
        }
        catch
        {
            // Keep default configuration if config file is unreadable
        }
    }

    private void SaveConfigLocked()
    {
        try
        {
            string[] lines =
            [
                $"region={_selectedRegion.Id}",
                $"method={_selectedMethod.Id}",
                $"asr={_selectedAsrSchool}",
                $"notifications={_notificationsEnabled}",
                $"last_notified={_lastNotifiedKey}",
            ];
            File.WriteAllLines(_configPath, lines);
        }
        catch
        {
            // Ignore write errors
        }
    }
}

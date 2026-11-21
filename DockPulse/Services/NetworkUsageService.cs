using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Windows.Networking.Connectivity;

namespace DockPulse.Services;

public sealed record UsageBucket(
    string Label,
    ulong ReceivedBytes,
    ulong SentBytes)
{
    public ulong TotalBytes => ReceivedBytes + SentBytes;
}

public sealed record AdapterUsage(
    string ProfileName,
    bool IsActiveInternet,
    ulong SentToday,
    ulong ReceivedToday,
    ulong SentMonth,
    ulong ReceivedMonth,
    IReadOnlyList<UsageBucket> HourlyToday,
    IReadOnlyList<UsageBucket> DailyMonth)
{
    public ulong TotalToday => SentToday + ReceivedToday;

    public ulong TotalMonth => SentMonth + ReceivedMonth;
}

public sealed record NetworkUsageSnapshot(
    DateTimeOffset UpdatedAt,
    DateTimeOffset TodayStart,
    DateTimeOffset MonthStart,
    int CurrentHour,
    int CurrentDay,
    ulong TotalSentToday,
    ulong TotalReceivedToday,
    ulong TotalSentMonth,
    ulong TotalReceivedMonth,
    IReadOnlyList<UsageBucket> HourlyToday,
    IReadOnlyList<UsageBucket> DailyMonth,
    IReadOnlyList<AdapterUsage> Adapters)
{
    public ulong TotalToday => TotalSentToday + TotalReceivedToday;

    public ulong TotalMonth => TotalSentMonth + TotalReceivedMonth;
}

public sealed class NetworkUsageService
{
    public async Task<NetworkUsageSnapshot> GetUsageAsync()
    {
        var now = DateTimeOffset.Now;

        // Midnight (00:00:00) local time at the start of today
        var todayStart = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset);
        var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, now.Offset);
        int daysInMonth = DateTime.DaysInMonth(now.Year, now.Month);

        // Guard against exact midnight tick (GetNetworkUsageAsync requires startTime < endTime)
        var queryEnd = now > todayStart ? now : todayStart.AddSeconds(1);

        var states = new NetworkUsageStates
        {
            Roaming = TriStates.DoNotCare,
            Shared = TriStates.DoNotCare,
        };

        var activeProfileName = NetworkInformation.GetInternetConnectionProfile()?.ProfileName;
        var profiles = NetworkInformation.GetConnectionProfiles();
        var seenProfiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var adapters = new List<AdapterUsage>();

        ulong sumSentToday = 0, sumRecvToday = 0;
        ulong sumSentMonth = 0, sumRecvMonth = 0;

        ulong[] aggHourlyRecv = new ulong[24];
        ulong[] aggHourlySent = new ulong[24];
        ulong[] aggDailyRecv = new ulong[daysInMonth];
        ulong[] aggDailySent = new ulong[daysInMonth];

        foreach (var profile in profiles)
        {
            if (profile is null || string.IsNullOrWhiteSpace(profile.ProfileName))
            {
                continue;
            }

            var dedupeKey = $"{profile.ProfileName}|{profile.NetworkAdapter?.NetworkAdapterId}";
            if (!seenProfiles.Add(dedupeKey))
            {
                continue;
            }

            try
            {
                var (sToday, rToday, adapterHourlyRecv, adapterHourlySent) =
                    await QuerySeriesWithFallbackAsync(
                        profile,
                        todayStart,
                        queryEnd,
                        DataUsageGranularity.PerHour,
                        bucketCount: 24,
                        fallbackBucketIndex: Math.Clamp(now.Hour, 0, 23),
                        states).ConfigureAwait(false);

                var (sMonth, rMonth, adapterDailyRecv, adapterDailySent) =
                    await QuerySeriesWithFallbackAsync(
                        profile,
                        monthStart,
                        queryEnd,
                        DataUsageGranularity.PerDay,
                        bucketCount: daysInMonth,
                        fallbackBucketIndex: Math.Clamp(now.Day - 1, 0, daysInMonth - 1),
                        states).ConfigureAwait(false);

                bool isActive = string.Equals(profile.ProfileName, activeProfileName, StringComparison.OrdinalIgnoreCase);

                if (sToday + rToday + sMonth + rMonth > 0 || isActive)
                {
                    adapters.Add(new AdapterUsage(
                        ProfileName: profile.ProfileName,
                        IsActiveInternet: isActive,
                        SentToday: sToday,
                        ReceivedToday: rToday,
                        SentMonth: sMonth,
                        ReceivedMonth: rMonth,
                        HourlyToday: BuildHourlyBuckets(adapterHourlyRecv, adapterHourlySent),
                        DailyMonth: BuildDailyBuckets(adapterDailyRecv, adapterDailySent)));

                    sumSentToday += sToday;
                    sumRecvToday += rToday;
                    sumSentMonth += sMonth;
                    sumRecvMonth += rMonth;

                    for (int h = 0; h < 24; h++)
                    {
                        aggHourlyRecv[h] += adapterHourlyRecv[h];
                        aggHourlySent[h] += adapterHourlySent[h];
                    }

                    for (int d = 0; d < daysInMonth; d++)
                    {
                        aggDailyRecv[d] += adapterDailyRecv[d];
                        aggDailySent[d] += adapterDailySent[d];
                    }
                }
            }
            catch
            {
                // Skip virtual/disconnected profiles without SRUM history
            }
        }

        var ordered = adapters
            .OrderByDescending(a => a.IsActiveInternet)
            .ThenByDescending(a => a.TotalMonth)
            .ToList();

        return new NetworkUsageSnapshot(
            UpdatedAt: now,
            TodayStart: todayStart,
            MonthStart: monthStart,
            CurrentHour: Math.Clamp(now.Hour, 0, 23),
            CurrentDay: Math.Clamp(now.Day, 1, daysInMonth),
            TotalSentToday: sumSentToday,
            TotalReceivedToday: sumRecvToday,
            TotalSentMonth: sumSentMonth,
            TotalReceivedMonth: sumRecvMonth,
            HourlyToday: BuildHourlyBuckets(aggHourlyRecv, aggHourlySent),
            DailyMonth: BuildDailyBuckets(aggDailyRecv, aggDailySent),
            Adapters: ordered);
    }

    private static async Task<(ulong Sent, ulong Received, ulong[] RecvBuckets, ulong[] SentBuckets)> QuerySeriesWithFallbackAsync(
        ConnectionProfile profile,
        DateTimeOffset start,
        DateTimeOffset end,
        DataUsageGranularity granularity,
        int bucketCount,
        int fallbackBucketIndex,
        NetworkUsageStates states)
    {
        ulong[] recvBuckets = new ulong[bucketCount];
        ulong[] sentBuckets = new ulong[bucketCount];
        ulong totalSent = 0;
        ulong totalRecv = 0;

        try
        {
            var series = await profile.GetNetworkUsageAsync(start, end, granularity, states);
            for (int i = 0; i < series.Count; i++)
            {
                var u = series[i];
                int slot = Math.Min(i, bucketCount - 1);
                recvBuckets[slot] += u.BytesReceived;
                sentBuckets[slot] += u.BytesSent;
                totalRecv += u.BytesReceived;
                totalSent += u.BytesSent;
            }

            return (totalSent, totalRecv, recvBuckets, sentBuckets);
        }
        catch
        {
            // Fallback to Total granularity if granular SRUM buckets are unavailable for this profile
            var totals = await profile.GetNetworkUsageAsync(start, end, DataUsageGranularity.Total, states);
            foreach (var u in totals)
            {
                totalRecv += u.BytesReceived;
                totalSent += u.BytesSent;
            }

            recvBuckets[fallbackBucketIndex] = totalRecv;
            sentBuckets[fallbackBucketIndex] = totalSent;
            return (totalSent, totalRecv, recvBuckets, sentBuckets);
        }
    }

    private static List<UsageBucket> BuildHourlyBuckets(ulong[] recv, ulong[] sent)
    {
        var list = new List<UsageBucket>(24);
        for (int h = 0; h < 24; h++)
        {
            list.Add(new UsageBucket(
                Label: h.ToString("00", CultureInfo.InvariantCulture),
                ReceivedBytes: recv[h],
                SentBytes: sent[h]));
        }

        return list;
    }

    private static List<UsageBucket> BuildDailyBuckets(ulong[] recv, ulong[] sent)
    {
        var list = new List<UsageBucket>(recv.Length);
        for (int d = 0; d < recv.Length; d++)
        {
            list.Add(new UsageBucket(
                Label: (d + 1).ToString(CultureInfo.InvariantCulture),
                ReceivedBytes: recv[d],
                SentBytes: sent[d]));
        }

        return list;
    }

    public static string FormatPeakHour(IReadOnlyList<UsageBucket> hourly)
    {
        int bestIdx = -1;
        ulong bestTotal = 0;
        for (int i = 0; i < hourly.Count; i++)
        {
            if (hourly[i].TotalBytes > bestTotal)
            {
                bestTotal = hourly[i].TotalBytes;
                bestIdx = i;
            }
        }

        return bestIdx >= 0
            ? $"{bestIdx:00}:00 ({FormatBytes(bestTotal)})"
            : "No traffic yet";
    }

    public static string FormatPeakDay(IReadOnlyList<UsageBucket> daily, DateTimeOffset monthStart)
    {
        int bestIdx = -1;
        ulong bestTotal = 0;
        for (int i = 0; i < daily.Count; i++)
        {
            if (daily[i].TotalBytes > bestTotal)
            {
                bestTotal = daily[i].TotalBytes;
                bestIdx = i;
            }
        }

        return bestIdx >= 0
            ? $"{monthStart.ToString("MMM", CultureInfo.InvariantCulture)} {bestIdx + 1} ({FormatBytes(bestTotal)})"
            : "No traffic yet";
    }

    public static string FormatBytes(ulong bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unitIndex = 0;

        while (value >= 1024d && unitIndex < units.Length - 1)
        {
            value /= 1024d;
            unitIndex++;
        }

        string format = unitIndex >= 3 ? "0.00" : (unitIndex >= 2 ? "0.0" : "0");
        return $"{value.ToString(format, CultureInfo.InvariantCulture)} {units[unitIndex]}";
    }
}

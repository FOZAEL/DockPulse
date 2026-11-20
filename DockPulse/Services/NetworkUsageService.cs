using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Windows.Networking.Connectivity;

namespace DockPulse.Services;

public sealed record AdapterUsage(
    string ProfileName,
    bool IsActiveInternet,
    ulong SentToday,
    ulong ReceivedToday,
    ulong SentMonth,
    ulong ReceivedMonth)
{
    public ulong TotalToday => SentToday + ReceivedToday;
    public ulong TotalMonth => SentMonth + ReceivedMonth;
}

public sealed record NetworkUsageSnapshot(
    DateTimeOffset UpdatedAt,
    DateTimeOffset TodayStart,
    DateTimeOffset MonthStart,
    ulong TotalSentToday,
    ulong TotalReceivedToday,
    ulong TotalSentMonth,
    ulong TotalReceivedMonth,
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
                var usageToday = await profile.GetNetworkUsageAsync(todayStart, queryEnd, DataUsageGranularity.Total, states);
                var usageMonth = await profile.GetNetworkUsageAsync(monthStart, queryEnd, DataUsageGranularity.Total, states);

                ulong sToday = 0, rToday = 0;
                foreach (var u in usageToday)
                {
                    sToday += u.BytesSent;
                    rToday += u.BytesReceived;
                }

                ulong sMonth = 0, rMonth = 0;
                foreach (var u in usageMonth)
                {
                    sMonth += u.BytesSent;
                    rMonth += u.BytesReceived;
                }

                bool isActive = string.Equals(profile.ProfileName, activeProfileName, StringComparison.OrdinalIgnoreCase);

                if (sToday + rToday + sMonth + rMonth > 0 || isActive)
                {
                    adapters.Add(new AdapterUsage(
                        ProfileName: profile.ProfileName,
                        IsActiveInternet: isActive,
                        SentToday: sToday,
                        ReceivedToday: rToday,
                        SentMonth: sMonth,
                        ReceivedMonth: rMonth));

                    sumSentToday += sToday;
                    sumRecvToday += rToday;
                    sumSentMonth += sMonth;
                    sumRecvMonth += rMonth;
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
            TotalSentToday: sumSentToday,
            TotalReceivedToday: sumRecvToday,
            TotalSentMonth: sumSentMonth,
            TotalReceivedMonth: sumRecvMonth,
            Adapters: ordered);
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

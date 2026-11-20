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
    ulong Sent24h,
    ulong Received24h,
    ulong SentMonth,
    ulong ReceivedMonth)
{
    public ulong Total24h => Sent24h + Received24h;
    public ulong TotalMonth => SentMonth + ReceivedMonth;
}

public sealed record NetworkUsageSnapshot(
    DateTimeOffset UpdatedAt,
    DateTimeOffset MonthStart,
    ulong TotalSent24h,
    ulong TotalReceived24h,
    ulong TotalSentMonth,
    ulong TotalReceivedMonth,
    IReadOnlyList<AdapterUsage> Adapters)
{
    public ulong Total24h => TotalSent24h + TotalReceived24h;
    public ulong TotalMonth => TotalSentMonth + TotalReceivedMonth;
}

public sealed class NetworkUsageService
{
    public async Task<NetworkUsageSnapshot> GetUsageAsync()
    {
        var now = DateTimeOffset.Now;
        var start24h = now.AddHours(-24);
        var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, now.Offset);

        var states = new NetworkUsageStates
        {
            Roaming = TriStates.DoNotCare,
            Shared = TriStates.DoNotCare,
        };

        var activeProfileName = NetworkInformation.GetInternetConnectionProfile()?.ProfileName;
        var profiles = NetworkInformation.GetConnectionProfiles();
        var seenProfiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var adapters = new List<AdapterUsage>();

        ulong sumSent24h = 0, sumRecv24h = 0;
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
                var usage24h = await profile.GetNetworkUsageAsync(start24h, now, DataUsageGranularity.Total, states);
                var usageMonth = await profile.GetNetworkUsageAsync(monthStart, now, DataUsageGranularity.Total, states);

                ulong s24 = 0, r24 = 0;
                foreach (var u in usage24h)
                {
                    s24 += u.BytesSent;
                    r24 += u.BytesReceived;
                }

                ulong sMonth = 0, rMonth = 0;
                foreach (var u in usageMonth)
                {
                    sMonth += u.BytesSent;
                    rMonth += u.BytesReceived;
                }

                bool isActive = string.Equals(profile.ProfileName, activeProfileName, StringComparison.OrdinalIgnoreCase);

                if (s24 + r24 + sMonth + rMonth > 0 || isActive)
                {
                    adapters.Add(new AdapterUsage(
                        ProfileName: profile.ProfileName,
                        IsActiveInternet: isActive,
                        Sent24h: s24,
                        Received24h: r24,
                        SentMonth: sMonth,
                        ReceivedMonth: rMonth));

                    sumSent24h += s24;
                    sumRecv24h += r24;
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
            MonthStart: monthStart,
            TotalSent24h: sumSent24h,
            TotalReceived24h: sumRecv24h,
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
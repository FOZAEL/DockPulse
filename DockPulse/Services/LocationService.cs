using System;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DockPulse.Helpers;

namespace DockPulse.Services;

public sealed record PublicIpLocation(
    string Ip,
    int IpVersion,
    string City,
    string Region,
    string Country,
    string CountryCode,
    double Latitude,
    double Longitude,
    string Asn,
    string AsnOrganization,
    string? TimeZoneId);

public sealed record ActiveAdapterInfo(string Name, string LocalIp, bool IsVpn, bool IsWireless);

public sealed record LocationSnapshot(PublicIpLocation? Location, ActiveAdapterInfo? Adapter, DateTime CheckedAt);

/// <summary>
/// Looks up the city and country of the PC's public IP with freeipapi.com: when the network changes,
/// at most every 30 minutes otherwise, and when the user asks.
/// Nothing is sent until <see cref="EnsureStarted"/> runs, which happens only when the My Location band is
/// shown or its page is opened, so people who don't use the band never contact freeipapi.com.
/// This is the only DockPulse feature that makes a network request.
/// </summary>
internal sealed class LocationService : IDisposable
{
    public const string SourceName = "freeipapi.com";
    public const string SourceUrl = "https://freeipapi.com";

    private const string LookupUrl = "https://free.freeipapi.com/api/v1/json/";
    private static readonly TimeSpan PeriodicInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan NetworkChangeDelay = TimeSpan.FromSeconds(5);

    private static readonly string[] VpnNameHints =
    [
        "vpn", "wireguard", "wintun", "openvpn", "tap-windows", "nordlynx", "mullvad", "proton",
        "anyconnect", "fortinet", "forticlient", "pangp", "globalprotect", "zscaler", "cloudflare warp",
    ];

    private readonly HttpClient _http;
    private readonly Timer _periodicTimer;
    private readonly Timer _networkChangeTimer;
    private int _started;
    private readonly object _lock = new();
    private LocationSnapshot? _snapshot;
    private int _isRefreshing;

    public event Action? Changed;

    public LocationService()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("DockPulse");

        _networkChangeTimer = new Timer(_ => _ = RefreshAsync(), null, Timeout.Infinite, Timeout.Infinite);
        _periodicTimer = new Timer(_ => _ = RefreshAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Starts looking up the location (first lookup now, then every 30 minutes and on network changes).</summary>
    public void EnsureStarted()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }

        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        _periodicTimer.Change(TimeSpan.Zero, PeriodicInterval);
    }

    public LocationSnapshot? GetSnapshot()
    {
        lock (_lock)
        {
            return _snapshot;
        }
    }

    public async Task RefreshAsync()
    {
        EnsureStarted();
        if (Interlocked.Exchange(ref _isRefreshing, 1) == 1)
        {
            return;
        }

        try
        {
            var adapter = GetActiveAdapter();
            PublicIpLocation? location = null;

            if (adapter is not null && NetworkInterface.GetIsNetworkAvailable())
            {
                try
                {
                    string json = await _http.GetStringAsync(LookupUrl).ConfigureAwait(false);
                    location = Parse(json);
                }
                catch
                {
                    // Lookup failed (offline, captive portal, service down): treated as offline below
                }
            }

            lock (_lock)
            {
                _snapshot = new LocationSnapshot(location, adapter, DateTime.Now);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _isRefreshing, 0);
        }

        try
        {
            Changed?.Invoke();
        }
        catch
        {
            // Ignore subscriber errors
        }
    }

    public static string FormatUtcOffset(string timeZoneId)
    {
        try
        {
            var offset = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId).GetUtcOffset(DateTime.UtcNow);
            string sign = offset < TimeSpan.Zero ? "-" : "+";
            return $"UTC{sign}{Math.Abs(offset.Hours):00}:{Math.Abs(offset.Minutes):00}";
        }
        catch
        {
            return string.Empty;
        }
    }

    public static string FormatPlace(PublicIpLocation location) =>
        string.Join(", ", new[] { location.City, location.Region, location.Country }
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Distinct(StringComparer.OrdinalIgnoreCase));

    public static string FormatShortPlace(PublicIpLocation location)
    {
        string first = string.IsNullOrWhiteSpace(location.City) ? location.Country : location.City;
        return string.IsNullOrWhiteSpace(location.CountryCode) ? first : $"{first}, {location.CountryCode}";
    }

    public static string GetMapsUrl(PublicIpLocation location) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"https://www.bing.com/maps?cp={location.Latitude:0.####}~{location.Longitude:0.####}&lvl=11");

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        // Address changes come in bursts while a connection or VPN comes up; look up once it settles
        _networkChangeTimer.Change(NetworkChangeDelay, Timeout.InfiniteTimeSpan);
    }

    private static PublicIpLocation? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!DockPulseHelpers.TryReadString(root, "ipAddress", out var ip) || string.IsNullOrWhiteSpace(ip))
        {
            return null;
        }

        DockPulseHelpers.TryReadInt(root, "ipVersion", out var ipVersion);
        DockPulseHelpers.TryReadString(root, "cityName", out var city);
        DockPulseHelpers.TryReadString(root, "regionName", out var region);
        DockPulseHelpers.TryReadString(root, "countryName", out var country);
        DockPulseHelpers.TryReadString(root, "countryCode", out var countryCode);
        DockPulseHelpers.TryReadString(root, "asn", out var asn);
        DockPulseHelpers.TryReadString(root, "asnOrganization", out var asnOrg);

        // timeZones lists every zone of the country, so it only names this location's zone when there is one
        string? timeZone = null;
        if (root.TryGetProperty("timeZones", out var zones) &&
            zones.ValueKind == JsonValueKind.Array &&
            zones.GetArrayLength() == 1 &&
            zones[0].ValueKind == JsonValueKind.String)
        {
            timeZone = zones[0].GetString();
        }

        return new PublicIpLocation(
            ip,
            ipVersion == 6 ? 6 : 4,
            city ?? string.Empty,
            region ?? string.Empty,
            country ?? string.Empty,
            countryCode ?? string.Empty,
            ReadDouble(root, "latitude"),
            ReadDouble(root, "longitude"),
            string.IsNullOrWhiteSpace(asn) ? string.Empty : $"AS{asn}",
            asnOrg ?? string.Empty,
            timeZone);
    }

    private static double ReadDouble(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : 0;

    /// <summary>The adapter Windows routes internet traffic through right now (a VPN tunnel when one is on).</summary>
    private static ActiveAdapterInfo? GetActiveAdapter()
    {
        try
        {
            // 8.8.8.8 in network byte order: every byte is 8, so byte order doesn't matter
            if (GetBestInterface(0x08080808, out uint bestIndex) != 0)
            {
                return null;
            }

            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                var props = nic.GetIPProperties();
                IPv4InterfaceProperties? v4;
                try
                {
                    v4 = props.GetIPv4Properties();
                }
                catch (NetworkInformationException)
                {
                    continue;
                }

                if (v4 is null || v4.Index != bestIndex)
                {
                    continue;
                }

                string localIp = props.UnicastAddresses
                    .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(a => a.Address.ToString())
                    .FirstOrDefault() ?? string.Empty;

                string haystack = $"{nic.Name} {nic.Description}".ToLowerInvariant();
                bool isVpn = nic.NetworkInterfaceType == NetworkInterfaceType.Ppp ||
                             VpnNameHints.Any(hint => haystack.Contains(hint, StringComparison.Ordinal));

                return new ActiveAdapterInfo(
                    nic.Name,
                    localIp,
                    isVpn,
                    nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211);
            }
        }
        catch
        {
            // Ignore interface enumeration failures
        }

        return null;
    }

    [DllImport("iphlpapi.dll")]
    private static extern uint GetBestInterface(uint dwDestAddr, out uint pdwBestIfIndex);

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        _periodicTimer.Dispose();
        _networkChangeTimer.Dispose();
        _http.Dispose();
    }
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Storage.Streams;

namespace DockPulse.Services;

/// <summary>
/// Clean-room MIT parser for Apple BLE Continuity Proximity Pairing (0x004C / 0x07)
/// advertisements broadcast by AirPods (1/2/3/4, Pro 1/2/3, Max) and Beats audio devices.
/// </summary>
internal sealed class AppleAirPodsBleReader : IDisposable
{
    private const ushort AppleCompanyId = 0x004C;
    private const byte ProximityPairingType = 0x07;
    private const byte ProximityPairingLength = 0x19;
    private const short MinMatchingRssi = -82;
    private const short StrongFallbackRssi = -65;
    private static readonly TimeSpan ReadingMaxAge = TimeSpan.FromMinutes(5);

    private readonly object _watcherLock = new();
    private readonly ConcurrentDictionary<ushort, AirPodsReading> _latestByModel = new();
    private BluetoothLEAdvertisementWatcher? _watcher;
    private bool _disposed;

    public sealed record AirPodsReading(
        ushort ModelId,
        int LeftPercent,
        int RightPercent,
        int CasePercent,
        bool LeftCharging,
        bool RightCharging,
        bool CaseCharging,
        short Rssi,
        DateTimeOffset SeenAtUtc)
    {
        public int PrimaryPercent
        {
            get
            {
                if (LeftPercent >= 0 && RightPercent >= 0)
                {
                    return Math.Min(LeftPercent, RightPercent);
                }

                if (LeftPercent >= 0)
                {
                    return LeftPercent;
                }

                if (RightPercent >= 0)
                {
                    return RightPercent;
                }

                return CasePercent >= 0 ? CasePercent : -1;
            }
        }

        public bool IsCharging
        {
            get
            {
                if (LeftPercent >= 0 && RightPercent >= 0)
                {
                    return LeftCharging && RightCharging;
                }

                if (LeftPercent >= 0)
                {
                    return LeftCharging;
                }

                if (RightPercent >= 0)
                {
                    return RightCharging;
                }

                return CaseCharging;
            }
        }

        public string FormatDetailText()
        {
            var parts = new List<string>(3);

            // Single-unit over-ear headphones (e.g. AirPods Max / Beats) report only one bud slot and no case
            if (IsSingleUnitModel(ModelId) && CasePercent < 0)
            {
                int pct = PrimaryPercent;
                if (pct >= 0)
                {
                    parts.Add(IsCharging ? $"Battery: {pct}% (Charging)" : $"Battery: {pct}%");
                }
            }
            else
            {
                if (LeftPercent >= 0)
                {
                    parts.Add(LeftCharging ? $"L: {LeftPercent}%⚡" : $"L: {LeftPercent}%");
                }

                if (RightPercent >= 0)
                {
                    parts.Add(RightCharging ? $"R: {RightPercent}%⚡" : $"R: {RightPercent}%");
                }

                if (CasePercent >= 0)
                {
                    parts.Add(CaseCharging ? $"Case: {CasePercent}%⚡" : $"Case: {CasePercent}%");
                }
            }

            return string.Join(" • ", parts);
        }
    }

    public void EnsureStarted()
    {
        lock (_watcherLock)
        {
            if (_disposed)
            {
                return;
            }

            if (_watcher is not null &&
                _watcher.Status is BluetoothLEAdvertisementWatcherStatus.Started or BluetoothLEAdvertisementWatcherStatus.Created)
            {
                if (_watcher.Status == BluetoothLEAdvertisementWatcherStatus.Created)
                {
                    try
                    {
                        _watcher.Start();
                    }
                    catch
                    {
                        // Ignore when Bluetooth radio is off or unavailable
                    }
                }

                return;
            }

            try
            {
                if (_watcher is not null)
                {
                    _watcher.Received -= OnAdvertisementReceived;
                }

                var watcher = new BluetoothLEAdvertisementWatcher
                {
                    ScanningMode = BluetoothLEScanningMode.Passive,
                };

                // Hardware/OS-level filter for Apple manufacturer packets (0x004C)
                watcher.AdvertisementFilter.Advertisement.ManufacturerData.Add(
                    new BluetoothLEManufacturerData { CompanyId = AppleCompanyId });

                watcher.Received += OnAdvertisementReceived;
                watcher.Start();
                _watcher = watcher;
            }
            catch
            {
                // Bluetooth LE radio unavailable
            }
        }
    }

    public AirPodsReading? TryGetFreshReading(ushort expectedModelId, bool allowStrongFallback)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        PruneStale(nowUtc);

        if (expectedModelId != 0 &&
            _latestByModel.TryGetValue(expectedModelId, out var exact) &&
            exact.Rssi >= MinMatchingRssi &&
            exact.PrimaryPercent >= 0)
        {
            return exact;
        }

        if (allowStrongFallback)
        {
            return _latestByModel.Values
                .Where(r => r.Rssi >= StrongFallbackRssi && r.PrimaryPercent >= 0)
                .OrderByDescending(r => r.Rssi)
                .FirstOrDefault();
        }

        return null;
    }

    private void PruneStale(DateTimeOffset nowUtc)
    {
        foreach (var kvp in _latestByModel)
        {
            if ((nowUtc - kvp.Value.SeenAtUtc) > ReadingMaxAge)
            {
                _latestByModel.TryRemove(kvp.Key, out _);
            }
        }
    }

    private void OnAdvertisementReceived(
        BluetoothLEAdvertisementWatcher sender,
        BluetoothLEAdvertisementReceivedEventArgs args)
    {
        try
        {
            if (args.RawSignalStrengthInDBm < MinMatchingRssi)
            {
                return;
            }

            foreach (var md in args.Advertisement.ManufacturerData)
            {
                if (md.CompanyId != AppleCompanyId || md.Data is null || md.Data.Length < 27)
                {
                    continue;
                }

                byte[] bytes = new byte[md.Data.Length];
                using (var reader = DataReader.FromBuffer(md.Data))
                {
                    reader.ReadBytes(bytes);
                }

                if (!TryParseProximityPairing(bytes, args.RawSignalStrengthInDBm, out var reading))
                {
                    continue;
                }

                var nowUtc = DateTimeOffset.UtcNow;
                _latestByModel.AddOrUpdate(
                    reading.ModelId,
                    reading,
                    (_, existing) =>
                    {
                        // Prefer newer packets from a nearby device; reject much weaker distant packets
                        bool existingStale = (nowUtc - existing.SeenAtUtc) > TimeSpan.FromSeconds(15);
                        return (existingStale || reading.Rssi >= existing.Rssi - 12) ? reading : existing;
                    });
            }
        }
        catch
        {
            // Ignore malformed BLE advertisements
        }
    }

    private static bool TryParseProximityPairing(byte[] bytes, short rssi, out AirPodsReading reading)
    {
        reading = null!;

        // Apple Continuity Proximity Pairing: [0]=0x07, [1]>=0x19 (25+ bytes), [2]=0x01 (paired) or 0x00 (lid open/pairing)
        if (bytes.Length < 27 ||
            bytes[0] != ProximityPairingType ||
            bytes[1] < ProximityPairingLength ||
            (bytes[2] != 0x01 && bytes[2] != 0x00))
        {
            return false;
        }

        ushort modelId = (ushort)((bytes[3] << 8) | bytes[4]);
        if (!IsSupportedAudioModel(modelId))
        {
            return false;
        }

        byte status = bytes[5];
        byte podsBattery = bytes[6];
        byte chargeAndCase = bytes[7];

        // Status bit 5 (0x20): 1 = Left is primary (low nibble), 0 = Right is primary (low nibble)
        bool leftIsPrimary = (status & 0x20) != 0;

        int primaryRaw = podsBattery & 0x0F;
        int secondaryRaw = (podsBattery >> 4) & 0x0F;
        int caseRaw = chargeAndCase & 0x0F;

        int chargeFlags = (chargeAndCase >> 4) & 0x0F;
        bool primaryCharging = (chargeFlags & 0x01) != 0;
        bool secondaryCharging = (chargeFlags & 0x02) != 0;
        bool caseCharging = (chargeFlags & 0x04) != 0;

        int leftPercent = DecodeNibblePercent(leftIsPrimary ? primaryRaw : secondaryRaw);
        int rightPercent = DecodeNibblePercent(leftIsPrimary ? secondaryRaw : primaryRaw);
        int casePercent = DecodeNibblePercent(caseRaw);

        bool leftCharging = leftIsPrimary ? primaryCharging : secondaryCharging;
        bool rightCharging = leftIsPrimary ? secondaryCharging : primaryCharging;

        var candidate = new AirPodsReading(
            ModelId: modelId,
            LeftPercent: leftPercent,
            RightPercent: rightPercent,
            CasePercent: casePercent,
            LeftCharging: leftCharging,
            RightCharging: rightCharging,
            CaseCharging: caseCharging,
            Rssi: rssi,
            SeenAtUtc: DateTimeOffset.UtcNow);

        if (candidate.PrimaryPercent < 0)
        {
            return false;
        }

        reading = candidate;
        return true;
    }

    private static int DecodeNibblePercent(int raw) =>
        raw is >= 0 and <= 10 ? raw * 10 : -1;

    /// <summary>
    /// Converts an Apple Bluetooth Classic PID (e.g. 0x2027 for AirPods Pro 3)
    /// into its big-endian BLE Continuity Model ID (0x2720).
    /// </summary>
    public static ushort PidToContinuityModelId(ushort bluetoothPid) =>
        (ushort)(((bluetoothPid & 0xFF) << 8) | (bluetoothPid >> 8));

    private static bool IsSupportedAudioModel(ushort modelId) => modelId switch
    {
        0x0220 => true, // AirPods (1st Gen)
        0x0F20 => true, // AirPods (2nd Gen)
        0x1320 => true, // AirPods (3rd Gen)
        0x1920 => true, // AirPods 4
        0x1B20 => true, // AirPods 4 (ANC)
        0x0E20 => true, // AirPods Pro (1st Gen)
        0x1420 => true, // AirPods Pro 2 (Lightning)
        0x2420 => true, // AirPods Pro 2 (USB-C)
        0x2720 => true, // AirPods Pro 3
        0x0A20 => true, // AirPods Max (Lightning)
        0x1F20 => true, // AirPods Max (USB-C)
        0x0320 => true, // Powerbeats3
        0x0520 => true, // BeatsX
        0x0620 => true, // Beats Solo3
        0x0920 => true, // Beats Studio3
        0x0B20 => true, // Powerbeats Pro
        0x0C20 => true, // Beats Solo Pro
        0x0D20 => true, // Powerbeats 4
        0x1020 => true, // Beats Flex
        0x1120 => true, // Beats Studio Buds
        0x1220 => true, // Beats Fit Pro
        0x1620 => true, // Beats Studio Buds+
        0x1720 => true, // Beats Studio Pro
        0x1D20 => true, // Powerbeats Pro 2
        _ => (modelId & 0xFF) == 0x20, // Future Apple audio models (0xXX20)
    };

    private static bool IsSingleUnitModel(ushort modelId) =>
        modelId is 0x0A20 or 0x1F20 or 0x0620 or 0x0920 or 0x0C20 or 0x1720;

    public void Dispose()
    {
        lock (_watcherLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_watcher is not null)
            {
                try
                {
                    _watcher.Received -= OnAdvertisementReceived;
                    _watcher.Stop();
                }
                catch
                {
                    // Ignore stop errors
                }

                _watcher = null;
            }
        }
    }
}

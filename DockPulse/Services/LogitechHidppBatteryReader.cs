using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace DockPulse.Services;

/// <summary>
/// Clean-room MIT implementation of the Logitech HID++ 2.0 battery protocol
/// over standard Windows HID APIs (setupapi.dll, hid.dll, kernel32.dll).
/// Supports Logitech LIGHTSPEED (e.g. PRO X2 SUPERSTRIKE, PRO X SUPERLIGHT 2),
/// Logi Bolt, Unifying, wired USB charging, and Bluetooth HID++ devices.
/// </summary>
internal sealed class LogitechHidppBatteryReader
{
    private const ushort LogitechVendorId = 0x046D;
    private const ushort UsagePageHidpp = 0xFF00;
    private const ushort UsagePageHidppBle = 0xFF43;

    private const byte ReportIdShort = 0x10;
    private const byte ReportIdLong = 0x11;
    private const byte SoftwareId = 0x0D; // Distinct from G HUB / Options+ and device event ID 0x00
    private const byte PingSentinel = 0x5A;

    private const ushort FeatureDeviceName = 0x0005;
    private const ushort FeatureBatteryStatus = 0x1000;
    private const ushort FeatureBatteryVoltage = 0x1001;
    private const ushort FeatureAdcMeasurement = 0x1002;
    private const ushort FeatureUnifiedBattery = 0x1004;

    private const int PingTimeoutMs = 120;
    private const int CommandTimeoutMs = 250;
    private static readonly TimeSpan SleepCacheDuration = TimeSpan.FromMinutes(15);

    private readonly object _pollGate = new();
    private readonly ConcurrentDictionary<string, CachedReading> _sleepCache = new(StringComparer.OrdinalIgnoreCase);

    private sealed record CachedReading(string HardwareGroupKey, WirelessDeviceBattery Device, DateTimeOffset SeenAtUtc);

    private sealed record HidCandidate(
        string Path,
        string HardwareGroupKey,
        ushort ProductId,
        int InputLength,
        int OutputLength,
        string ProductName);

    public Task<IReadOnlyList<WirelessDeviceBattery>> GetDevicesAsync()
    {
        return Task.Run<IReadOnlyList<WirelessDeviceBattery>>(() =>
        {
            lock (_pollGate)
            {
                return QueryDevicesSync();
            }
        });
    }

    private List<WirelessDeviceBattery> QueryDevicesSync()
    {
        var found = new List<WirelessDeviceBattery>();
        var foundNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var presentHardwareGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var candidates = EnumerateLogitechHidppCollections();
            foreach (var candidate in candidates)
            {
                presentHardwareGroups.Add(candidate.HardwareGroupKey);
                ProbeCollection(candidate, found, foundNames);
            }

            // Retain recently seen sleeping wireless devices only while their receiver stays connected
            var nowUtc = DateTimeOffset.UtcNow;
            foreach (var entry in _sleepCache)
            {
                if (!presentHardwareGroups.Contains(entry.Value.HardwareGroupKey) ||
                    (nowUtc - entry.Value.SeenAtUtc) > SleepCacheDuration)
                {
                    _sleepCache.TryRemove(entry.Key, out _);
                    continue;
                }

                if (!foundNames.Contains(entry.Value.Device.Name))
                {
                    found.Add(entry.Value.Device);
                    foundNames.Add(entry.Value.Device.Name);
                }
            }
        }
        catch
        {
            // Ignore transient HID enumeration errors
        }

        return found;
    }

    private static List<HidCandidate> EnumerateLogitechHidppCollections()
    {
        var byGroup = new Dictionary<string, HidCandidate>(StringComparer.OrdinalIgnoreCase);

        HidD_GetHidGuid(out Guid hidGuid);
        nint devInfo = SetupDiGetClassDevsW(ref hidGuid, null, 0, DigcfPresent | DigcfDeviceInterface);
        if (devInfo == InvalidHandleValue)
        {
            return [];
        }

        try
        {
            var ifaceData = new SpDeviceInterfaceData
            {
                CbSize = Marshal.SizeOf<SpDeviceInterfaceData>(),
            };

            for (uint index = 0; SetupDiEnumDeviceInterfaces(devInfo, 0, ref hidGuid, index, ref ifaceData); index++)
            {
                _ = SetupDiGetDeviceInterfaceDetailW(devInfo, ref ifaceData, 0, 0, out int requiredSize, 0);
                if (requiredSize <= 4)
                {
                    continue;
                }

                nint detailBuf = Marshal.AllocHGlobal(requiredSize);
                try
                {
                    Marshal.WriteInt32(detailBuf, nint.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetailW(devInfo, ref ifaceData, detailBuf, requiredSize, out _, 0))
                    {
                        continue;
                    }

                    string? path = Marshal.PtrToStringUni(detailBuf + 4);
                    if (string.IsNullOrWhiteSpace(path) ||
                        !path.Contains("046d", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    // Open with zero access first so we never touch standard mouse/keyboard TLCs with R/W
                    nint metaHandle = CreateFileW(
                        path,
                        0,
                        FileShareRead | FileShareWrite,
                        0,
                        OpenExisting,
                        0,
                        0);

                    if (metaHandle == InvalidHandleValue)
                    {
                        continue;
                    }

                    try
                    {
                        var attrs = new HiddAttributes { Size = Marshal.SizeOf<HiddAttributes>() };
                        if (!HidD_GetAttributes(metaHandle, ref attrs) || attrs.VendorId != LogitechVendorId)
                        {
                            continue;
                        }

                        if (!HidD_GetPreparsedData(metaHandle, out nint preparsed) || preparsed == 0)
                        {
                            continue;
                        }

                        HidpCaps caps;
                        try
                        {
                            if (HidP_GetCaps(preparsed, out caps) < 0)
                            {
                                continue;
                            }
                        }
                        finally
                        {
                            _ = HidD_FreePreparsedData(preparsed);
                        }

                        bool isHidppUsage = caps.UsagePage is UsagePageHidpp or UsagePageHidppBle;
                        bool isSupportedReportSize =
                            (caps.OutputReportByteLength == 20 && caps.InputReportByteLength >= 20) ||
                            (caps.OutputReportByteLength == 7 && caps.InputReportByteLength >= 7);

                        if (!isHidppUsage || !isSupportedReportSize)
                        {
                            continue;
                        }

                        string productName = ReadProductString(metaHandle);
                        string groupKey = BuildHardwareGroupKey(path, attrs.ProductId);

                        // Prefer 20-byte Long HID++ 2.0 collection over 7-byte Short collection on the same device
                        if (!byGroup.TryGetValue(groupKey, out var existing) ||
                            caps.OutputReportByteLength > existing.OutputLength)
                        {
                            byGroup[groupKey] = new HidCandidate(
                                Path: path,
                                HardwareGroupKey: groupKey,
                                ProductId: attrs.ProductId,
                                InputLength: caps.InputReportByteLength,
                                OutputLength: caps.OutputReportByteLength,
                                ProductName: productName);
                        }
                    }
                    finally
                    {
                        _ = CloseHandle(metaHandle);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(detailBuf);
                }
            }
        }
        finally
        {
            _ = SetupDiDestroyDeviceInfoList(devInfo);
        }

        return [.. byGroup.Values];
    }

    private void ProbeCollection(
        HidCandidate candidate,
        List<WirelessDeviceBattery> found,
        HashSet<string> foundNames)
    {
        nint handle = CreateFileW(
            candidate.Path,
            GenericRead | GenericWrite,
            FileShareRead | FileShareWrite,
            0,
            OpenExisting,
            FileFlagOverlapped,
            0);

        if (handle == InvalidHandleValue)
        {
            return;
        }

        try
        {
            // Try direct device index (0xFF) first (wired USB or direct Bluetooth HID++ device)
            if (TryReadDeviceBattery(handle, candidate, 0xFF, out var directDevice))
            {
                RecordDevice(candidate.HardwareGroupKey, 0xFF, directDevice, found, foundNames);
                return;
            }

            // Otherwise probe paired wireless receiver slots 1..6 (LIGHTSPEED, Bolt, Unifying)
            for (byte slot = 1; slot <= 6; slot++)
            {
                if (TryReadDeviceBattery(handle, candidate, slot, out var pairedDevice))
                {
                    RecordDevice(candidate.HardwareGroupKey, slot, pairedDevice, found, foundNames);
                }
            }
        }
        finally
        {
            _ = CloseHandle(handle);
        }
    }

    private void RecordDevice(
        string hardwareGroupKey,
        byte deviceIndex,
        WirelessDeviceBattery device,
        List<WirelessDeviceBattery> found,
        HashSet<string> foundNames)
    {
        string cacheKey = $"{hardwareGroupKey}|slot-{deviceIndex:X2}";
        _sleepCache[cacheKey] = new CachedReading(hardwareGroupKey, device, DateTimeOffset.UtcNow);

        if (foundNames.Add(device.Name))
        {
            found.Add(device);
        }
    }

    private static bool TryReadDeviceBattery(
        nint handle,
        HidCandidate candidate,
        byte deviceIndex,
        out WirelessDeviceBattery device)
    {
        device = null!;

        // 1. HID++ 2.0 IRoot (0x00) Func 1: GetProtocolVersion(0, 0, PingSentinel)
        byte[]? ping = Exchange(
            handle,
            candidate.OutputLength,
            candidate.InputLength,
            deviceIndex,
            featureIndex: 0x00,
            functionId: 0x01,
            p0: 0x00,
            p1: 0x00,
            p2: PingSentinel,
            timeoutMs: PingTimeoutMs);

        if (ping is null ||
            ping.Length < 7 ||
            ping[2] != 0x00 ||
            ping[4] < 2 ||
            ping[6] != PingSentinel)
        {
            return false;
        }

        // 2. Read battery level & charging state using the newest battery feature supported
        if (!TryQueryBattery(handle, candidate, deviceIndex, out int percent, out bool isCharging))
        {
            return false;
        }

        // 3. Read device name & hardware device type via Feature 0x0005
        var (deviceName, deviceKind) = QueryDeviceIdentity(handle, candidate, deviceIndex);
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            deviceName = string.IsNullOrWhiteSpace(candidate.ProductName)
                ? "Logitech Wireless Device"
                : candidate.ProductName;
        }

        if (deviceKind == WirelessDeviceKind.Unknown)
        {
            deviceKind = WirelessBatteryService.InferKind(deviceName);
        }

        string connectionType = DescribeConnectionType(candidate, deviceIndex);
        bool wiredOrCharging = isCharging || (deviceIndex == 0xFF && !IsBluetoothPath(candidate.Path));

        device = new WirelessDeviceBattery(
            Id: $"logi-hidpp-{candidate.ProductId:X4}-{deviceIndex:X2}",
            Name: deviceName,
            BatteryPercent: Math.Clamp(percent, 0, 100),
            ConnectionType: connectionType,
            Kind: deviceKind,
            IsWiredOrCharging: wiredOrCharging);

        return true;
    }

    private static bool TryQueryBattery(
        nint handle,
        HidCandidate candidate,
        byte deviceIndex,
        out int percent,
        out bool isCharging)
    {
        percent = -1;
        isCharging = false;

        // Priority 1: Feature 0x1004 — Unified Battery (PRO X2 SUPERSTRIKE, PRO X SUPERLIGHT 2, MX Master 3S, etc.)
        if (TryGetFeatureIndex(handle, candidate, deviceIndex, FeatureUnifiedBattery, out byte unifiedIdx))
        {
            var resp = Exchange(handle, candidate.OutputLength, candidate.InputLength, deviceIndex, unifiedIdx, 0x01, 0, 0, 0, CommandTimeoutMs);
            if (resp is not null && resp.Length >= 8 && resp[2] == unifiedIdx)
            {
                int soc = resp[4];
                byte levelMask = resp[5];
                byte chargingStatus = resp[6];
                byte externalPower = resp[7];

                percent = soc is > 0 and <= 100
                    ? soc
                    : MapUnifiedLevelBitmask(levelMask);

                // 0 = discharging, 1 = charging, 2 = charging slow, 3 = charging complete
                isCharging = chargingStatus is 1 or 2 or 3 || (externalPower != 0 && chargingStatus != 0);

                if (percent is >= 0 and <= 100)
                {
                    return true;
                }
            }
        }

        // Priority 2: Feature 0x1001 — Battery Voltage (G Pro Wireless, G Pro X Superlight 1, G305, G703, G903)
        if (TryGetFeatureIndex(handle, candidate, deviceIndex, FeatureBatteryVoltage, out byte voltageIdx))
        {
            var resp = Exchange(handle, candidate.OutputLength, candidate.InputLength, deviceIndex, voltageIdx, 0x00, 0, 0, 0, CommandTimeoutMs);
            if (resp is not null && resp.Length >= 7 && resp[2] == voltageIdx)
            {
                int millivolts = (resp[4] << 8) | resp[5];
                byte flags = resp[6];
                if (millivolts is >= 2500 and <= 4600)
                {
                    percent = ConvertLiPoMillivoltsToPercent(millivolts);
                    bool externalPower = (flags & 0x80) != 0;
                    int chargeState = flags & 0x07;
                    isCharging = externalPower && chargeState is 0 or 1 or 3;
                    return true;
                }
            }
        }

        // Priority 3: Feature 0x1000 — Battery Status (older Unifying / Bolt office peripherals)
        if (TryGetFeatureIndex(handle, candidate, deviceIndex, FeatureBatteryStatus, out byte statusIdx))
        {
            var resp = Exchange(handle, candidate.OutputLength, candidate.InputLength, deviceIndex, statusIdx, 0x00, 0, 0, 0, CommandTimeoutMs);
            if (resp is not null && resp.Length >= 7 && resp[2] == statusIdx)
            {
                int level = resp[4];
                byte status = resp[6];
                if (level is >= 0 and <= 100)
                {
                    percent = level;
                    isCharging = status is 1 or 2 or 3 or 4;
                    return true;
                }
            }
        }

        // Priority 4: Feature 0x1002 — ADC Measurement (legacy gaming peripherals)
        if (TryGetFeatureIndex(handle, candidate, deviceIndex, FeatureAdcMeasurement, out byte adcIdx))
        {
            var resp = Exchange(handle, candidate.OutputLength, candidate.InputLength, deviceIndex, adcIdx, 0x00, 0, 0, 0, CommandTimeoutMs);
            if (resp is not null && resp.Length >= 7 && resp[2] == adcIdx)
            {
                int millivolts = (resp[4] << 8) | resp[5];
                byte flags = resp[6];
                if (millivolts is >= 2500 and <= 4600)
                {
                    percent = ConvertLiPoMillivoltsToPercent(millivolts);
                    isCharging = (flags & 0x01) != 0;
                    return true;
                }
            }
        }

        return false;
    }

    private static (string Name, WirelessDeviceKind Kind) QueryDeviceIdentity(
        nint handle,
        HidCandidate candidate,
        byte deviceIndex)
    {
        if (!TryGetFeatureIndex(handle, candidate, deviceIndex, FeatureDeviceName, out byte nameIdx))
        {
            return (string.Empty, WirelessDeviceKind.Unknown);
        }

        string name = string.Empty;
        var countResp = Exchange(handle, candidate.OutputLength, candidate.InputLength, deviceIndex, nameIdx, 0x00, 0, 0, 0, CommandTimeoutMs);
        if (countResp is not null && countResp.Length >= 5 && countResp[2] == nameIdx)
        {
            int expectedLength = Math.Min(countResp[4], (byte)64);
            var rawBytes = new List<byte>(expectedLength);

            while (rawBytes.Count < expectedLength)
            {
                var chunkResp = Exchange(
                    handle,
                    candidate.OutputLength,
                    candidate.InputLength,
                    deviceIndex,
                    nameIdx,
                    0x01,
                    (byte)rawBytes.Count,
                    0,
                    0,
                    CommandTimeoutMs);

                if (chunkResp is null || chunkResp.Length <= 4 || chunkResp[2] != nameIdx)
                {
                    break;
                }

                int chunkBytes = Math.Min(expectedLength - rawBytes.Count, chunkResp.Length - 4);
                for (int i = 0; i < chunkBytes; i++)
                {
                    rawBytes.Add(chunkResp[4 + i]);
                }
            }

            if (rawBytes.Count > 0)
            {
                name = Encoding.UTF8.GetString(rawBytes.ToArray()).TrimEnd('\0').Trim();
            }
        }

        WirelessDeviceKind kind = WirelessDeviceKind.Unknown;
        var typeResp = Exchange(handle, candidate.OutputLength, candidate.InputLength, deviceIndex, nameIdx, 0x02, 0, 0, 0, CommandTimeoutMs);
        if (typeResp is not null && typeResp.Length >= 5 && typeResp[2] == nameIdx)
        {
            kind = typeResp[4] switch
            {
                0 or 2 => WirelessDeviceKind.Keyboard,
                3 or 4 or 5 => WirelessDeviceKind.Mouse,
                8 => WirelessDeviceKind.Audio,
                10 or 11 or 12 => WirelessDeviceKind.Controller,
                _ => WirelessDeviceKind.Unknown,
            };
        }

        return (name, kind);
    }

    private static bool TryGetFeatureIndex(
        nint handle,
        HidCandidate candidate,
        byte deviceIndex,
        ushort featureId,
        out byte featureIndex)
    {
        featureIndex = 0;

        var resp = Exchange(
            handle,
            candidate.OutputLength,
            candidate.InputLength,
            deviceIndex,
            featureIndex: 0x00,
            functionId: 0x00,
            p0: (byte)(featureId >> 8),
            p1: (byte)(featureId & 0xFF),
            p2: 0x00,
            timeoutMs: CommandTimeoutMs);

        if (resp is not null && resp.Length >= 5 && resp[2] == 0x00 && resp[4] != 0)
        {
            featureIndex = resp[4];
            return true;
        }

        return false;
    }

    private static byte[]? Exchange(
        nint handle,
        int outputLength,
        int inputLength,
        byte deviceIndex,
        byte featureIndex,
        byte functionId,
        byte p0,
        byte p1,
        byte p2,
        int timeoutMs)
    {
        byte reportId = outputLength >= 20 ? ReportIdLong : ReportIdShort;
        byte funcAndSw = (byte)((functionId << 4) | (SoftwareId & 0x0F));

        byte[] request = new byte[outputLength];
        request[0] = reportId;
        request[1] = deviceIndex;
        request[2] = featureIndex;
        request[3] = funcAndSw;
        request[4] = p0;
        request[5] = p1;
        request[6] = p2;

        if (!WriteReport(handle, request, timeoutMs))
        {
            return null;
        }

        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < timeoutMs)
        {
            int remaining = Math.Max(1, timeoutMs - (int)timer.ElapsedMilliseconds);
            byte[]? response = ReadReport(handle, inputLength, remaining);
            if (response is null || response.Length < 7)
            {
                return null;
            }

            if (response[1] != deviceIndex)
            {
                continue;
            }

            // HID++ 1.0 (0x8F) or HID++ 2.0 (0xFF) error response for our request
            if ((response[2] == 0x8F || response[2] == 0xFF) && response[3] == featureIndex)
            {
                return response;
            }

            // Exact match on feature index, function ID, and our SoftwareId
            if (response[2] == featureIndex && response[3] == funcAndSw)
            {
                return response;
            }
        }

        return null;
    }

    private static bool WriteReport(nint handle, byte[] buffer, int timeoutMs)
    {
        nint ev = CreateEventW(0, true, false, null);
        if (ev == 0)
        {
            return false;
        }

        var ov = new OverlappedData { EventHandle = ev };
        try
        {
            if (WriteFile(handle, buffer, buffer.Length, out _, ref ov))
            {
                return true;
            }

            if (Marshal.GetLastWin32Error() != ErrorIoPending)
            {
                return false;
            }

            if (WaitForSingleObject(ev, (uint)timeoutMs) != WaitObject0)
            {
                _ = CancelIoEx(handle, ref ov);
                return false;
            }

            return GetOverlappedResult(handle, ref ov, out _, false);
        }
        finally
        {
            _ = CloseHandle(ev);
        }
    }

    private static byte[]? ReadReport(nint handle, int length, int timeoutMs)
    {
        nint ev = CreateEventW(0, true, false, null);
        if (ev == 0)
        {
            return null;
        }

        var buffer = new byte[length];
        var ov = new OverlappedData { EventHandle = ev };
        try
        {
            if (ReadFile(handle, buffer, buffer.Length, out int readNow, ref ov))
            {
                return readNow >= 7 ? buffer : null;
            }

            if (Marshal.GetLastWin32Error() != ErrorIoPending)
            {
                return null;
            }

            if (WaitForSingleObject(ev, (uint)timeoutMs) != WaitObject0)
            {
                _ = CancelIoEx(handle, ref ov);
                return null;
            }

            return GetOverlappedResult(handle, ref ov, out int transferred, false) && transferred >= 7
                ? buffer
                : null;
        }
        finally
        {
            _ = CloseHandle(ev);
        }
    }

    private static int MapUnifiedLevelBitmask(byte mask)
    {
        if ((mask & 0x08) != 0)
        {
            return 100; // Full
        }

        if ((mask & 0x04) != 0)
        {
            return 65; // Good
        }

        if ((mask & 0x02) != 0)
        {
            return 20; // Low
        }

        if ((mask & 0x01) != 0)
        {
            return 5; // Critical
        }

        return -1;
    }

    private static int ConvertLiPoMillivoltsToPercent(int millivolts)
    {
        // Standard single-cell Li-Po discharge curve (mV -> %)
        ReadOnlySpan<(int Mv, int Pct)> curve =
        [
            (4186, 100),
            (4115, 95),
            (4060, 90),
            (3980, 80),
            (3910, 70),
            (3850, 60),
            (3810, 50),
            (3780, 40),
            (3750, 30),
            (3710, 20),
            (3650, 10),
            (3580, 5),
            (3500, 0),
        ];

        if (millivolts >= curve[0].Mv)
        {
            return 100;
        }

        if (millivolts <= curve[^1].Mv)
        {
            return 0;
        }

        for (int i = 0; i < curve.Length - 1; i++)
        {
            var high = curve[i];
            var low = curve[i + 1];
            if (millivolts <= high.Mv && millivolts >= low.Mv)
            {
                double t = (double)(millivolts - low.Mv) / (high.Mv - low.Mv);
                return (int)Math.Round(low.Pct + (t * (high.Pct - low.Pct)));
            }
        }

        return 0;
    }

    private static string DescribeConnectionType(HidCandidate candidate, byte deviceIndex)
    {
        if (IsBluetoothPath(candidate.Path))
        {
            return "Bluetooth (Logitech HID++)";
        }

        if (deviceIndex == 0xFF)
        {
            return "Logitech USB";
        }

        return candidate.ProductId switch
        {
            0xC539 or 0xC53A or 0xC53D or 0xC53F or 0xC541 or 0xC545 or 0xC547 or 0xC54D => "Logitech LIGHTSPEED",
            0xC548 => "Logi Bolt",
            0xC52B or 0xC532 => "Logitech Unifying",
            _ => "Logitech Wireless",
        };
    }

    private static bool IsBluetoothPath(string path) =>
        path.Contains("bthle", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("00001124", StringComparison.OrdinalIgnoreCase);

    private static string BuildHardwareGroupKey(string path, ushort productId)
    {
        // Strip "&colXX" so Col01 (short) and Col02 (long) on the same USB interface group together
        int colIdx = path.IndexOf("&col", StringComparison.OrdinalIgnoreCase);
        if (colIdx > 0)
        {
            int hashIdx = path.IndexOf('#', colIdx);
            if (hashIdx > colIdx)
            {
                return $"{productId:X4}|{path[..colIdx]}{path[hashIdx..]}";
            }
        }

        return $"{productId:X4}|{path}";
    }

    private static string ReadProductString(nint handle)
    {
        byte[] buf = new byte[256];
        if (!HidD_GetProductString(handle, buf, buf.Length))
        {
            return string.Empty;
        }

        string text = Encoding.Unicode.GetString(buf);
        int nullIdx = text.IndexOf('\0');
        return (nullIdx >= 0 ? text[..nullIdx] : text).Trim();
    }

    private const uint DigcfPresent = 0x02;
    private const uint DigcfDeviceInterface = 0x10;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x01;
    private const uint FileShareWrite = 0x02;
    private const uint OpenExisting = 3;
    private const uint FileFlagOverlapped = 0x40000000;
    private const int ErrorIoPending = 997;
    private const uint WaitObject0 = 0;
    private static readonly nint InvalidHandleValue = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public int CbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public nint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HiddAttributes
    {
        public int Size;
        public ushort VendorId;
        public ushort ProductId;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HidpCaps
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        public ushort Reserved00;
        public ushort Reserved01;
        public ushort Reserved02;
        public ushort Reserved03;
        public ushort Reserved04;
        public ushort Reserved05;
        public ushort Reserved06;
        public ushort Reserved07;
        public ushort Reserved08;
        public ushort Reserved09;
        public ushort Reserved10;
        public ushort Reserved11;
        public ushort Reserved12;
        public ushort Reserved13;
        public ushort Reserved14;
        public ushort Reserved15;
        public ushort Reserved16;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OverlappedData
    {
        public nint Internal;
        public nint InternalHigh;
        public int Offset;
        public int OffsetHigh;
        public nint EventHandle;
    }

    [DllImport("hid.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern void HidD_GetHidGuid(out Guid hidGuid);

    [DllImport("hid.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetAttributes(nint hidDeviceObject, ref HiddAttributes attributes);

    [DllImport("hid.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetPreparsedData(nint hidDeviceObject, out nint preparsedData);

    [DllImport("hid.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_FreePreparsedData(nint preparsedData);

    [DllImport("hid.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int HidP_GetCaps(nint preparsedData, out HidpCaps capabilities);

    [DllImport("hid.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetProductString(nint hidDeviceObject, [Out] byte[] buffer, int bufferLength);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint SetupDiGetClassDevsW(ref Guid classGuid, string? enumerator, nint hwndParent, uint flags);

    [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(
        nint deviceInfoSet,
        nint deviceInfoData,
        ref Guid interfaceClassGuid,
        uint memberIndex,
        ref SpDeviceInterfaceData deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(
        nint deviceInfoSet,
        ref SpDeviceInterfaceData deviceInterfaceData,
        nint deviceInterfaceDetailData,
        int deviceInterfaceDetailDataSize,
        out int requiredSize,
        nint deviceInfoData);

    [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(nint deviceInfoSet);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteFile(
        nint fileHandle,
        [In] byte[] buffer,
        int bytesToWrite,
        out int bytesWritten,
        ref OverlappedData overlapped);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadFile(
        nint fileHandle,
        [Out] byte[] buffer,
        int bytesToRead,
        out int bytesRead,
        ref OverlappedData overlapped);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint CreateEventW(
        nint eventAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool manualReset,
        [MarshalAs(UnmanagedType.Bool)] bool initialState,
        string? name);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint WaitForSingleObject(nint handle, uint milliseconds);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOverlappedResult(
        nint fileHandle,
        ref OverlappedData overlapped,
        out int numberOfBytesTransferred,
        [MarshalAs(UnmanagedType.Bool)] bool wait);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CancelIoEx(nint fileHandle, ref OverlappedData overlapped);
}

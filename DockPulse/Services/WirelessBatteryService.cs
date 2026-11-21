using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace DockPulse.Services;

public enum WirelessDeviceKind
{
    Unknown,
    Mouse,
    Keyboard,
    Audio,
    Controller,
}

public sealed record WirelessDeviceBattery(
    string Id,
    string Name,
    int BatteryPercent,
    string ConnectionType,
    WirelessDeviceKind Kind,
    bool IsWiredOrCharging = false,
    string? DetailText = null);

public sealed class WirelessBatteryService : IDisposable
{
    // Windows PnP property key DEVPKEY_Bluetooth_Battery ({104EA319-6EE2-4701-BD47-8DDBF425BBE5}, 2)
    private const string DevPkeyBluetoothBattery = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";

    // Windows PnP property key DEVPKEY_Device_IsConnected ({83DA6326-97A6-4088-9453-A1923F573B29}, 15)
    private const string DevPkeyDeviceIsConnected = "{83DA6326-97A6-4088-9453-A1923F573B29} 15";

    private const string PropContainerId = "System.Devices.ContainerId";
    private const string PropFriendlyName = "System.Devices.FriendlyName";
    private const string PropItemName = "System.ItemNameDisplay";
    private const string PropDeviceInstanceId = "System.Devices.DeviceInstanceId";

    private readonly LogitechHidppBatteryReader _logitechReader = new();
    private readonly AppleAirPodsBleReader _appleAirPodsReader = new();

    private sealed record ConnectedAppleDevice(string ContainerId, string Name, ushort ExpectedModelId);

    public WirelessBatteryService()
    {
        // Start hardware-filtered passive BLE watcher (0x004C) early so AirPods broadcasts are ready immediately
        _appleAirPodsReader.EnsureStarted();
    }

    public async Task<IReadOnlyList<WirelessDeviceBattery>> GetDevicesAsync(bool forceUncachedGatt = false)
    {
        _appleAirPodsReader.EnsureStarted();

        var byContainer = new Dictionary<string, WirelessDeviceBattery>(StringComparer.OrdinalIgnoreCase);

        // 0. Discover currently CONNECTED Bluetooth Classic & BLE containers (prevents stale disconnected ghost batteries)
        var connectedContainers = await GetConnectedBluetoothContainersAsync().ConfigureAwait(false);

        // 1. Query Windows PnP Bluetooth / BLE battery levels (filtered strictly to connected devices)
        var connectedAppleDevices = await QueryPnpBluetoothBatteriesAsync(byContainer, connectedContainers).ConfigureAwait(false);

        // 2. Query Apple AirPods / Beats BLE Continuity broadcasts (L/R/Case % for AirPods Pro 3, Pro 2, etc.)
        await QueryAppleAirPodsBleAsync(byContainer, connectedAppleDevices, forceUncachedGatt).ConfigureAwait(false);

        // 3. Fallback: Query connected BLE GATT Battery Service (0x180F / 0x2A19)
        await QueryBleGattBatteriesAsync(byContainer, forceUncachedGatt).ConfigureAwait(false);

        // 4. Query Logitech Wireless / LIGHTSPEED / Bolt / Unifying / USB HID++ 2.0 devices
        await QueryLogitechHidppDevicesAsync(byContainer).ConfigureAwait(false);

        // 5. Query Xbox / XInput controllers
        QueryXInputControllers(byContainer);

        return byContainer.Values
            .OrderBy(d => d.BatteryPercent)
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static async Task<HashSet<string>> GetConnectedBluetoothContainersAsync()
    {
        var connected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string[] props = [PropContainerId];

        try
        {
            string classicSelector = BluetoothDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected);
            var classicDevices = await DeviceInformation.FindAllAsync(classicSelector, props);
            foreach (var dev in classicDevices)
            {
                if (TryGetGuidString(dev.Properties, PropContainerId, out var cid))
                {
                    connected.Add(cid);
                }
            }
        }
        catch
        {
            // Classic Bluetooth radio unavailable
        }

        try
        {
            string bleSelector = BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected);
            var bleDevices = await DeviceInformation.FindAllAsync(bleSelector, props);
            foreach (var dev in bleDevices)
            {
                if (TryGetGuidString(dev.Properties, PropContainerId, out var cid))
                {
                    connected.Add(cid);
                }
            }
        }
        catch
        {
            // BLE radio unavailable
        }

        return connected;
    }

    private static async Task<List<ConnectedAppleDevice>> QueryPnpBluetoothBatteriesAsync(
        Dictionary<string, WirelessDeviceBattery> results,
        HashSet<string> connectedContainers)
    {
        var connectedApple = new Dictionary<string, ConnectedAppleDevice>(StringComparer.OrdinalIgnoreCase);

        try
        {
            string[] requestedProperties =
            [
                DevPkeyBluetoothBattery,
                DevPkeyDeviceIsConnected,
                PropContainerId,
                PropFriendlyName,
                PropItemName,
                PropDeviceInstanceId,
            ];

            const string aqs = "System.Devices.Present:=System.StructuredQueryType.Boolean#True";
            var devices = await DeviceInformation.FindAllAsync(aqs, requestedProperties, DeviceInformationKind.Device);

            var containerNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var containerAppleModelIds = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase);

            foreach (var dev in devices)
            {
                var instanceId = dev.Properties.TryGetValue(PropDeviceInstanceId, out var inst)
                    ? inst?.ToString() ?? string.Empty
                    : string.Empty;

                if (!TryGetGuidString(dev.Properties, PropContainerId, out var containerId))
                {
                    continue;
                }

                // Mark container as connected if any PnP Bluetooth node in it reports DEVPKEY_Device_IsConnected == true
                if (IsDeviceConnectedFlagTrue(dev.Properties) &&
                    (instanceId.StartsWith("BTHENUM\\", StringComparison.OrdinalIgnoreCase) ||
                     instanceId.StartsWith("BTHLE", StringComparison.OrdinalIgnoreCase)))
                {
                    connectedContainers.Add(containerId);
                }

                // Extract Apple Bluetooth PID (e.g. VID&0001004c_PID&2027 -> AirPods Pro 3 -> 0x2720)
                if (TryParseAppleBluetoothPid(instanceId, out ushort applePid))
                {
                    containerAppleModelIds[containerId] = AppleAirPodsBleReader.PidToContinuityModelId(applePid);
                }

                var candidateName = GetBestName(dev);
                if (!string.IsNullOrWhiteSpace(candidateName) &&
                    !candidateName.Contains("Hands-Free", StringComparison.OrdinalIgnoreCase) &&
                    !candidateName.Contains("Avrcp", StringComparison.OrdinalIgnoreCase) &&
                    !candidateName.Contains("GATT", StringComparison.OrdinalIgnoreCase) &&
                    !candidateName.Contains("HID", StringComparison.OrdinalIgnoreCase) &&
                    !candidateName.Contains("AAP Server", StringComparison.OrdinalIgnoreCase) &&
                    !candidateName.Contains("UARPS", StringComparison.OrdinalIgnoreCase) &&
                    !candidateName.Contains("Service Discovery", StringComparison.OrdinalIgnoreCase) &&
                    !containerNames.ContainsKey(containerId))
                {
                    containerNames[containerId] = CleanDeviceName(candidateName);
                }
            }

            // Collect currently connected Apple audio containers (so AirPods Pro 3 / Pro 2 etc. can match BLE broadcasts)
            foreach (var cid in connectedContainers)
            {
                bool hasApplePid = containerAppleModelIds.TryGetValue(cid, out ushort modelId);
                containerNames.TryGetValue(cid, out var friendlyName);

                if (hasApplePid ||
                    (!string.IsNullOrWhiteSpace(friendlyName) &&
                     (friendlyName.Contains("AirPods", StringComparison.OrdinalIgnoreCase) ||
                      friendlyName.Contains("Beats", StringComparison.OrdinalIgnoreCase))))
                {
                    string displayName = !string.IsNullOrWhiteSpace(friendlyName)
                        ? friendlyName
                        : "AirPods";
                    connectedApple[cid] = new ConnectedAppleDevice(cid, displayName, modelId);
                }
            }

            foreach (var dev in devices)
            {
                if (!dev.Properties.TryGetValue(DevPkeyBluetoothBattery, out var rawBattery) || rawBattery is null)
                {
                    continue;
                }

                var key = TryGetGuidString(dev.Properties, PropContainerId, out var cid) ? cid : dev.Id;

                // CRITICAL: Skip disconnected paired devices that retain stale cached PnP battery levels
                bool isConnected = IsDeviceConnectedFlagTrue(dev.Properties) || connectedContainers.Contains(key);
                if (!isConnected)
                {
                    continue;
                }

                int percent = rawBattery switch
                {
                    byte b => b,
                    sbyte sb => sb,
                    int i => i,
                    uint ui => (int)ui,
                    _ => -1,
                };

                if (percent < 0 || percent > 100)
                {
                    continue;
                }

                var name = containerNames.TryGetValue(key, out var mappedName)
                    ? mappedName
                    : CleanDeviceName(GetBestName(dev));

                var instanceId = dev.Properties.TryGetValue(PropDeviceInstanceId, out var inst)
                    ? inst?.ToString() ?? string.Empty
                    : string.Empty;

                var connType = instanceId.Contains("BTHLE", StringComparison.OrdinalIgnoreCase)
                    ? "Bluetooth LE"
                    : "Bluetooth";

                results[key] = new WirelessDeviceBattery(
                    Id: key,
                    Name: name,
                    BatteryPercent: percent,
                    ConnectionType: connType,
                    Kind: InferKind(name));
            }
        }
        catch
        {
            // Ignore when Bluetooth radio is unavailable
        }

        return [.. connectedApple.Values];
    }

    private async Task QueryAppleAirPodsBleAsync(
        Dictionary<string, WirelessDeviceBattery> results,
        List<ConnectedAppleDevice> connectedAppleDevices,
        bool forceUncached)
    {
        if (connectedAppleDevices.Count == 0)
        {
            return;
        }

        try
        {
            bool allowFallback = connectedAppleDevices.Count == 1;

            // If any connected Apple audio device hasn't had a BLE Continuity packet captured yet
            // (e.g. right after startup or manual refresh), wait briefly for the next broadcast
            bool missingAny = connectedAppleDevices.Any(
                a => _appleAirPodsReader.TryGetFreshReading(a.ExpectedModelId, allowFallback) is null);

            if (missingAny)
            {
                int maxSteps = forceUncached ? 10 : 8; // up to 2.5s on manual refresh, 2.0s on startup/poll
                for (int step = 0; step < maxSteps; step++)
                {
                    await Task.Delay(250).ConfigureAwait(false);
                    if (connectedAppleDevices.All(
                            a => _appleAirPodsReader.TryGetFreshReading(a.ExpectedModelId, allowFallback) is not null))
                    {
                        break;
                    }
                }
            }

            foreach (var apple in connectedAppleDevices)
            {
                var reading = _appleAirPodsReader.TryGetFreshReading(apple.ExpectedModelId, allowFallback);
                if (reading is null || reading.PrimaryPercent < 0)
                {
                    continue;
                }

                string detail = reading.FormatDetailText();
                results[apple.ContainerId] = new WirelessDeviceBattery(
                    Id: apple.ContainerId,
                    Name: apple.Name,
                    BatteryPercent: reading.PrimaryPercent,
                    ConnectionType: "Apple BLE",
                    Kind: WirelessDeviceKind.Audio,
                    IsWiredOrCharging: reading.IsCharging,
                    DetailText: string.IsNullOrWhiteSpace(detail) ? null : detail);
            }
        }
        catch
        {
            // Ignore BLE watcher read errors
        }
    }

    private async Task QueryLogitechHidppDevicesAsync(Dictionary<string, WirelessDeviceBattery> results)
    {
        try
        {
            var logiDevices = await _logitechReader.GetDevicesAsync().ConfigureAwait(false);
            foreach (var device in logiDevices)
            {
                // Avoid duplicate entry if the same Logitech device also reported over Bluetooth PnP/GATT
                string? existingKey = null;
                foreach (var kvp in results)
                {
                    if (string.Equals(kvp.Value.Name, device.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        existingKey = kvp.Key;
                        break;
                    }
                }

                if (existingKey is not null)
                {
                    results.Remove(existingKey);
                }

                results[device.Id] = device;
            }
        }
        catch
        {
            // Ignore HID++ query errors
        }
    }

    private static async Task QueryBleGattBatteriesAsync(
        Dictionary<string, WirelessDeviceBattery> results,
        bool forceUncached)
    {
        try
        {
            string selector = BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected);
            string[] props = [PropContainerId];
            var bleInfos = await DeviceInformation.FindAllAsync(selector, props);

            foreach (var info in bleInfos)
            {
                var key = TryGetGuidString(info.Properties, PropContainerId, out var cid) ? cid : info.Id;
                if (results.ContainsKey(key))
                {
                    continue;
                }

                try
                {
                    using var bleDevice = await BluetoothLEDevice.FromIdAsync(info.Id);
                    if (bleDevice is null)
                    {
                        continue;
                    }

                    var cacheMode = forceUncached ? BluetoothCacheMode.Uncached : BluetoothCacheMode.Cached;
                    var serviceResult = await bleDevice.GetGattServicesForUuidAsync(GattServiceUuids.Battery, cacheMode);
                    if (serviceResult.Status != GattCommunicationStatus.Success || serviceResult.Services.Count == 0)
                    {
                        continue;
                    }

                    using var batteryService = serviceResult.Services[0];
                    var charResult = await batteryService.GetCharacteristicsForUuidAsync(GattCharacteristicUuids.BatteryLevel, cacheMode);
                    if (charResult.Status != GattCommunicationStatus.Success || charResult.Characteristics.Count == 0)
                    {
                        continue;
                    }

                    var readResult = await charResult.Characteristics[0].ReadValueAsync(cacheMode);
                    if (readResult.Status != GattCommunicationStatus.Success || readResult.Value is null || readResult.Value.Length == 0)
                    {
                        continue;
                    }

                    using var reader = DataReader.FromBuffer(readResult.Value);
                    int percent = reader.ReadByte();
                    if (percent is >= 0 and <= 100)
                    {
                        var name = CleanDeviceName(string.IsNullOrWhiteSpace(bleDevice.Name) ? info.Name : bleDevice.Name);
                        results[key] = new WirelessDeviceBattery(
                            Id: key,
                            Name: name,
                            BatteryPercent: percent,
                            ConnectionType: "Bluetooth LE (GATT)",
                            Kind: InferKind(name));
                    }
                }
                catch
                {
                    // Skip BLE devices that reject GATT reads
                }
            }
        }
        catch
        {
            // Ignore when BLE is unavailable
        }
    }

    private static void QueryXInputControllers(Dictionary<string, WirelessDeviceBattery> results)
    {
        try
        {
            for (uint i = 0; i < 4; i++)
            {
                if (XInputGetBatteryInformation(i, BatteryDevTypeGamepad, out var info) != 0)
                {
                    continue;
                }

                if (info.BatteryType == BatteryTypeDisconnected)
                {
                    continue;
                }

                bool isWired = info.BatteryType == BatteryTypeWired;
                int percent = isWired
                    ? 100
                    : info.BatteryLevel switch
                    {
                        0 => 10,
                        1 => 30,
                        2 => 65,
                        3 => 100,
                        _ => 0,
                    };

                var key = $"xinput-controller-{i}";
                results[key] = new WirelessDeviceBattery(
                    Id: key,
                    Name: $"Xbox Controller #{i + 1}",
                    BatteryPercent: percent,
                    ConnectionType: isWired ? "XInput (Wired)" : "XInput (Wireless)",
                    Kind: WirelessDeviceKind.Controller,
                    IsWiredOrCharging: isWired);
            }
        }
        catch
        {
            // xinput1_4.dll unavailable
        }
    }

    private static bool IsDeviceConnectedFlagTrue(IReadOnlyDictionary<string, object> props) =>
        props.TryGetValue(DevPkeyDeviceIsConnected, out var raw) && raw is bool b && b;

    private static bool TryParseAppleBluetoothPid(string instanceId, out ushort pid)
    {
        pid = 0;
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return false;
        }

        // Matches Apple Bluetooth PnP IDs such as "VID&0001004c_PID&2027"
        int marker = instanceId.IndexOf("004c_pid&", StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
        {
            return false;
        }

        int hexStart = marker + "004c_pid&".Length;
        if (hexStart + 4 > instanceId.Length)
        {
            return false;
        }

        return ushort.TryParse(
            instanceId.AsSpan(hexStart, 4),
            NumberStyles.HexNumber,
            CultureInfo.InvariantCulture,
            out pid);
    }

    private static bool TryGetGuidString(IReadOnlyDictionary<string, object> props, string key, out string value)
    {
        if (props.TryGetValue(key, out var raw) && raw is not null)
        {
            var str = raw.ToString();
            if (!string.IsNullOrWhiteSpace(str) && str != Guid.Empty.ToString())
            {
                value = str;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    private static string GetBestName(DeviceInformation dev)
    {
        if (dev.Properties.TryGetValue(PropFriendlyName, out var fn) && fn is string s1 && !string.IsNullOrWhiteSpace(s1))
        {
            return s1;
        }

        if (!string.IsNullOrWhiteSpace(dev.Name))
        {
            return dev.Name;
        }

        if (dev.Properties.TryGetValue(PropItemName, out var item) && item is string s2 && !string.IsNullOrWhiteSpace(s2))
        {
            return s2;
        }

        return "Wireless Device";
    }

    private static string CleanDeviceName(string name)
    {
        var cleaned = name
            .Replace(" Hands-Free AG", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace(" Hands-Free", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace(" Stereo", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();

        return string.IsNullOrWhiteSpace(cleaned) ? "Wireless Device" : cleaned;
    }

    internal static WirelessDeviceKind InferKind(string name)
    {
        if (name.Contains("mouse", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("mx master", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("mx anywhere", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("trackpad", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("viper", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("deathadder", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("superlight", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("superstrike", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("pro x2", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("g pro", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("g502", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("g305", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("g703", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("g903", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("basilisk", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("aerox", StringComparison.OrdinalIgnoreCase))
        {
            return WirelessDeviceKind.Mouse;
        }

        if (name.Contains("keyboard", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("mx keys", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("keychron", StringComparison.OrdinalIgnoreCase))
        {
            return WirelessDeviceKind.Keyboard;
        }

        if (name.Contains("head", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("bud", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("pod", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("beats", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("wh-1000", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("wf-1000", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("bose", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("audio", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("speaker", StringComparison.OrdinalIgnoreCase))
        {
            return WirelessDeviceKind.Audio;
        }

        if (name.Contains("xbox", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("controller", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("dualsense", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("gamepad", StringComparison.OrdinalIgnoreCase))
        {
            return WirelessDeviceKind.Controller;
        }

        return WirelessDeviceKind.Unknown;
    }

    public static string GetDeviceGlyph(WirelessDeviceKind kind) => kind switch
    {
        WirelessDeviceKind.Mouse => "\uE962",
        WirelessDeviceKind.Keyboard => "\uE765",
        WirelessDeviceKind.Audio => "\uE7F6",
        WirelessDeviceKind.Controller => "\uE7FC",
        _ => "\uE702",
    };

    public static string GetBatteryGlyph(int percent, bool charging = false)
    {
        if (charging)
        {
            return "\uE83E";
        }

        return percent switch
        {
            >= 95 => "\uE83F",
            >= 85 => "\uE859",
            >= 75 => "\uE858",
            >= 65 => "\uE857",
            >= 55 => "\uE856",
            >= 45 => "\uE855",
            >= 35 => "\uE854",
            >= 25 => "\uE853",
            >= 15 => "\uE852",
            >= 5 => "\uE851",
            _ => "\uE850",
        };
    }

    public void Dispose()
    {
        _appleAirPodsReader.Dispose();
    }

    private const byte BatteryDevTypeGamepad = 0x00;
    private const byte BatteryTypeDisconnected = 0x00;
    private const byte BatteryTypeWired = 0x01;

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputBatteryInformation
    {
        public byte BatteryType;
        public byte BatteryLevel;
    }

    [DllImport("xinput1_4.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint XInputGetBatteryInformation(
        uint dwUserIndex,
        byte devType,
        out XInputBatteryInformation pBatteryInformation);
}

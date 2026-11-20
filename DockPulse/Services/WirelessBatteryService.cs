using System;
using System.Collections.Generic;
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
    bool IsWiredOrCharging = false);

public sealed class WirelessBatteryService
{
    // Windows PnP property key DEVPKEY_Bluetooth_Battery ({104EA319-6EE2-4701-BD47-8DDBF425BBE5}, 2)
    private const string DevPkeyBluetoothBattery = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
    private const string PropContainerId = "System.Devices.ContainerId";
    private const string PropFriendlyName = "System.Devices.FriendlyName";
    private const string PropItemName = "System.ItemNameDisplay";
    private const string PropDeviceInstanceId = "System.Devices.DeviceInstanceId";

    public async Task<IReadOnlyList<WirelessDeviceBattery>> GetDevicesAsync(bool forceUncachedGatt = false)
    {
        var byContainer = new Dictionary<string, WirelessDeviceBattery>(StringComparer.OrdinalIgnoreCase);

        // 1. Query Windows PnP Bluetooth / BLE battery levels
        await QueryPnpBluetoothBatteriesAsync(byContainer).ConfigureAwait(false);

        // 2. Fallback: Query connected BLE GATT Battery Service (0x180F / 0x2A19)
        await QueryBleGattBatteriesAsync(byContainer, forceUncachedGatt).ConfigureAwait(false);

        // 3. Query Xbox / XInput controllers
        QueryXInputControllers(byContainer);

        return byContainer.Values
            .OrderBy(d => d.BatteryPercent)
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static async Task QueryPnpBluetoothBatteriesAsync(Dictionary<string, WirelessDeviceBattery> results)
    {
        try
        {
            string[] requestedProperties =
            [
                DevPkeyBluetoothBattery,
                PropContainerId,
                PropFriendlyName,
                PropItemName,
                PropDeviceInstanceId,
            ];

            const string aqs = "System.Devices.Present:=System.StructuredQueryType.Boolean#True";
            var devices = await DeviceInformation.FindAllAsync(aqs, requestedProperties, DeviceInformationKind.Device);

            var containerNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dev in devices)
            {
                if (!TryGetGuidString(dev.Properties, PropContainerId, out var containerId))
                {
                    continue;
                }

                var candidateName = GetBestName(dev);
                if (!string.IsNullOrWhiteSpace(candidateName) &&
                    !candidateName.Contains("Hands-Free", StringComparison.OrdinalIgnoreCase) &&
                    !candidateName.Contains("Avrcp", StringComparison.OrdinalIgnoreCase) &&
                    !candidateName.Contains("GATT", StringComparison.OrdinalIgnoreCase) &&
                    !candidateName.Contains("HID", StringComparison.OrdinalIgnoreCase) &&
                    !containerNames.ContainsKey(containerId))
                {
                    containerNames[containerId] = candidateName;
                }
            }

            foreach (var dev in devices)
            {
                if (!dev.Properties.TryGetValue(DevPkeyBluetoothBattery, out var rawBattery) || rawBattery is null)
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

                var key = TryGetGuidString(dev.Properties, PropContainerId, out var cid) ? cid : dev.Id;
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

    private static WirelessDeviceKind InferKind(string name)
    {
        if (name.Contains("mouse", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("mx master", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("mx anywhere", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("trackpad", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("viper", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("deathadder", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("superlight", StringComparison.OrdinalIgnoreCase))
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
    private static extern uint XInputGetBatteryInformation(
        uint dwUserIndex,
        byte devType,
        out XInputBatteryInformation pBatteryInformation);
}
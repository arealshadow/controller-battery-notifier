using System.Runtime.InteropServices.WindowsRuntime;
using ControllerBatteryNotifier.Models;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;

namespace ControllerBatteryNotifier.Services;

/// <summary>
/// Reads battery levels from all paired Bluetooth devices that expose the standard
/// GATT Battery Service (UUID 0x180F, characteristic 0x2A19).
///
/// Modern Bluetooth game controllers connected via Bluetooth expose this service,
/// which is the same mechanism the Windows 11 Settings "Bluetooth" page uses to show
/// controller battery.
///
/// Uses the modern WinRT GATT API (<see cref="GattDeviceService"/>), which is what
/// the .NET 9 WinRT projection exposes. The legacy AEP-based enumeration that the
/// old .NET Framework build relied on no longer finds these devices on Windows 11
/// 24H2, so devices are discovered through a GATT selector for the Battery service.
///
/// Note: a controller that is in sleep mode does not answer GATT reads (the read
/// comes back Unreachable). Reads are therefore retried a few times with a short
/// delay; once the user wakes the controller (any button press), the next poll
/// succeeds.
/// </summary>
public sealed class BluetoothBatterySource : IDisposable
{
    private sealed class GattBattery
    {
        public required BatteryDevice Device { get; init; }
        public required GattDeviceService Service { get; init; }
        public required GattCharacteristic Characteristic { get; init; }
        public int LastLevel { get; set; } = -1;
        public bool Subscribed { get; set; }
    }

    private readonly List<GattBattery> _batteries = new();
    private readonly object _sync = new();

    /// <summary>Raised (on a thread-pool thread) when a device reports a new level.</summary>
    public event Action<BatteryDevice, int>? LevelChanged;

    public string Name => "Bluetooth (GATT)";

    public async Task<IReadOnlyList<BatteryDevice>> ReadAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // 1. Enumerate all devices that expose the Battery service (0x180F).
        var selector = GattDeviceService.GetDeviceSelectorFromUuid(GattServiceUuids.Battery);
        var deviceInfos = await DeviceInformation.FindAllAsync(selector);

        var found = new List<BatteryDevice>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var info in deviceInfos)
        {
            if (ct.IsCancellationRequested) break;

            var battery = await TryAttachAsync(info, ct);
            if (battery == null) continue;

            await ReadWithRetryAsync(battery, ct);

            found.Add(battery.Device);
            seenIds.Add(battery.Device.Id);
        }

        // 2. Drop subscriptions for devices that disappeared.
        lock (_sync)
        {
            var removed = _batteries.Where(b => !seenIds.Contains(b.Device.Id)).ToList();
            foreach (var b in removed)
            {
                _batteries.Remove(b);
                try { b.Service.Dispose(); } catch { /* ignore */ }
            }
        }

        return found;
    }

    /// <summary>
    /// Opens the device's Battery service and locates the Battery Level
    /// characteristic. Reuses an already-attached entry so notification
    /// subscriptions stay alive across polls.
    /// </summary>
    private async Task<GattBattery?> TryAttachAsync(DeviceInformation info, CancellationToken ct)
    {
        lock (_sync)
        {
            var existing = _batteries.FirstOrDefault(b => b.Device.Id == info.Id);
            if (existing != null) return existing;
        }

        GattDeviceService? service = null;
        GattBattery? battery = null;
        try
        {
            // The WinRT projection has no CancellationToken overloads, so each
            // call is raced against a timeout (a sleeping controller would
            // otherwise hang the poll forever).
            var fromTask = GattDeviceService.FromIdAsync(info.Id, GattSharingMode.SharedReadOnly).AsTask();
            if (await Task.WhenAny(fromTask, Task.Delay(15000)) != fromTask) return null;
            try { service = await fromTask; } catch { return null; }
            if (service == null) return null;

            var openTask = service.OpenAsync(GattSharingMode.SharedReadOnly).AsTask();
            if (await Task.WhenAny(openTask, Task.Delay(15000)) != openTask) { service.Dispose(); return null; }
            GattOpenStatus openStatus;
            try { openStatus = await openTask; } catch { service.Dispose(); return null; }
            if (openStatus is not (GattOpenStatus.Success or GattOpenStatus.AlreadyOpened))
            {
                service.Dispose();
                return null;
            }

            var lookup = await service.GetCharacteristicsForUuidAsync(GattCharacteristicUuids.BatteryLevel);
            if (lookup.Status != GattCommunicationStatus.Success || lookup.Characteristics.Count < 1)
            {
                service.Dispose();
                return null;
            }

            var characteristic = lookup.Characteristics[0];

            battery = new GattBattery
            {
                Service = service,
                Characteristic = characteristic,
                Device = new BatteryDevice
                {
                    Id = info.Id,
                    Name = string.IsNullOrWhiteSpace(info.Name) ? ShortenId(info.Id) : info.Name,
                    Source = Name,
                    Level = null,
                },
            };

            // Subscribe to live updates (best effort — not all devices notify).
            try
            {
                var subTask = characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask();
                if (await Task.WhenAny(subTask, Task.Delay(10000)) == subTask)
                    await subTask;
                // (a timed-out write just means no live notifications — polling covers it)

                characteristic.ValueChanged += (_, e) =>
                {
                    var level = SafeLevel(e.CharacteristicValue);
                    if (level is >= 0 and <= 100)
                    {
                        battery.LastLevel = level;
                        battery.Device.Level = level;
                        battery.Device.LastSeen = DateTime.Now;
                        LevelChanged?.Invoke(battery.Device, level);
                    }
                };

                battery.Subscribed = true;
            }
            catch
            {
                // No notify support — polling covers it.
            }
        }
        catch
        {
            // Radio stack hiccup — this device is skipped for now.
            try { service?.Dispose(); } catch { /* ignore */ }
            return null;
        }

        if (battery == null) return null;

        lock (_sync)
        {
            _batteries.Add(battery);
        }

        return battery;
    }

    /// <summary>
    /// Reads the battery level, retrying on transient failures (the usual case is
    /// a sleeping controller — GATT reads cannot wake it, but a read issued right
    /// after the user pressed a button will succeed).
    /// </summary>
    private static async Task ReadWithRetryAsync(GattBattery battery, CancellationToken ct)
    {
        const int attempts = 3;

        for (var attempt = 0; attempt < attempts; attempt++)
        {
            if (ct.IsCancellationRequested) return;

            try
            {
                // 10s per attempt — a sleeping controller never answers, and the
                // call would otherwise hang the poll indefinitely.
                var readTask = battery.Characteristic.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask();
                if (await Task.WhenAny(readTask, Task.Delay(10000)) != readTask)
                    continue; // read timed out (controller asleep) — retry
                var result = await readTask;
                if (result.Status == GattCommunicationStatus.Success)
                {
                    var level = SafeLevel(result.Value);
                    if (level is >= 0 and <= 100)
                    {
                        battery.LastLevel = level;
                        battery.Device.Level = level;
                        battery.Device.LastSeen = DateTime.Now;
                        return;
                    }

                    return; // readable, but the value was not a sane percentage
                }
            }
            catch
            {
                // Radio hiccup (link reset, device dropped off) — retry.
            }

            if (attempt < attempts - 1)
                await Task.Delay(500, ct);
        }
    }

    /// <summary>Extracts a 0-100 percentage from the characteristic value, or -1.</summary>
    private static int SafeLevel(Windows.Storage.Streams.IBuffer? buffer)
    {
        try
        {
            if (buffer is not { Length: >= 1 }) return -1;
            var bytes = buffer.ToArray();
            if (bytes.Length < 1) return -1;

            var level = bytes[0];
            // Some devices report the level in the second byte; fall back to it.
            if (level == 0 && bytes.Length > 1) level = bytes[1];
            return level;
        }
        catch
        {
            return -1;
        }
    }

    private static string ShortenId(string id) =>
        id.Length > 24 ? id[..24] + "…" : id;

    public void Dispose()
    {
        lock (_sync)
        {
            foreach (var b in _batteries)
            {
                try { b.Service.Dispose(); } catch { /* ignore */ }
            }
            _batteries.Clear();
        }
    }
}
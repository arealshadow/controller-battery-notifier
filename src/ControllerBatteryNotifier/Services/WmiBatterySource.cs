using System.Management;
using ControllerBatteryNotifier.Models;

namespace ControllerBatteryNotifier.Services;

/// <summary>
/// Fallback source: the PC's own battery via WMI (root\WMI\BatteryStatus).
/// Controllers don't appear here — this is mainly useful on laptops.
/// </summary>
public sealed class WmiBatterySource
{
    // Latch: the WMI battery provider can hang *indefinitely* on some systems.
    // After the first timeout we stop retrying for the rest of the process
    // lifetime, otherwise every poll would leak another blocked thread.
    private static volatile bool _wmiBroken;

    public string Name => "WMI (system battery)";

    public async Task<IReadOnlyList<BatteryDevice>> ReadAsync(CancellationToken ct = default)
    {
        if (_wmiBroken) return Array.Empty<BatteryDevice>();
        ct.ThrowIfCancellationRequested();

        // The native query blocks on its own thread; race it against a timeout
        // so a wedged provider can never stall the poll loop.
        var work = Task.Run(() => QueryWmi());
        if (await Task.WhenAny(work, Task.Delay(15000)) != work)
        {
            _wmiBroken = true;
            return Array.Empty<BatteryDevice>();
        }

        try { return await work; }
        catch { /* desktop PC without a battery / WMI issues — report nothing */ }

        return Array.Empty<BatteryDevice>();
    }

    private static IReadOnlyList<BatteryDevice> QueryWmi()
    {
        var results = new List<BatteryDevice>();

        using var searcher = new ManagementObjectSearcher("root\\WMI",
            "SELECT * FROM BatteryStatus");

        foreach (ManagementObject obj in searcher.Get())
        {
            int? level = null;
            if (obj.Properties["EstimatedChargeRemaining"] is { Value: int remaining })
            {
                if (remaining is >= 0 and <= 100) level = remaining;
            }

            string? batteryId = obj.Properties["BatteryID"] is { Value: string id } ? id : "battery";

            results.Add(new BatteryDevice
            {
                Id = $"wmi:{batteryId}",
                Name = "This PC (battery)",
                Level = level,
                Source = "WMI",
                LastSeen = DateTime.Now,
            });
            break; // one entry is enough
        }

        return results;
    }
}

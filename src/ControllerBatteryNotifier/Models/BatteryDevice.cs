namespace ControllerBatteryNotifier.Models;

/// <summary>
/// A device whose battery level can be read (e.g. a Bluetooth game controller, or the PC battery).
/// </summary>
public sealed class BatteryDevice
{
    public required string Id { get; init; }
    public required string Name { get; init; }

    /// <summary>Battery level in percent (0-100), or null if the device does not report a level.</summary>
    public int? Level { get; set; }

    /// <summary>Where the reading came from, e.g. "Bluetooth (GATT)" or "WMI".</summary>
    public string Source { get; set; } = "";

    public DateTime? LastSeen { get; set; }

    public override string ToString() =>
        string.Format("{0} — {1} ({2})", Name, Level.HasValue ? Level.Value + "%" : "no battery info", Source);
}

namespace ControllerBatteryNotifier.Models;

public class AppSettings
{
    /// <summary>Battery level (in %) that triggers a low-battery notification.</summary>
    public int ThresholdPercent { get; set; } = 20;

    /// <summary>Whether to render the percentage inside the tray battery icon.</summary>
    public bool ShowPercentageInTray { get; set; } = true;

    /// <summary>Whether the app should start automatically when the user logs in.</summary>
    public bool StartWithWindows { get; set; } = false;

    /// <summary>How often (in seconds) battery levels are polled.</summary>
    public int PollIntervalSeconds { get; set; } = 30;

    /// <summary>Id of the device shown in the tray icon. Null = automatic (first controller / first device).</summary>
    public string? PrimaryDeviceId { get; set; }

    /// <summary>Whether low-battery notifications are enabled at all.</summary>
    public bool NotifyOnLowBattery { get; set; } = true;
}

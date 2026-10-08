using System.Threading;
using ControllerBatteryNotifier.Models;

namespace ControllerBatteryNotifier.Services;

/// <summary>
/// Periodically polls all battery sources, tracks the "primary" device (the one shown
/// in the tray icon), and raises events when it crosses below the configured threshold.
/// </summary>
public sealed class BatteryMonitor : IDisposable
{
    private readonly BluetoothBatterySource _bluetooth = new();
    private readonly WmiBatterySource _wmi = new();
    private readonly object _sync = new();

    private Timer? _timer;
    private CancellationTokenSource? _cts;
    private int _pollInFlight;

    /// <summary>Raised after every poll with the merged device list and the resolved primary device.</summary>
    public event Action<IReadOnlyList<BatteryDevice>, BatteryDevice?>? DevicesUpdated;

    /// <summary>Raised when the primary device drops to/below the threshold (once, until it recovers).</summary>
    public event Action<BatteryDevice, int>? LowBatteryDetected;

    public AppSettings Settings { get; }

    public IReadOnlyList<BatteryDevice> LastDevices { get; private set; } = Array.Empty<BatteryDevice>();
    public BatteryDevice? LastPrimary { get; private set; }

    public BatteryMonitor(AppSettings settings)
    {
        Settings = settings;
        _bluetooth.LevelChanged += OnLiveLevelChanged;
    }

    public void Start()
    {
        lock (_sync)
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            _timer?.Dispose();
            var interval = TimeSpan.FromSeconds(Settings.PollIntervalSeconds);
            _timer = new Timer(_ => _ = PollAsync(_cts.Token), null, TimeSpan.Zero, interval);
        }
    }

    /// <summary>Recreates the timer — call after changing PollIntervalSeconds.</summary>
    public void Restart() => Start();

    public async Task ForceRefreshAsync() => await PollAsync(CancellationToken.None);

    private async Task PollAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _pollInFlight, 1) == 1) return;
        try
        {
            var all = new List<BatteryDevice>();
            try { all.AddRange(await _bluetooth.ReadAsync(ct)); }
            catch { /* Bluetooth stack hiccup — keep going */ }

            try { all.AddRange(await _wmi.ReadAsync(ct)); }
            catch { /* no WMI / no battery — keep going */ }

            var primary = ResolvePrimary(all, Settings.PrimaryDeviceId);
            EvaluateThreshold(primary);

            LastDevices = all;
            LastPrimary = primary;
            DevicesUpdated?.Invoke(all, primary);
        }
        finally
        {
            Interlocked.Exchange(ref _pollInFlight, 0);
        }
    }

    private BatteryDevice? _lastNotifiedFor;
    private int? _lastSeenLevel;
    private bool _notified;

    private void EvaluateThreshold(BatteryDevice? primary)
    {
        if (!Settings.NotifyOnLowBattery || primary?.Level == null) return;

        // Reset state when the primary device changes.
        if (_lastNotifiedFor?.Id != primary.Id)
        {
            _lastNotifiedFor = null;
            _lastSeenLevel = null;
            _notified = false;
        }

        var level = primary.Level.Value;
        var threshold = Settings.ThresholdPercent;

        if (level > threshold)
        {
            // Recovered — re-arm so the next drop below the threshold notifies again.
            _notified = false;
            _lastSeenLevel = level;
            return;
        }

        if (_notified)
        {
            _lastSeenLevel = level;
            return;
        }

        // Notify on a crossing: it was seen above the threshold (or not seen yet)
        // and is now at/below it. A device already below on first sight notifies once.
        if (_lastSeenLevel == null || _lastSeenLevel > threshold)
        {
            _notified = true;
            _lastNotifiedFor = primary;
            _lastSeenLevel = level;
            LowBatteryDetected?.Invoke(primary, level);
        }
        else
        {
            _lastSeenLevel = level;
        }
    }

    private void OnLiveLevelChanged(BatteryDevice device, int level)
    {
        // A GATT subscription pushed an update — merge it into the last snapshot
        // (in place, same object reference) and re-evaluate immediately.
        var match = LastDevices.FirstOrDefault(d => d.Id == device.Id);
        if (match == null) return;

        match.Level = level;
        match.LastSeen = DateTime.Now;

        if (LastPrimary?.Id == device.Id)
            LastPrimary.Level = level;

        EvaluateThreshold(LastPrimary);
        DevicesUpdated?.Invoke(LastDevices, LastPrimary);
    }

    /// <summary>
    /// Picks the device shown in the tray: the user's explicit choice, else a device
    /// that looks like a game controller, else the first one found.
    /// </summary>
    public static BatteryDevice? ResolvePrimary(IReadOnlyList<BatteryDevice> all, string? preferredId)
    {
        if (all.Count == 0) return null;

        if (!string.IsNullOrEmpty(preferredId))
        {
            var match = all.FirstOrDefault(d => d.Id == preferredId);
            if (match != null) return match;
        }

        return all.FirstOrDefault(d =>
                   d.Name.Contains("controller", StringComparison.OrdinalIgnoreCase) ||
                   d.Name.Contains("gamepad", StringComparison.OrdinalIgnoreCase) ||
                   d.Name.Contains("playstation", StringComparison.OrdinalIgnoreCase))
               ?? all[0];
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _cts?.Cancel();
        _bluetooth.LevelChanged -= OnLiveLevelChanged;
        _bluetooth.Dispose();
    }
}

using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using ControllerBatteryNotifier.Models;
using ControllerBatteryNotifier.Services;
using Hardcodet.Wpf.TaskbarNotification;

namespace ControllerBatteryNotifier;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;
    private TaskbarIcon? _trayIcon;
    private FlyoutWindow? _flyout;
    private SettingsWindow? _settingsWindow;

    public AppSettings Settings { get; private set; } = new();
    public BatteryMonitor Monitor { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Diagnostics mode: list devices that expose a readable battery and exit.
        // IMPORTANT: run off the UI thread — before the message loop starts the
        // dispatcher will never pump, so any Task continuation captured on the UI
        // thread would deadlock the probe (WinRT awaits are fine, Task awaits aren't).
        if (e.Args.Any(a => a.Equals("--list-devices", StringComparison.OrdinalIgnoreCase)))
        {
            Task.Run(() => RunDeviceProbe()).GetAwaiter().GetResult();
            Shutdown();
            return;
        }

        // Diagnostics mode: render the tray icon to a PNG for visual inspection.
        //   --render-icon <path.png> [level|unknown]
        if (e.Args.FirstOrDefault(a => a.StartsWith("--render-icon", StringComparison.OrdinalIgnoreCase)) is { } renderArg)
        {
            var idx = Array.IndexOf(e.Args, renderArg);
            var path = idx + 1 < e.Args.Length ? e.Args[idx + 1] : "cbn-icon.png";
            int? lvl = 81;
            if (idx + 2 < e.Args.Length)
            {
                var arg = e.Args[idx + 2];
                lvl = int.TryParse(arg, out var v) ? v
                    : arg.Equals("unknown", StringComparison.OrdinalIgnoreCase) ? null : 81;
            }
            RenderIconToPng(path, lvl);
            Shutdown();
            return;
        }

        _singleInstanceMutex = new Mutex(true, @"Local\ControllerBatteryNotifier.SingleInstance", out var isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show(
                "Controller Battery Notifier is already running — look for the battery icon in the system tray.",
                "Controller Battery Notifier",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        Settings = SettingsStore.Load();
        Notifier.Initialize();

        var debugFlyout = e.Args.Any(a => a.Equals("--debug-flyout", StringComparison.OrdinalIgnoreCase));

        Monitor = new BatteryMonitor(Settings);
        Monitor.DevicesUpdated += OnDevicesUpdated;
        Monitor.LowBatteryDetected += OnLowBatteryDetected;

        CreateTrayIcon();
        Monitor.Start();

        // The app lives in the system tray:
        //   left-click the icon  -> the flyout pops out of the tray (refresh / devices / settings)
        //   right-click the icon -> a small menu (refresh / start with Windows / exit)
        _flyout = new FlyoutWindow(this);
        if (debugFlyout)
            _flyout.ShowCentered();

        // Diagnostics mode: open the full settings window directly.
        if (e.Args.Any(a => a.Equals("--debug-settings", StringComparison.OrdinalIgnoreCase)))
            ShowSettings();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Monitor?.Dispose();
        _trayIcon?.Dispose();
        try { _singleInstanceMutex?.ReleaseMutex(); } catch { /* not owned */ }
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    private void CreateTrayIcon()
    {
        _trayIcon = new TaskbarIcon
        {
            ToolTipText = "Controller Battery Notifier",
            IconSource = IconRenderer.Render(null, Settings.ShowPercentageInTray),
        };

        // Left-click: the flyout pops out of the tray icon.
        // Right-click: a small quick menu (refresh / start with Windows / exit).
        _trayIcon.TrayLeftMouseUp += (_, _) => ToggleFlyout();
        _trayIcon.TrayMouseDoubleClick += (_, _) => ToggleFlyout();
        _trayIcon.TrayRightMouseUp += (_, _) => OpenTrayMenu();

        var menu = new ContextMenu();

        var refresh = new MenuItem { Header = "Refresh now" };
        refresh.Click += (_, _) => _ = Monitor.ForceRefreshAsync();
        menu.Items.Add(refresh);

        var startup = new MenuItem
        {
            Header = "Start with Windows",
            IsCheckable = true,
            IsChecked = StartupManager.IsRegistered()
        };
        startup.Click += (_, _) =>
        {
            startup.IsChecked = !startup.IsChecked;
            StartupManager.Set(startup.IsChecked);
            Settings.StartWithWindows = startup.IsChecked;
            SettingsStore.Save(Settings);
        };
        menu.Items.Add(startup);

        menu.Items.Add(new Separator());

        var exit = new MenuItem { Header = "Exit" };
        exit.Click += (_, _) => ForceExit();
        menu.Items.Add(exit);

        _trayIcon.ContextMenu = menu;
    }

    private void ToggleFlyout()
    {
        if (_flyout == null || !_flyout.IsLoaded)
            _flyout = new FlyoutWindow(this);
        _flyout.ShowAtCursor();
    }

    /// <summary>Opens the full settings window (the old "app window" experience).</summary>
    public void ShowSettings()
    {
        if (_settingsWindow == null || !_settingsWindow.IsLoaded)
            _settingsWindow = new SettingsWindow(this);

        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void OpenTrayMenu()
    {
        if (_trayIcon?.ContextMenu is { IsOpen: false } menu)
        {
            // Open at the mouse position (over the tray icon) — the same
            // spot the native right-click popup uses, but triggered by a
            // left click as well.
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            menu.IsOpen = true;
        }
    }

    private void OnDevicesUpdated(IReadOnlyList<BatteryDevice> devices, BatteryDevice? primary)
    {
        Dispatcher.BeginInvoke(() =>
        {
            ApplyTrayState(devices, primary);
            _flyout?.UpdateDevices(devices, primary);
            _settingsWindow?.Panel?.UpdateDevices(devices, primary);
        });
    }

    private void ApplyTrayState(IReadOnlyList<BatteryDevice> devices, BatteryDevice? primary)
    {
        if (_trayIcon == null) return;

        var level = primary?.Level;
        _trayIcon.IconSource = IconRenderer.Render(level, Settings.ShowPercentageInTray);
        _trayIcon.ToolTipText = level.HasValue && primary != null
            ? $"{primary.Name}: {level.Value}%  —  Controller Battery Notifier"
            : "Controller Battery Notifier — no device with a battery found";
    }

    /// <summary>Re-renders the tray icon (e.g. after the "show percentage" preference changed).</summary>
    public void RefreshTrayIcon() => ApplyTrayState(Monitor.LastDevices, Monitor.LastPrimary);

    private void OnLowBatteryDetected(BatteryDevice device, int level)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var title = $"{device.Name} battery is low";
            var message = $"Battery is at {level}% — might need to recharge the batteries.";

            if (!Notifier.TryShowToast(title, message))
            {
                // Fallback: tray balloon tip.
                _trayIcon?.ShowBalloonTip("Low battery",
                    $"{device.Name} is at {level}% — might need to recharge the batteries.",
                    BalloonIcon.Warning);
            }
        });
    }

    public void SendTestNotification()
    {
        const string title = "Controller Battery Notifier";
        const string message = "This is a test notification. Low-battery alerts will look like this.";

        if (!Notifier.TryShowToast(title, message))
        {
            _trayIcon?.ShowBalloonTip("Test notification", message, BalloonIcon.Info);
            MessageBox.Show(
                "Toast notification was unavailable, so a tray balloon was used instead.",
                "Controller Battery Notifier", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    public void ForceExit()
    {
        _flyout?.Close();
        _settingsWindow?.Close();
        Shutdown();
    }

    private static void RenderIconToPng(string path, int? level)
    {
        var bmp = IconRenderer.Render(level, true);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        encoder.Save(fs);
        Console.WriteLine($"Icon rendered to {path}");
    }

    private async Task RunDeviceProbe()
    {
        var lines = new List<string>
        {
            "Controller Battery Notifier — device probe",
            new string('=', 45)
        };

        try
        {
            using var bluetooth = new BluetoothBatterySource();
            var wmi = new WmiBatterySource();

            var all = new List<BatteryDevice>();
            all.AddRange(await bluetooth.ReadAsync());
            all.AddRange(await wmi.ReadAsync());

            if (all.Count == 0)
            {
                lines.Add("No devices with a readable battery were found.");
            }
            else
            {
                foreach (var d in all)
                {
                    var level = d.Level is { } l ? $"{l}%" : "n/a";
                    lines.Add($"{d.Name,-42} {level,-6} {d.Source}");
                }
            }

            lines.Add(string.Empty);
            var primary = BatteryMonitor.ResolvePrimary(all, null);
            lines.Add(primary != null
                ? $"Tray icon would show: {primary.Name}"
                : "Tray icon would show: (no device)");
        }
        catch (Exception ex)
        {
            lines.Add($"Probe failed: {ex.Message}");
        }

        var text = string.Join(Environment.NewLine, lines);
        try { Console.WriteLine(text); } catch { /* no console attached */ }

        try
        {
            var path = Path.Combine(Path.GetTempPath(), "cbn-devices.txt");
            File.WriteAllText(path, text + Environment.NewLine);
            try { Console.WriteLine(); Console.WriteLine($"Also written to: {path}"); } catch { }
        }
        catch { /* ignore */ }
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ControllerBatteryNotifier.Models;
using ControllerBatteryNotifier.Services;

namespace ControllerBatteryNotifier;

/// <summary>Row wrapper so the UI can show per-device display state (e.g. which one is primary).</summary>
public sealed class DeviceRow
{
    public required BatteryDevice Device { get; init; }
    public bool IsPrimary { get; init; }

    public string Name => Device.Name;
    public string SourceText => Device.Source;
    public string LevelText => Device.Level is { } l ? $"{l}%" : "—";
    public double LevelPercent => Device.Level ?? 0;
    public Brush LevelBrush => Device.Level is { } l ? IconRenderer.ColorForLevel(l) : Brushes.Gray;
}

/// <summary>
/// The settings UI, hosted by the tray flyout. All state changes are applied
/// immediately and persisted (same behavior as the old settings window).
/// </summary>
public partial class SettingsPanel : UserControl
{
    private readonly App _app;
    private bool _loadingSettings;

    public SettingsPanel(App app)
    {
        // Assign BEFORE InitializeComponent: XAML event handlers (e.g. the Slider's
        // ValueChanged, which fires while the XAML is being built) must never see
        // _app == null — and must not save settings mid-construction either.
        _app = app;
        _loadingSettings = true;
        InitializeComponent();
        LoadSettingsIntoUi();
        _ = RefreshDevicesAsync();
    }

    private void LoadSettingsIntoUi()
    {
        _loadingSettings = true;
        try
        {
            var s = _app.Settings;
            NotifyCheckBox.IsChecked = s.NotifyOnLowBattery;
            ThresholdSlider.Value = s.ThresholdPercent;
            ShowPercentCheckBox.IsChecked = s.ShowPercentageInTray;
            StartWithWindowsCheckBox.IsChecked = StartupManager.IsRegistered();
            SelectPollInterval(s.PollIntervalSeconds);
        }
        finally
        {
            _loadingSettings = false;
        }

        UpdateThresholdLabel();
    }

    private void SelectPollInterval(int seconds)
    {
        foreach (var item in PollIntervalCombo.Items.OfType<ComboBoxItem>())
        {
            if (int.TryParse((string?)item.Tag, out var value) && value == seconds)
            {
                PollIntervalCombo.SelectedItem = item;
                return;
            }
        }

        if (PollIntervalCombo.SelectedIndex < 0)
            PollIntervalCombo.SelectedIndex = 1; // default: 30 seconds
    }

    private void NotifyChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _app.Settings.NotifyOnLowBattery = NotifyCheckBox.IsChecked == true;
        SettingsStore.Save(_app.Settings);
    }

    private void ThresholdChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateThresholdLabel();
        if (_loadingSettings || _app == null) return;
        _app.Settings.ThresholdPercent = (int)ThresholdSlider.Value;
        SettingsStore.Save(_app.Settings);
    }

    private void UpdateThresholdLabel()
    {
        if (ThresholdLabel == null) return;
        ThresholdLabel.Text = $"Threshold: {(int)ThresholdSlider.Value}%";
    }

    private void TrayPrefChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _app.Settings.ShowPercentageInTray = ShowPercentCheckBox.IsChecked == true;
        SettingsStore.Save(_app.Settings);
        _app.RefreshTrayIcon();
    }

    private void StartWithWindowsChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        var enabled = StartWithWindowsCheckBox.IsChecked == true;
        StartupManager.Set(enabled);
        _app.Settings.StartWithWindows = enabled;
        SettingsStore.Save(_app.Settings);
    }

    private void PollIntervalChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings || PollIntervalCombo.SelectedItem is not ComboBoxItem { Tag: string tag })
            return;
        if (!int.TryParse(tag, out var seconds)) return;

        _app.Settings.PollIntervalSeconds = seconds;
        SettingsStore.Save(_app.Settings);
        _app.Monitor.Restart();
    }

    public void UpdateDevices(IReadOnlyList<BatteryDevice> devices, BatteryDevice? primary)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => UpdateDevices(devices, primary));
            return;
        }

        var rows = devices.Select(d => new DeviceRow { Device = d, IsPrimary = primary?.Id == d.Id }).ToList();
        DeviceList.ItemsSource = rows;

        if (devices.Count == 0)
        {
            DeviceHint.Text =
                                "No devices found. Pair your controller over Bluetooth " +
                "(Settings → Bluetooth & devices → Add device → Wireless controller), make sure it is " +
                "connected, then press “Refresh now”. " +
                "If the controller is in sleep mode, press any button to wake it up first — " +
                "a sleeping controller does not answer battery queries.";
        }
        else if (primary != null)
        {
            DeviceHint.Text =
                $"The tray icon currently shows: {primary.Name}. Use “Primary” to show a different device instead.";
        }
        else
        {
            DeviceHint.Text = "Devices found, but none reports a battery level yet.";
        }

        StatusText.Text = $"Last updated {DateTime.Now:HH:mm:ss} — {devices.Count} device(s) found.";
    }

    private async Task RefreshDevicesAsync()
    {
        RefreshButton.IsEnabled = false;
        StatusText.Text = "Scanning for devices…";
        try
        {
            await _app.Monitor.ForceRefreshAsync();
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
        // The DevicesUpdated event raises UpdateDevices with the fresh data.
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = RefreshDevicesAsync();

    private void MakePrimary_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: DeviceRow row }) return;

        _app.Settings.PrimaryDeviceId = row.Device.Id;
        SettingsStore.Save(_app.Settings);

        var primary = BatteryMonitor.ResolvePrimary(_app.Monitor.LastDevices, _app.Settings.PrimaryDeviceId);
        _app.RefreshTrayIcon();
        UpdateDevices(_app.Monitor.LastDevices, primary);
        StatusText.Text = $"The tray icon now shows: {row.Device.Name}.";
    }

    private void TestNotification_Click(object sender, RoutedEventArgs e) => _app.SendTestNotification();
}
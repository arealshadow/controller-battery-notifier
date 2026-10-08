using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ControllerBatteryNotifier.Models;
using ControllerBatteryNotifier.Services;

namespace ControllerBatteryNotifier;

/// <summary>
/// The tray flyout (opened by LEFT-clicking the tray icon): a compact, borderless,
/// glass-styled panel anchored to the tray icon that disappears the moment focus
/// leaves it — like the Windows 11 volume flyout.
/// Shows: Refresh button, connected devices, and a button opening the full
/// Settings window.
/// </summary>
public partial class FlyoutWindow : Window
{
    private readonly App _app;
    private bool _suppressClose;
    private bool _refreshing;

    // XAML binding helpers (static so they can be referenced with {x:Static}).
    public static readonly PercentTextConverter PercentText = new();
    public static readonly LevelBrushConverter LevelBrush = new();

    public FlyoutWindow(App app)
    {
        _app = app;
        InitializeComponent();
        UpdateDevices(_app.Monitor.LastDevices, _app.Monitor.LastPrimary);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_refreshing) return;
        _refreshing = true;
        RefreshButton.IsEnabled = false;
        RefreshButton.Content = "Refreshing…";
        try
        {
            await _app.Monitor.ForceRefreshAsync();
            UpdateDevices(_app.Monitor.LastDevices, _app.Monitor.LastPrimary);
        }
        finally
        {
            _refreshing = false;
            RefreshButton.IsEnabled = true;
            RefreshButton.Content = "Refresh";
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        // Dismiss the flyout, then open the full settings window (a real window).
        Hide();
        _app.ShowSettings();
    }

    // ---- XAML binding converters --------------------------------------------

    public sealed class PercentTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            => value is int level ? $"{level}%" : "—";

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            => throw new NotSupportedException();
    }

    public sealed class LevelBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            => value is int level ? IconRenderer.ColorForLevel(level) : new SolidColorBrush(Color.FromRgb(0x7E, 0x9E, 0xC9));

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>Shows (or toggles) the flyout anchored just above the tray icon.</summary>
    public void ShowAtCursor()
    {
        if (IsVisible)
        {
            Hide();
            return;
        }

        var (w, h) = GetRenderedSize();

        var workArea = SystemParameters.WorkArea;
        GetCursorPos(out var cursor);
        var scale = ScreenScale();
        double x = cursor.X / scale;
        double y = cursor.Y / scale;

        // Center on the icon horizontally, just above it; flip below if there is
        // no room above (e.g. cursor near the top edge).
        double left = Math.Max(workArea.Left, Math.Min(x - w / 2.0, workArea.Right - w));
        double top = y - h - 6;
        if (top < workArea.Top)
            top = y + 14;

        _suppressClose = true;
        Left = left;
        Top = top;
        Show();
        Activate();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => _suppressClose = false));
    }

    /// <summary>Shows the flyout in the screen center (used by --debug-flyout).</summary>
    public void ShowCentered()
    {
        if (IsVisible) return;
        var (w, h) = GetRenderedSize();
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Left + (workArea.Width - w) / 2.0;
        Top = workArea.Top + (workArea.Height - h) / 2.0;
        _suppressClose = true;
        Show();
        Activate();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => _suppressClose = false));
    }

    public void UpdateDevices(IReadOnlyList<BatteryDevice> devices, BatteryDevice? primary)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => UpdateDevices(devices, primary));
            return;
        }

        DeviceList.ItemsSource = devices;
        var empty = devices.Count == 0;
        DeviceList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        EmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        EmptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Hides when the user clicks/activates anything else.</summary>
    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        if (_suppressClose) return;

        // Check after the current input is processed: if focus is still inside
        // the flyout (user clicked a control) keep it open; otherwise hide.
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            if (_suppressClose) return;
            if (IsActive || Keyboard.FocusedElement is not null) return;
            Hide();
        }));
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            Hide();
    }

    /// <summary>
    /// Returns the flyout's (width, height) in WPF units for positioning math.
    /// The height uses SizeToContent, so before the FIRST Show it is still NaN —
    /// feeding NaN into Left/Top makes WPF fall back to its default cascade
    /// position (near the top of the screen) instead of anchoring to the
    /// cursor. Measuring the content first yields a real desired size.
    /// </summary>
    private (double w, double h) GetRenderedSize()
    {
        double w = Width, h = Height;
        if (double.IsNaN(w) || double.IsNaN(h))
        {
            Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var desired = DesiredSize;
            if (double.IsNaN(w)) w = desired.Width;
            if (double.IsNaN(h)) h = desired.Height;
        }
        if (double.IsNaN(w) || w <= 0) w = 320; // last-resort fallback
        if (double.IsNaN(h) || h <= 0) h = 240;
        return (w, h);
    }

    // ---- Win32 helpers (cursor position + DPI scale) -------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern int GetDeviceCaps(IntPtr hdc, int index);
    private const int LOGPIXELSX = 88;

    private static double ScreenScale()
    {
        var hdc = GetDC(IntPtr.Zero);
        if (hdc == IntPtr.Zero) return 1.0;
        try
        {
            var dpi = GetDeviceCaps(hdc, LOGPIXELSX);
            return dpi > 0 ? dpi / 96.0 : 1.0;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, hdc);
        }
    }
}
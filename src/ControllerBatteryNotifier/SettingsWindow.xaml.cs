using System.Windows;
using System.Windows.Input;

namespace ControllerBatteryNotifier;

/// <summary>
/// The full settings window (opened from the flyout's "Settings" button),
/// the same classic app-window experience as before the tray flyout existed.
/// </summary>
public partial class SettingsWindow : Window
{
    public SettingsPanel Panel { get; }

    public SettingsWindow(App app)
    {
        InitializeComponent();
        Panel = new SettingsPanel(app);
        Host.Content = Panel;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) Close();
        else DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
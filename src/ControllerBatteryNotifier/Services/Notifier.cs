using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Xml;
using Microsoft.Win32;
using Windows.UI.Notifications;

namespace ControllerBatteryNotifier.Services;

/// <summary>
/// Sends proper Windows toast notifications. The app registers an AppUserModelID
/// (HKCU\Software\Classes\AppUserModelId) so un-packaged toasts show our name + icon.
/// </summary>
public static class Notifier
{
    public const string AumId = "cba.controllerbatterynotifier.app";

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string id);

    public static void Initialize()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(
                $@"Software\Classes\AppUserModelId\{AumId}", writable: true);

            key?.SetValue("DisplayName", "Controller Battery Notifier");
            key?.SetValue("IconBackgroundColor", "#0078D7");
            if (Environment.ProcessPath is { } exe)
            {
                key?.SetValue("IconUri", new Uri(exe).AbsoluteUri);
            }

            SetCurrentProcessExplicitAppUserModelID(AumId);
        }
        catch
        {
            // Toasts may fail later — the tray balloon fallback covers it.
        }
    }

    /// <summary>Shows a toast; returns false if it failed (caller can fall back to a balloon tip).</summary>
    public static bool TryShowToast(string title, string message)
    {
        try
        {
            // ToastNotification expects the WinRT Windows.Data.Xml.Dom.XmlDocument.
            var document = new Windows.Data.Xml.Dom.XmlDocument();
            document.LoadXml(BuildToastXml(title, message));

            var toast = new ToastNotification(document);
            var notifier = ToastNotificationManager.CreateToastNotifier(AumId);
            notifier.Show(toast);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string BuildToastXml(string title, string message)
    {
        var sb = new StringBuilder();
        sb.Append("<toast activationType=\"none\">");
        sb.Append("<visual><binding template=\"ToastText02\">");
        sb.Append("<text id=\"1\">").Append(SecurityElement.Escape(title)).Append("</text>");
        sb.Append("<text id=\"2\">").Append(SecurityElement.Escape(message)).Append("</text>");
        sb.Append("</binding></visual></toast>");
        return sb.ToString();
    }
}
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ControllerBatteryNotifier.Models;

namespace ControllerBatteryNotifier.Services;

/// <summary>
/// Persists user settings as JSON in %APPDATA%\ControllerBatteryNotifier\settings.json
/// </summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string SettingsDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "ControllerBatteryNotifier");

    public static string SettingsPath { get; } = Path.Combine(SettingsDirectory, "settings.json");

    public static bool WasCreatedOnThisMachine { get; } = File.Exists(SettingsPath) is false;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath));
                if (loaded != null)
                {
                    Normalize(loaded);
                    return loaded;
                }
            }
        }
        catch
        {
            // Corrupt settings file — fall back to defaults.
        }

        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        Normalize(settings);
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch
        {
            // Best effort — settings simply won't persist this time.
        }
    }

    private static void Normalize(AppSettings s)
    {
        s.ThresholdPercent = Math.Clamp(s.ThresholdPercent, 1, 99);
        s.PollIntervalSeconds = Math.Clamp(s.PollIntervalSeconds, 10, 3600);
    }
}

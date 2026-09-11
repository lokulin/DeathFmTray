using System;
using System.IO;
using System.Text.Json;

namespace DeathFmTray;

/// <summary>Everything about the app's state that should survive a restart.</summary>
public sealed class AppSettings
{
    public bool StartWithWindows { get; set; }
    public bool StartMinimizedToTray { get; set; }
    public bool MinimizeToTrayOnClose { get; set; } = true;

    public int WindowWidth { get; set; } = 1040;
    public int WindowHeight { get; set; } = 560;
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }

    // Change the "station" query param here if you ever want to point this
    // same app at one of death.fm's sister stations (80s.fm, adagio.fm, etc).
    public string StationUrl { get; set; } = "https://death.fm/player.php?station=dfm";
}

/// <summary>Reads/writes AppSettings as JSON under %AppData%\DeathFmTray\settings.json.</summary>
public static class SettingsStore
{
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DeathFmTray");

    private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                string json = File.ReadAllText(SettingsPath);
                AppSettings? loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (loaded is not null)
                {
                    return loaded;
                }
            }
        }
        catch
        {
            // Corrupt or unreadable settings file - fall back to defaults rather than crash on startup.
        }

        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            string json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Best-effort save; a failure here shouldn't take the app down.
        }
    }
}

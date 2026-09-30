using System;
using System.IO;
using System.Text.Json;

namespace DeathFmTray;

/// <summary>Everything about the app's state that should survive a restart.</summary>
public sealed class AppSettings
{
    public bool StartMinimizedToTray { get; set; }
    public bool MinimizeToTrayOnClose { get; set; } = true;
    public bool ShowTrackChangeNotifications { get; set; } = true;

    public int? WindowX { get; set; }
    public int? WindowY { get; set; }

    // Client-area size, remembered across runs. The window is locked to this
    // size unless unlocked from the menu (see PlayerForm.SetSizeLocked).
    public int? WindowWidth { get; set; }
    public int? WindowHeight { get; set; }
    public bool WindowSizeLocked { get; set; } = true;

    // Change the "station" query param here if you ever want to point this
    // same app at one of death.fm's sister stations (80s.fm, adagio.fm, etc).
    public string StationUrl { get; set; } = "https://death.fm/player.php?station=dfm";

    // The page's own #vol-slider resets to its hardcoded default (0.8) on
    // every load - null here means "never changed it, use the page's own
    // default" rather than baking 0.8 in twice. See VolumeService.
    public double? Volume { get; set; }

    // Last.fm scrobbling. The API key/secret that identify this app to
    // Last.fm's API are baked in as compile-time constants (see
    // AppCredentials) rather than stored here, since they're this app's own
    // identifiers, not a per-user secret. LastFmSessionKey/LastFmUsername are
    // still per-user: filled in automatically by the "Connect Last.fm..."
    // tray menu flow once you've authorized the app in your browser.
    public string? LastFmSessionKey { get; set; }
    public string? LastFmUsername { get; set; }
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

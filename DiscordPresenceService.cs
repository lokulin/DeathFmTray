using System;
using DiscordRPC;

namespace DeathFmTray;

/// <summary>
/// Shows the current track as a Discord Rich Presence status, via Discord's
/// local RPC (a named pipe the Discord desktop client listens on - nothing
/// shows up unless Discord is actually running; DiscordRpcClient handles
/// connecting and reconnecting to it in the background on its own).
///
/// Note: this always renders as "Playing Death.FM Player", not "Listening
/// to ..." - the "Listening to Spotify" verb is a special first-party
/// integration Discord built specifically for Spotify, not something the
/// general RPC protocol third-party apps use actually exposes, despite
/// persistent requests for it over the years.
///
/// Fed by PlayerForm the same way as LastFmScrobbler: only while actually
/// playing, since the page's now-playing display updates on a timer
/// regardless of play state.
/// </summary>
public sealed class DiscordPresenceService : IDisposable
{
    private DiscordRpcClient? _client;

    /// <summary>Opens the connection to Discord's local RPC pipe. Safe to call even if Discord isn't running yet.</summary>
    public void Start()
    {
        if (_client is not null)
            return;

        _client = new DiscordRpcClient(AppCredentials.DiscordClientId);
        _client.Initialize();
    }

    public void OnTrackChanged(NowPlayingMetadata metadata)
    {
        if (_client is null)
            return;

        // The injected script falls back to the literal string "Death.FM"
        // for title/artist before real now-playing data has loaded.
        if (string.IsNullOrWhiteSpace(metadata.Title) || (metadata.Title == "Death.FM" && metadata.Artist == "Death.FM"))
            return;

        var presence = new RichPresence
        {
            Details = Truncate(metadata.Title),
            State = Truncate(metadata.Artist),
            Timestamps = new Timestamps { Start = DateTime.UtcNow },
        };

        // Discord accepts a direct external image URL here (not just a
        // pre-uploaded asset key) - falls back to whatever default image key
        // is configured (uploaded under Rich Presence -> Art Assets in the
        // Discord Developer Portal for your own application) when a track
        // has no album art of its own yet.
        string? imageKey = !string.IsNullOrEmpty(metadata.ArtUrl) ? metadata.ArtUrl : AppCredentials.DiscordDefaultImageKey;
        if (!string.IsNullOrEmpty(imageKey))
        {
            presence.Assets = new Assets
            {
                LargeImageKey = imageKey,
                LargeImageText = "Death.FM",
            };
        }

        _client.SetPresence(presence);
    }

    public void OnPlaybackStopped() => _client?.ClearPresence();

    // Discord silently rejects Details/State over 128 characters; keep it well under that.
    private static string Truncate(string value) => value.Length <= 120 ? value : value[..117] + "...";

    public void Dispose() => _client?.Dispose();
}

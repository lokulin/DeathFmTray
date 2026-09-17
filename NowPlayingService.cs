using System;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace DeathFmTray;

/// <summary>Mirrors the page's playback state; consumed by <see cref="SmtcService"/>.</summary>
public enum PlaybackState
{
    Stopped,
    Playing,
    Paused,
    Waiting,
}

public readonly record struct NowPlayingMetadata(string Title, string Artist, string Album, string? ArtUrl);

/// <summary>
/// Reads the death.fm page's now-playing data and forwards it to C# instead of
/// through navigator.mediaSession. WebView2/Chromium auto-bridges
/// navigator.mediaSession to Windows' System Media Transport Controls on its
/// own, but it does so from its own msedgewebview2.exe child process - which
/// registers under its own identity, showing up as "Unknown app" in the
/// volume flyout no matter what AppUserModelID we set on our own process.
/// Instead we keep Chromium out of it entirely and drive SMTC ourselves from
/// DeathFmTray.exe via <see cref="SmtcService"/>, fed by the messages this
/// class receives.
/// </summary>
public sealed class NowPlayingService
{
    private readonly WebView2 _webView;

    public event Action<NowPlayingMetadata>? MetadataChanged;
    public event Action<PlaybackState>? PlaybackStateChanged;

    public NowPlayingService(WebView2 webView)
    {
        _webView = webView;
    }

    /// <summary>
    /// Registers the injection script so it runs on every page load, and
    /// starts listening for the messages it posts back. Call this once, after
    /// EnsureCoreWebView2Async and before the first Navigate.
    /// </summary>
    public async Task StartAsync()
    {
        _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
        await _webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(InjectedScript);
    }

    /// <summary>Clicks the page's own play button - used when SMTC's Play button is pressed.</summary>
    public void TriggerPlay() =>
        _ = _webView.CoreWebView2?.ExecuteScriptAsync("window.__deathFmTrayPlay && window.__deathFmTrayPlay();");

    /// <summary>Clicks the page's own stop button - used when SMTC's Pause/Stop button is pressed.</summary>
    public void TriggerStop() =>
        _ = _webView.CoreWebView2?.ExecuteScriptAsync("window.__deathFmTrayStop && window.__deathFmTrayStop();");

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(e.WebMessageAsJson);
            JsonElement root = doc.RootElement;
            string messageType = root.GetProperty("type").GetString() ?? "";

            switch (messageType)
            {
                case "metadata":
                    MetadataChanged?.Invoke(new NowPlayingMetadata(
                        root.GetProperty("title").GetString() ?? "Death.FM",
                        root.GetProperty("artist").GetString() ?? "Death.FM",
                        root.GetProperty("album").GetString() ?? "",
                        root.TryGetProperty("art", out JsonElement artEl) ? artEl.GetString() : null));
                    break;

                case "playbackState":
                    string state = root.GetProperty("state").GetString() ?? "";
                    PlaybackStateChanged?.Invoke(state switch
                    {
                        "playing" => PlaybackState.Playing,
                        "paused" => PlaybackState.Paused,
                        "waiting" => PlaybackState.Waiting,
                        _ => PlaybackState.Stopped,
                    });
                    break;
            }
        }
        catch (JsonException)
        {
            // Malformed/unexpected message from the page - ignore rather than crash the host.
        }
    }

    // Runs inside the death.fm page. Reads the same #np-track / #np-artist /
    // #np-album / #now-playing-art elements the page's own updateTrackData()
    // populates, and posts them back to C# via window.chrome.webview.postMessage
    // whenever they change, instead of setting navigator.mediaSession.metadata
    // (which would register a *second*, wrongly-attributed SMTC session via
    // Chromium's own msedgewebview2.exe process - see class remarks above).
    // Also exposes window.__deathFmTrayPlay/__deathFmTrayStop so SmtcService's
    // button-press handling can drive the page's own #btn-play/#btn-stop -
    // death.fm is a live stream with no real "pause" position, so Pause is
    // mapped to the same action as Stop.
    private const string InjectedScript = @"
(function () {
    function post(message) {
        try { window.chrome.webview.postMessage(message); } catch (e) { /* not ready yet */ }
    }

    var lastKey = '';

    function updateMetadata() {
        var trackEl = document.getElementById('np-track');
        var artistEl = document.getElementById('np-artist');
        var albumEl = document.getElementById('np-album');
        var artEl = document.getElementById('now-playing-art');

        var title = trackEl ? trackEl.innerText.trim() : 'Death.FM';
        var artist = artistEl ? artistEl.innerText.trim() : 'Death.FM';
        var album = albumEl ? albumEl.innerText.trim() : '';
        var art = artEl ? artEl.src : '';

        var key = title + '|' + artist + '|' + album + '|' + art;
        if (key === lastKey) return;
        lastKey = key;

        post({
            type: 'metadata',
            title: title || 'Death.FM',
            artist: artist || 'Death.FM',
            album: album,
            art: art
        });
    }

    window.__deathFmTrayPlay = function () {
        var btn = document.getElementById('btn-play');
        if (btn) btn.click();
    };
    window.__deathFmTrayStop = function () {
        var btn = document.getElementById('btn-stop');
        if (btn) btn.click();
    };

    function wirePlaybackState() {
        var audio = document.getElementById('audio-engine');
        if (!audio) { setTimeout(wirePlaybackState, 500); return; }

        audio.addEventListener('playing', function () {
            post({ type: 'playbackState', state: 'playing' });
            updateMetadata();
        });
        audio.addEventListener('pause', function () {
            post({ type: 'playbackState', state: 'paused' });
        });
        audio.addEventListener('waiting', function () {
            post({ type: 'playbackState', state: 'waiting' });
        });
    }

    function setup() {
        wirePlaybackState();
        updateMetadata();

        // The page refetches now-playing info every 30s and on track change;
        // a light poll here keeps our metadata in sync without needing to
        // patch the page's own fetch logic.
        setInterval(updateMetadata, 5000);
    }

    if (document.readyState === 'complete' || document.readyState === 'interactive') {
        setup();
    } else {
        document.addEventListener('DOMContentLoaded', setup);
    }
})();
";
}

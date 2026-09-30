using System;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Timer = System.Windows.Forms.Timer;

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
/// class receives. Also runs a watchdog that force-reloads the page if
/// playback stays stuck buffering for too long.
/// </summary>
public sealed class NowPlayingService : IDisposable
{
    // death.fm occasionally gets stuck buffering (the 'waiting' event fires
    // and playback never resumes) after long uptimes, sometimes surviving a
    // manual play/stop toggle. Force a full page reload if it stays stuck
    // longer than this - a plain WebView2 Navigate/Reload is a more reliable
    // recovery than relying on the page's own reconnect logic.
    private static readonly TimeSpan StallThreshold = TimeSpan.FromSeconds(30);

    private readonly WebView2 _webView;
    private readonly Timer _stallWatchdog = new() { Interval = 5000 };
    private DateTime? _waitingSince;

    public event Action<NowPlayingMetadata>? MetadataChanged;
    public event Action<PlaybackState>? PlaybackStateChanged;

    /// <summary>Raised once per page load with the station's "--theme-bg" CSS custom property, as a hex color string.</summary>
    public event Action<string>? ThemeChanged;

    /// <summary>Raised when the page's time readout moves; the value is its vertical centre in WebView2 device pixels.</summary>
    public event Action<int>? CountdownCentreChanged;

    /// <summary>Raised when the chat login/register overlay is closed - see PlayerForm.OnLoginOverlayClosed.</summary>
    public event Action? LoginOverlayClosed;

    public NowPlayingService(WebView2 webView)
    {
        _webView = webView;
        _stallWatchdog.Tick += OnStallWatchdogTick;
        _stallWatchdog.Start();
    }

    public void Dispose()
    {
        _stallWatchdog.Tick -= OnStallWatchdogTick;
        _stallWatchdog.Dispose();
    }

    private void OnStallWatchdogTick(object? sender, EventArgs e)
    {
        if (_waitingSince is DateTime since && DateTime.UtcNow - since >= StallThreshold)
        {
            // Reset rather than stop watching - if the reload doesn't fix it,
            // we want to try again after another StallThreshold rather than
            // waiting forever.
            _waitingSince = null;
            _webView.CoreWebView2?.Reload();
        }
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
                    _waitingSince = state == "waiting" ? (_waitingSince ?? DateTime.UtcNow) : null;
                    PlaybackStateChanged?.Invoke(state switch
                    {
                        "playing" => PlaybackState.Playing,
                        "paused" => PlaybackState.Paused,
                        "waiting" => PlaybackState.Waiting,
                        _ => PlaybackState.Stopped,
                    });
                    break;

                case "theme":
                    string? bg = root.TryGetProperty("bg", out JsonElement bgEl) ? bgEl.GetString() : null;
                    if (!string.IsNullOrEmpty(bg))
                        ThemeChanged?.Invoke(bg);
                    break;

                case "countdownY":
                    CountdownCentreChanged?.Invoke(root.GetProperty("y").GetInt32());
                    break;

                case "closeLogin":
                    LoginOverlayClosed?.Invoke();
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
        var stopBtn = document.getElementById('btn-stop');
        if (!audio || !stopBtn) { setTimeout(wirePlaybackState, 500); return; }

        audio.addEventListener('playing', function () {
            post({ type: 'playbackState', state: 'playing' });
            updateMetadata();
        });
        audio.addEventListener('waiting', function () {
            post({ type: 'playbackState', state: 'waiting' });
        });

        // Not audio.addEventListener('pause', ...): the page's own Play
        // button handler also calls audio.pause() as part of resetting the
        // element before reconnecting, so a bare 'pause' event fires on every
        // (re)connect too, not just on a real stop - it can't be trusted to
        // mean 'stopped'. Tying directly to the Stop button click instead is
        // unambiguous.
        stopBtn.addEventListener('click', function () {
            post({ type: 'playbackState', state: 'stopped' });
        });
    }

    function postTheme() {
        // Each station (?station=80s/afm/dfm/efm/sst) is its own full page
        // load with these CSS custom properties baked in server-side with
        // different values - read once per load rather than polling.
        var bg = getComputedStyle(document.documentElement).getPropertyValue('--theme-bg').trim();
        if (bg) post({ type: 'theme', bg: bg });
    }

    // The page's own vertical/horizontal layout toggle doesn't belong in this
    // wrapper (the window is resized instead), so it's hidden.
    function hideLayoutToggle() {
        var style = document.createElement('style');
        style.textContent = '.player-layout-toggle { display: none !important; }' +
            // The page's CSS makes body scroll in both directions whenever the Community
            // tab is hovered (its content slightly overflows), which is just noise in a
            // fixed-size window - the tab panels scroll themselves where needed.
            ' html, body { overflow: hidden !important; }';
        document.head.appendChild(style);
    }

    // The page's layout is responsive, so the time readout (which the native
    // cast button lines up with) moves around. Report its vertical centre.
    var lastCountdownY = null;
    function updateCountdownY() {
        var el = document.getElementById('countdown-timer');
        if (!el) return;
        var r = el.getBoundingClientRect();
        var y = Math.round((r.top + r.height / 2) * window.devicePixelRatio);
        if (y === lastCountdownY) return;
        lastCountdownY = y;
        post({ type: 'countdownY', y: y });
    }

    function setup() {
        hideLayoutToggle();
        updateCountdownY();
        window.addEventListener('resize', updateCountdownY);
        setInterval(updateCountdownY, 500);
        wirePlaybackState();
        updateMetadata();
        postTheme();

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

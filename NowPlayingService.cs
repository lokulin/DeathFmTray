using System.Threading.Tasks;
using Microsoft.Web.WebView2.WinForms;

namespace DeathFmTray;

/// <summary>
/// Feeds the page's own now-playing data into the browser's Media Session API
/// (navigator.mediaSession). Chromium/Edge (and so WebView2) already publishes
/// Media Session state to Windows' System Media Transport Controls on its own -
/// that's the "Unknown app" control you see once you hit Play. The page itself
/// just never calls the Media Session API, so Windows has no title/artist/art
/// to show. This class injects a small script that does that wiring for it.
///
/// No WinRT/SMTC C# code needed - Chromium handles the OS integration itself
/// once navigator.mediaSession is populated correctly.
/// </summary>
public sealed class NowPlayingService
{
    private readonly WebView2 _webView;

    public NowPlayingService(WebView2 webView)
    {
        _webView = webView;
    }

    /// <summary>
    /// Registers the injection script so it runs on every page load. Call this
    /// once, after EnsureCoreWebView2Async and before the first Navigate.
    /// </summary>
    public async Task StartAsync()
    {
        await _webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(InjectedScript);
    }

    // Runs inside the death.fm page. Reads the same #np-track / #np-artist /
    // #np-album / #now-playing-art elements the page's own updateTrackData()
    // populates, and mirrors them into navigator.mediaSession.metadata whenever
    // they change - which is what Windows reads for the media flyout's title,
    // artist, and artwork. Also wires SMTC's play/pause/stop buttons back to
    // the page's own #btn-play/#btn-stop, since death.fm is a live stream with
    // no real "pause" - Pause is mapped to the same action as Stop.
    private const string InjectedScript = @"
(function () {
    function setup() {
        if (!('mediaSession' in navigator)) return;

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

            var artwork = [];
            if (art) {
                artwork = [
                    { src: art, sizes: '96x96',   type: 'image/jpeg' },
                    { src: art, sizes: '256x256', type: 'image/jpeg' },
                    { src: art, sizes: '500x500', type: 'image/jpeg' }
                ];
            }

            try {
                navigator.mediaSession.metadata = new MediaMetadata({
                    title: title || 'Death.FM',
                    artist: artist || 'Death.FM',
                    album: album || 'Death.FM',
                    artwork: artwork
                });
            } catch (e) { /* ignore malformed metadata on transient DOM states */ }
        }

        function wireActionHandlers() {
            var setHandler = function (action, handler) {
                try { navigator.mediaSession.setActionHandler(action, handler); }
                catch (e) { /* action not supported by this WebView2/Edge version */ }
            };
            setHandler('play', function () {
                var btn = document.getElementById('btn-play');
                if (btn) btn.click();
            });
            // death.fm streams live with no resumable position, so Pause behaves
            // like Stop - there is nothing to 'resume' other than reconnecting.
            setHandler('pause', function () {
                var btn = document.getElementById('btn-stop');
                if (btn) btn.click();
            });
            setHandler('stop', function () {
                var btn = document.getElementById('btn-stop');
                if (btn) btn.click();
            });
        }

        function wirePlaybackState() {
            var audio = document.getElementById('audio-engine');
            if (!audio) { setTimeout(wirePlaybackState, 500); return; }

            audio.addEventListener('playing', function () {
                navigator.mediaSession.playbackState = 'playing';
                updateMetadata();
            });
            audio.addEventListener('pause', function () {
                navigator.mediaSession.playbackState = 'paused';
            });
            audio.addEventListener('waiting', function () {
                navigator.mediaSession.playbackState = 'none';
            });
        }

        wireActionHandlers();
        wirePlaybackState();
        updateMetadata();

        // The page refetches now-playing info every 30s and on track change;
        // a light poll here keeps mediaSession.metadata in sync without
        // needing to patch the page's own fetch logic.
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

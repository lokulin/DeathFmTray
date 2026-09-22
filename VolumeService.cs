using System;
using System.Globalization;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace DeathFmTray;

/// <summary>
/// Makes the page's volume slider remember its last value across launches -
/// death.fm's own page always resets #vol-slider to its hardcoded default
/// (0.8) on load and never persists anything itself.
///
/// Restoring is a one-way push from C# (<see cref="ApplyStoredVolume"/>,
/// called by <see cref="PlayerForm"/> once per navigation - initial load,
/// reloads, and station switches all count) rather than baking the value
/// into the injected script: the script registered via
/// AddScriptToExecuteOnDocumentCreatedAsync runs once and stays fixed for
/// the WebView2 instance's lifetime, but the stored volume can change at
/// any point (whenever the user drags the slider), so re-reading
/// <see cref="AppSettings.Volume"/> fresh at each navigation is simpler
/// than trying to keep an injected script's baked-in value in sync.
/// </summary>
public sealed class VolumeService : IDisposable
{
    // Matches the page's own hardcoded #vol-slider default - see the
    // player.php markup: <input type="range" id="vol-slider" ... value="0.8">
    private const double DefaultVolume = 0.8;

    private readonly WebView2 _webView;
    private readonly AppSettings _settings;

    public VolumeService(WebView2 webView, AppSettings settings)
    {
        _webView = webView;
        _settings = settings;
    }

    public void Dispose()
    {
        if (_webView.CoreWebView2 is not null)
        {
            _webView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
        }
    }

    /// <summary>
    /// Registers the injection script so it runs on every page load, and
    /// starts listening for volume-changed messages it posts back. Call this
    /// once, after EnsureCoreWebView2Async and before the first Navigate -
    /// same convention as NowPlayingService.StartAsync.
    /// </summary>
    public async System.Threading.Tasks.Task StartAsync()
    {
        _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
        await _webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(InjectedScript);
    }

    /// <summary>Pushes the persisted (or default) volume onto the page's slider/audio element - call once per navigation, after it completes.</summary>
    public void ApplyStoredVolume()
    {
        double volume = Math.Clamp(_settings.Volume ?? DefaultVolume, 0.0, 1.0);
        string js = $"window.__deathFmTraySetVolume && window.__deathFmTraySetVolume({volume.ToString(CultureInfo.InvariantCulture)});";
        _ = _webView.CoreWebView2?.ExecuteScriptAsync(js);
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(e.WebMessageAsJson);
            JsonElement root = doc.RootElement;
            if (root.GetProperty("type").GetString() != "volume")
            {
                return;
            }

            double volume = root.GetProperty("value").GetDouble();
            _settings.Volume = Math.Clamp(volume, 0.0, 1.0);
            SettingsStore.Save(_settings);
        }
        catch (JsonException)
        {
            // Malformed/unexpected message from the page - ignore rather than crash the host.
        }
    }

    // Runs inside the death.fm page. window.__deathFmTraySetVolume is exposed
    // immediately (used by ApplyStoredVolume, regardless of whether the DOM
    // elements it touches exist yet - it just no-ops until they do); the
    // 'change' listener (added once #vol-slider actually exists) is what
    // reports changes back to C# to persist.
    private const string InjectedScript = @"
(function () {
    function post(message) {
        try { window.chrome.webview.postMessage(message); } catch (e) { /* not ready yet */ }
    }

    window.__deathFmTraySetVolume = function (value) {
        var slider = document.getElementById('vol-slider');
        var audio = document.getElementById('audio-engine');
        if (slider) slider.value = value;
        if (audio) audio.volume = value;
    };

    function wireVolumePersistence() {
        var slider = document.getElementById('vol-slider');
        if (!slider) { setTimeout(wireVolumePersistence, 500); return; }

        // 'change' (fires once, when the user releases the slider or
        // finishes a keyboard step) rather than 'input' (fires continuously
        // while dragging) - avoids writing to disk dozens of times per drag.
        // The page's own 'input' listener (which live-updates audio.volume)
        // keeps working unaffected - this is a separate, additive listener.
        slider.addEventListener('change', function (e) {
            post({ type: 'volume', value: parseFloat(e.target.value) });
        });
    }

    if (document.readyState === 'complete' || document.readyState === 'interactive') {
        wireVolumePersistence();
    } else {
        document.addEventListener('DOMContentLoaded', wireVolumePersistence);
    }
})();
";
}

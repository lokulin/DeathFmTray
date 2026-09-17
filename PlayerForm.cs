using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Windows.Media;

namespace DeathFmTray;

/// <summary>
/// Main window: a thin WinForms shell around a WebView2 control pointed at the
/// death.fm player page. Closing the window hides it to the tray instead of
/// exiting (configurable); the tray icon is what actually owns app lifetime.
/// </summary>
public sealed class PlayerForm : Form
{
    private readonly WebView2 _webView = new();
    private readonly AppSettings _settings;
    private readonly NowPlayingService _nowPlaying;
    private readonly LastFmScrobbler _lastFm;
    private bool _allowClose;
    private Icon? _formIcon;
    private SmtcService? _smtc;
    private bool _isPlaying;
    private bool _resumeAfterLoginReload;
    private NowPlayingMetadata? _lastMetadata;

    /// <summary>Raised after a Last.fm connect/disconnect - used by TrayAppContext to refresh its menu item.</summary>
    public event Action? LastFmConnectionChanged;

    // death.fm's chat module sends guests here (see OnNavigationStarting).
    private const string AccountPageUrlFragment = "modules.php?name=Your_Account";

    // The one target="_blank" link we want WebView2 to keep handling itself
    // as a small embedded popup (see OnNewWindowRequested) - everything else
    // (album art, Amazon shop links) should open in the user's real browser.
    private const string RatingPopupUrlFragment = "modules/Ratings/playing_rating.php";

    /// <summary>Raised whenever playback starts/stops/pauses - used by TrayAppContext to reflect state in the tray icon.</summary>
    public event Action<PlaybackState>? PlaybackStateChanged;

    public PlayerForm(AppSettings settings)
    {
        _settings = settings;
        _nowPlaying = new NowPlayingService(_webView);
        _lastFm = new LastFmScrobbler(settings);

        Text = "Death.FM Player";

        // Fixed size matching the page's own .main-wrapper (1024×500).
        // The death.fm player does not reflow, so we lock the client area to
        // that exact size and remove the maximize button / size grips.
        var size = new Size(1050, 550);
        MinimumSize = size;
        MaximumSize = size;
        ClientSize = size; // client area = content size; Form adds titlebar/borders
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;

        if (settings.WindowX is int x && settings.WindowY is int y && IsOnScreen(x, y))
        {
            StartPosition = FormStartPosition.Manual;
            Location = new Point(x, y);
        }
        else
        {
            StartPosition = FormStartPosition.CenterScreen;
        }

        TrySetIcon();

        // Matches death.fm's own near-black/red palette (--theme-bg / --theme-color
        // in the page's CSS) - only takes full effect on Windows 11 22H2+; older
        // builds still get a generic dark titlebar via the same call.
        HandleCreated += (_, _) => WindowChromeHelper.ApplyDarkTitleBar(
            this,
            captionColor: Color.FromArgb(0x22, 0x00, 0x00),
            textColor: Color.White);

        _webView.Dock = DockStyle.Fill;
        Controls.Add(_webView);

        Load += PlayerForm_Load;
        Resize += PlayerForm_Resize;
        FormClosing += PlayerForm_FormClosing;
    }

    private void TrySetIcon()
    {
        try
        {
            _formIcon = LoadEmbeddedIcon("app.ico");
            Icon = _formIcon;
        }
        catch
        {
            // Missing/invalid icon resource shouldn't stop the app from running.
        }
    }

    private static Icon LoadEmbeddedIcon(string resourceName)
    {
        using Stream? stream = typeof(PlayerForm).Assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
            throw new InvalidOperationException($"Embedded resource '{resourceName}' not found.");
        return new Icon(stream);
    }

    private async void PlayerForm_Load(object? sender, EventArgs e)
    {
        try
        {
            // Keep WebView2's user-data folder under the user's AppData instead of
            // next to the exe. The default (DeathFmTray.exe.WebView2) fails when the
            // app is installed under Program Files where the user has no write access.
            string userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DeathFmTray",
                "WebView2");

            // Chromium auto-registers its own SMTC "Now Playing" session for any
            // page playing audio - completely independent of navigator.mediaSession
            // usage - using a generic fallback title and msedgewebview2.exe's own
            // identity. That session ends up competing with (and, per testing,
            // winning over) the one SmtcService registers under our own AUMID,
            // which is why the volume flyout still showed "Unknown app" even after
            // wiring up our own SMTC session correctly. Disabling this Chromium
            // feature stops it from creating that second session at all.
            var options = new CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments = "--disable-features=HardwareMediaKeyHandling"
            };

            var env = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: userDataFolder,
                options: options);

            await _webView.EnsureCoreWebView2Async(env);
            _webView.CoreWebView2.NavigationStarting += OnNavigationStarting;
            _webView.CoreWebView2.NewWindowRequested += OnNewWindowRequested;

            // Own SMTC session bound to this window, instead of relying on
            // WebView2/Chromium's navigator.mediaSession auto-bridging (which
            // registers under msedgewebview2.exe's identity, not ours - see
            // SmtcService for the full story).
            _smtc = new SmtcService(Handle);
            _smtc.ButtonPressed += OnSmtcButtonPressed;
            _nowPlaying.MetadataChanged += OnNowPlayingMetadataChanged;
            _nowPlaying.PlaybackStateChanged += OnNowPlayingPlaybackStateChanged;
            _nowPlaying.ThemeChanged += OnThemeChanged;
            _nowPlaying.LoginOverlayClosed += OnLoginOverlayClosed;

            // Registers the now-playing bridge script before the first
            // navigation so it's guaranteed to run on page load (and every
            // reload thereafter) - see NowPlayingService.cs for what it does.
            await _nowPlaying.StartAsync();

            _webView.CoreWebView2.Navigate(_settings.StationUrl);
        }
        catch (Exception ex)
        {
            // Most commonly: the WebView2 Runtime isn't installed. Surface a
            // friendly message instead of letting an async void exception
            // crash the app with an unhandled-exception dialog.
            MessageBox.Show(
                "Death.FM Player couldn't start its browser component (WebView2).\n\n" +
                "If you don't have the WebView2 Runtime installed, get it from:\n" +
                "https://developer.microsoft.com/microsoft-edge/webview2/\n\n" +
                $"Details: {ex.Message}",
                "Death.FM Player",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private void OnNowPlayingMetadataChanged(NowPlayingMetadata metadata)
    {
        _smtc?.UpdateMetadata(metadata.Title, metadata.Artist, metadata.Album, metadata.ArtUrl);
        _lastMetadata = metadata;

        // The page's now-playing display updates on a timer regardless of
        // whether the user has actually pressed Play, so only feed the
        // scrobbler while genuinely playing - otherwise it'd scrobble
        // tracks the user never actually listened to.
        if (_isPlaying)
            _lastFm.OnTrackChanged(metadata);
    }

    // Each station (?station=80s/afm/dfm/efm/sst) bakes in its own
    // --theme-bg CSS value server-side; switching stations is a full page
    // navigation, so re-applying the titlebar color per load keeps it
    // matching whichever station is currently loaded instead of always
    // showing death.fm's own near-black/red.
    private void OnThemeChanged(string hexColor)
    {
        try
        {
            Color themeBg = ColorTranslator.FromHtml(hexColor);
            WindowChromeHelper.ApplyDarkTitleBar(this, themeBg, Color.White);
        }
        catch (Exception)
        {
            // Malformed color from the page - keep whatever's already applied.
        }
    }

    // The chat tab's Login/Register links do
    // window.top.location.href = '/modules.php?name=Your_Account', replacing
    // the whole player with death.fm's full site and no way back. Intercept
    // that specific top-level navigation and show it as an in-page overlay
    // instead - tried a separate modal Form hosting its own WebView2 first,
    // but that hit a "Class not registered" COM error (a second WebView2
    // control sharing an environment, hosted via ShowDialog's nested message
    // loop, is a known-fragile combination). An overlay iframe injected into
    // the already-working page needs none of that: same WebView2, same
    // profile, cookies just work, and the login page sends no
    // X-Frame-Options/CSP that would block being framed by itself.
    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (e.Uri.Contains(AccountPageUrlFragment, StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true;
            string urlJson = JsonSerializer.Serialize(e.Uri);
            _ = _webView.CoreWebView2?.ExecuteScriptAsync($"({LoginOverlayScript})({urlJson});");
            return;
        }

        // The album-rating popup (window.open from the Now Playing panel)
        // refreshes the whole player from its window.opener after you rate,
        // which reloads the <audio> element and interrupts playback. That's
        // just this same page navigating to itself, so cancel it and run the
        // page's own updateTrackData() instead - it already re-fetches
        // np-rating-fg along with everything else on a timer, so this reuses
        // the exact same refresh path the page already trusts.
        if (string.Equals(e.Uri, _webView.CoreWebView2?.Source, StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true;
            _ = _webView.CoreWebView2?.ExecuteScriptAsync(
                "if (typeof updateTrackData === 'function') { updateTrackData(); }");
        }
    }

    // Album art and the Amazon shop links all use target="_blank", which
    // WebView2 otherwise handles by opening its own embedded popup window -
    // not what you want for "buy this on Amazon" links. Send those to the
    // user's actual default browser instead; the one exception is the rating
    // popup, which stays embedded since it's part of the app's own UI.
    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        if (e.Uri.Contains(RatingPopupUrlFragment, StringComparison.OrdinalIgnoreCase))
            return;

        e.Handled = true;
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri) { UseShellExecute = true });
        }
        catch
        {
            // Best-effort - nothing more useful to do if the shell can't launch a browser.
        }
    }

    // Builds a centered modal-style overlay with a close button around an
    // <iframe> pointed at the given URL, and re-syncs the chat iframes (in
    // case login state changed) when the overlay is closed.
    private const string LoginOverlayScript = @"
function (url) {
    var existing = document.getElementById('__deathFmTrayLoginOverlay');
    if (existing) existing.remove();

    var overlay = document.createElement('div');
    overlay.id = '__deathFmTrayLoginOverlay';
    overlay.style.cssText = 'position:fixed;inset:0;background:rgba(0,0,0,0.75);' +
        'z-index:2147483647;display:flex;align-items:center;justify-content:center;';

    // death.fm's account page is an old-school, non-responsive table layout
    // (no stable 'just the login form' selector to isolate it), so this is
    // sized to fit the page's natural width rather than trying to trim it -
    // narrower and it just clips the same content instead of reflowing it.
    var panel = document.createElement('div');
    panel.style.cssText = 'position:relative;width:960px;height:500px;max-width:95vw;' +
        'max-height:88vh;background:#111;border:1px solid #333;box-shadow:0 0 30px rgba(0,0,0,0.8);';

    var closeBtn = document.createElement('button');
    closeBtn.textContent = '✕ Close';
    closeBtn.style.cssText = 'position:absolute;top:-28px;right:0;background:transparent;' +
        'color:#fff;border:none;font-size:14px;cursor:pointer;font-family:Verdana,sans-serif;';
    closeBtn.onclick = function () {
        overlay.remove();
        // Reloading just the chat iframes (tried both contentWindow.reload()
        // and a cache-busted src) never picked up freshly-logged-in state,
        // even though the login cookie is confirmed set correctly - so hand
        // off to C# to do a real page reload instead (see
        // PlayerForm.OnLoginOverlayClosed), which auto-resumes playback
        // afterward so it doesn't feel like an interruption.
        // (Not the 'post' helper from NowPlayingService's injected script -
        // this script runs in its own separate scope via ExecuteScriptAsync,
        // so that closure isn't reachable from here.)
        try { window.chrome.webview.postMessage({ type: 'closeLogin' }); } catch (e) { /* ignore */ }
    };

    var iframe = document.createElement('iframe');
    iframe.src = url;
    iframe.style.cssText = 'width:100%;height:100%;border:none;background:#fff;';

    panel.appendChild(closeBtn);
    panel.appendChild(iframe);
    overlay.appendChild(panel);
    document.body.appendChild(overlay);
}";

    private void OnNowPlayingPlaybackStateChanged(PlaybackState state)
    {
        bool wasPlaying = _isPlaying;
        _isPlaying = state == PlaybackState.Playing;
        _smtc?.SetPlaybackStatus(state switch
        {
            PlaybackState.Playing => MediaPlaybackStatus.Playing,
            PlaybackState.Paused => MediaPlaybackStatus.Paused,
            PlaybackState.Waiting => MediaPlaybackStatus.Changing,
            _ => MediaPlaybackStatus.Stopped,
        });
        if (wasPlaying && !_isPlaying)
        {
            _lastFm.OnPlaybackStopped();
        }
        else if (!wasPlaying && _isPlaying && _lastMetadata is NowPlayingMetadata current)
        {
            // The now-playing display polls independently of play state and
            // dedupes by track identity, so if the current track's metadata
            // was already captured before Play was pressed, no new metadata
            // event will ever fire for it - feed it in directly here instead
            // of waiting for the next track change.
            _lastFm.OnTrackChanged(current);
        }
        PlaybackStateChanged?.Invoke(state);
    }

    // The chat overlay's close button posts here instead of us being able to
    // refresh just the chat iframes - iframe.contentWindow.reload() and a
    // cache-busted src reassignment were both tried and neither picked up a
    // freshly-logged-in session, even though the login cookie is confirmed
    // set correctly (checked the WebView2 profile's cookie database
    // directly). A real page reload does reflect it, so do that, but resume
    // playback automatically afterward so it doesn't feel like the
    // interruption this whole feature was built to avoid.
    private void OnLoginOverlayClosed()
    {
        _resumeAfterLoginReload = _isPlaying;
        _webView.CoreWebView2.NavigationCompleted += OnNavigationCompletedAfterLoginReload;
        _webView.CoreWebView2.Reload();
    }

    private async void OnNavigationCompletedAfterLoginReload(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        _webView.CoreWebView2.NavigationCompleted -= OnNavigationCompletedAfterLoginReload;
        if (!_resumeAfterLoginReload)
            return;

        _resumeAfterLoginReload = false;

        // Give the page's own script and our injected bridge a moment to
        // finish wiring up the fresh #btn-play element before clicking it.
        await Task.Delay(1000);
        _nowPlaying.TriggerPlay();
    }

    // Raised on a background thread by Windows, not the UI thread - hop back
    // onto it before touching the WebView2 control.
    private void OnSmtcButtonPressed(SystemMediaTransportControlsButton button)
    {
        if (IsDisposed)
            return;

        BeginInvoke(new Action(() =>
        {
            switch (button)
            {
                case SystemMediaTransportControlsButton.Play:
                    _nowPlaying.TriggerPlay();
                    break;
                case SystemMediaTransportControlsButton.Pause:
                case SystemMediaTransportControlsButton.Stop:
                    _nowPlaying.TriggerStop();
                    break;
            }
        }));
    }

    private void PlayerForm_Resize(object? sender, EventArgs e)
    {
        if (WindowState == FormWindowState.Minimized)
        {
            // Minimizing always tucks the window away into the tray rather than
            // leaving a taskbar entry around.
            Hide();
        }
    }

    private void PlayerForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_allowClose && _settings.MinimizeToTrayOnClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        PersistWindowState();
        SettingsStore.Save(_settings);
    }

    private void PersistWindowState()
    {
        if (WindowState == FormWindowState.Normal)
        {
            _settings.WindowX = Location.X;
            _settings.WindowY = Location.Y;
        }
    }

    private static bool IsOnScreen(int x, int y)
    {
        foreach (Screen screen in Screen.AllScreens)
        {
            if (screen.WorkingArea.Contains(x, y))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Brings the player window to the foreground, restoring it if minimized/hidden.</summary>
    public void ShowAndActivate()
    {
        Show();
        if (WindowState == FormWindowState.Minimized)
        {
            WindowState = FormWindowState.Normal;
        }
        Activate();
    }

    /// <summary>Actually closes the window (bypassing minimize-to-tray) - used when the app is exiting.</summary>
    public void ForceClose()
    {
        _allowClose = true;
        Close();
    }

    public bool IsLastFmConfigured => _lastFm.IsConfigured;
    public bool IsLastFmAuthorized => _lastFm.IsAuthorized;
    public string? LastFmUsername => _settings.LastFmUsername;

    /// <summary>
    /// Runs Last.fm's "desktop application" auth flow: get a token, send the
    /// user to authorize it in their real browser, then (once they confirm
    /// they've done so) exchange it for a session key that doesn't expire
    /// until revoked.
    /// </summary>
    public async Task ConnectLastFmAsync()
    {
        if (!_lastFm.IsConfigured)
        {
            MessageBox.Show(
                "Add a Last.fm API key and secret to settings.json first " +
                "(get one free at last.fm/api/account/create), then try again.",
                "Death.FM Player",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        try
        {
            string token = await _lastFm.GetAuthTokenAsync();
            Process.Start(new ProcessStartInfo(_lastFm.BuildAuthorizeUrl(token)) { UseShellExecute = true });

            DialogResult result = MessageBox.Show(
                "A browser window opened so you can authorize Death.FM Player on Last.fm.\n\n" +
                "Once you've approved it there, click OK to finish connecting.",
                "Connect Last.fm",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Information);
            if (result != DialogResult.OK)
                return;

            (string sessionKey, string username) = await _lastFm.CompleteAuthAsync(token);
            _settings.LastFmSessionKey = sessionKey;
            _settings.LastFmUsername = username;
            SettingsStore.Save(_settings);
            LastFmConnectionChanged?.Invoke();

            MessageBox.Show(
                $"Connected to Last.fm as {username}.",
                "Death.FM Player",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Couldn't connect to Last.fm.\n\nDetails: {ex.Message}",
                "Death.FM Player",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    public void DisconnectLastFm()
    {
        _settings.LastFmSessionKey = null;
        _settings.LastFmUsername = null;
        SettingsStore.Save(_settings);
        LastFmConnectionChanged?.Invoke();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _formIcon?.Dispose();
            _smtc?.Dispose();
            _nowPlaying.Dispose();
            _lastFm.Dispose();
        }
        base.Dispose(disposing);
    }
}

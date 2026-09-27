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
    private readonly CastService _castService;
    private readonly CastButton _castButton = new();
    private readonly ToolTip _castButtonToolTip = new();
    private readonly NowPlayingService _nowPlaying;
    private readonly VolumeService _volume;
    private readonly TrackChangeNotifier _trackChangeNotifier;
    private readonly LastFmScrobbler _lastFm;
    private readonly DiscordPresenceService _discord;
    private bool _allowClose;
    private Icon? _formIcon;
    private SmtcService? _smtc;
    private bool _isPlaying;
    private bool _resumeAfterLoginReload;
    private NowPlayingMetadata? _lastMetadata;

    /// <summary>Raised when Exit is chosen from the titlebar's system menu - TrayAppContext owns actually quitting.</summary>
    public event Action? ExitRequested;

    // Custom system menu command IDs (the menu from clicking the titlebar's
    // app icon, right-clicking the titlebar, or Alt+Space) - a second way to
    // reach the tray icon's most useful actions. Per the WM_SYSCOMMAND docs,
    // custom IDs must be below 0xF000 (reserved for Windows' own commands)
    // and Windows may use the low 4 bits internally, so these are kept at
    // round multiples of 0x10.
    private const int CmdSettings = 0x1000;
    private const int CmdCastTo = 0x1010;
    private const int CmdStartWithWindows = 0x1020;
    private const int CmdStartMinimized = 0x1030;
    private const int CmdMinimizeToTrayOnClose = 0x1040;
    private const int CmdShowTrackChangeNotifications = 0x1050;
    private const int CmdExit = 0x1060;
    private const int WM_SYSCOMMAND = 0x0112;
    private const int WM_INITMENU = 0x0116;

    // death.fm's chat module sends guests here (see OnNavigationStarting).
    private const string AccountPageUrlFragment = "modules.php?name=Your_Account";

    // The one target="_blank" link we want WebView2 to keep handling itself
    // as a small embedded popup (see OnNewWindowRequested) - everything else
    // (album art, Amazon shop links) should open in the user's real browser.
    private const string RatingPopupUrlFragment = "modules/Ratings/playing_rating.php";

    /// <summary>Raised whenever playback starts/stops/pauses - used by TrayAppContext to reflect state in the tray icon.</summary>
    public event Action<PlaybackState>? PlaybackStateChanged;

    public PlayerForm(AppSettings settings, CastService castService)
    {
        _settings = settings;
        _castService = castService;
        _nowPlaying = new NowPlayingService(_webView);
        _volume = new VolumeService(_webView, settings);
        _trackChangeNotifier = new TrackChangeNotifier(settings);
        _lastFm = new LastFmScrobbler(settings);
        _discord = new DiscordPresenceService();
        _discord.Start();

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

        // The tray icon is the only representation of this app that should
        // ever show - no separate taskbar button while the window's open,
        // on top of already hiding on minimize/close. WinForms achieves "no
        // taskbar button" by also excluding the window from Alt-Tab, which
        // is fine here since the tray icon (double-click or its menu) is
        // always available to bring it back.
        ShowInTaskbar = false;

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
        HandleCreated += (_, _) => BuildSystemMenu();

        _webView.Dock = DockStyle.Fill;
        Controls.Add(_webView);

        // Overlaid on top of the WebView2 rather than docked/anchored via
        // layout, since it needs to float above the page in a fixed corner.
        // Bottom-right, vertically centered on the page's own time readout.
        _castButton.Location = new Point(ClientSize.Width - _castButton.Width - 12, ClientSize.Height - _castButton.Height - 20);
        _castButton.Click += (_, _) => _ = CastMenuHelper.ShowAsync(_castService, _castButton.PointToScreen(new Point(0, _castButton.Height)));
        _castService.StateChanged += OnCastStateChanged;
        UpdateCastButtonState();
        Controls.Add(_castButton);
        _castButton.BringToFront();

        Load += PlayerForm_Load;
        Resize += PlayerForm_Resize;
        FormClosing += PlayerForm_FormClosing;
    }

    private void OnCastStateChanged(object? sender, EventArgs e)
    {
        if (InvokeRequired)
        {
            BeginInvoke(UpdateCastButtonState);
            return;
        }
        UpdateCastButtonState();
    }

    private void UpdateCastButtonState()
    {
        _castButton.IsCasting = _castService.State != CastState.Idle;
        _castButtonToolTip.SetToolTip(_castButton, _castService.State switch
        {
            CastState.Casting => $"Casting to {_castService.CastingDeviceName}",
            CastState.Connecting => "Connecting...",
            _ => "Cast to..."
        });
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
            // Re-applies the persisted volume on every load - initial load,
            // reloads, and station switches (each is a full page navigation)
            // all count, since the page's own slider always resets to its
            // hardcoded default otherwise. See VolumeService.
            _webView.CoreWebView2.NavigationCompleted += (_, _) => _volume.ApplyStoredVolume();

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
            _nowPlaying.MetadataChanged += _trackChangeNotifier.OnMetadataChanged;
            _nowPlaying.PlaybackStateChanged += _trackChangeNotifier.OnPlaybackStateChanged;

            // Registers the now-playing bridge script before the first
            // navigation so it's guaranteed to run on page load (and every
            // reload thereafter) - see NowPlayingService.cs for what it does.
            await _nowPlaying.StartAsync();
            await _volume.StartAsync();

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
        // scrobbler/presence while genuinely playing - otherwise they'd show
        // tracks the user never actually listened to.
        if (_isPlaying)
        {
            _lastFm.OnTrackChanged(metadata);
            _discord.OnTrackChanged(metadata);
        }
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
        if (state == PlaybackState.Stopped && wasPlaying)
        {
            // Deliberately state == Stopped, not "any transition away from
            // Playing": Waiting (a brief rebuffer, which this stream does
            // fairly often) was being treated the same as a real stop here,
            // which wiped the tracked current-track/start-time on every
            // buffering blip - resetting the elapsed-time-towards-30-seconds
            // counter to zero each time, so a track could play for minutes
            // total (with interruptions) and never actually reach the
            // scrobble threshold. Only a real Stop should finalize it.
            _lastFm.OnPlaybackStopped();
            _discord.OnPlaybackStopped();
        }
        else if (!wasPlaying && _isPlaying && _lastMetadata is NowPlayingMetadata current)
        {
            // The now-playing display polls independently of play state and
            // dedupes by track identity, so if the current track's metadata
            // was already captured before Play was pressed, no new metadata
            // event will ever fire for it - feed it in directly here instead
            // of waiting for the next track change.
            _lastFm.OnTrackChanged(current);
            _discord.OnTrackChanged(current);
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
    }

    /// <summary>Fires a toast using whatever track is currently cached, so notifications can be verified without waiting for a real track change.</summary>
    public void SendTestNotification()
    {
        NowPlayingMetadata metadata = _lastMetadata ?? new NowPlayingMetadata("Death.FM", "Death.FM", "", null);
        _trackChangeNotifier.ShowTest(metadata);
    }

    private void BuildSystemMenu()
    {
        SystemMenuHelper.AddSeparator(Handle);
        SystemMenuHelper.AddItem(Handle, CmdSettings, "Settings...");
        SystemMenuHelper.AddItem(Handle, CmdCastTo, "Cast to...");
        SystemMenuHelper.AddItem(Handle, CmdStartWithWindows, "Start with Windows");
        SystemMenuHelper.AddItem(Handle, CmdStartMinimized, "Start Minimized to Tray");
        SystemMenuHelper.AddItem(Handle, CmdMinimizeToTrayOnClose, "Minimize to Tray on Close");
        SystemMenuHelper.AddItem(Handle, CmdShowTrackChangeNotifications, "Show Notification on Track Change");
        SystemMenuHelper.AddSeparator(Handle);
        SystemMenuHelper.AddItem(Handle, CmdExit, "Exit");
    }

    protected override void WndProc(ref Message m)
    {
        // Refresh checkmarks/text just before the system menu is actually
        // shown, rather than trying to keep this menu and the tray's
        // ContextMenuStrip in sync with each other via events - simpler, and
        // correct regardless of which menu was used to change something last.
        if (m.Msg == WM_INITMENU)
        {
            SystemMenuHelper.SetChecked(Handle, CmdStartWithWindows, StartupManager.IsEnabled());
            SystemMenuHelper.SetChecked(Handle, CmdStartMinimized, _settings.StartMinimizedToTray);
            SystemMenuHelper.SetChecked(Handle, CmdMinimizeToTrayOnClose, _settings.MinimizeToTrayOnClose);
            SystemMenuHelper.SetChecked(Handle, CmdShowTrackChangeNotifications, _settings.ShowTrackChangeNotifications);
        }
        else if (m.Msg == WM_SYSCOMMAND)
        {
            // Windows may set the low 4 bits of the command ID itself, so
            // mask them off before comparing (the same reason the IDs above
            // are round multiples of 0x10).
            int cmd = (int)m.WParam & 0xFFF0;
            if (HandleSystemMenuCommand(cmd))
                return;
        }

        base.WndProc(ref m);
    }

    private bool HandleSystemMenuCommand(int cmd)
    {
        switch (cmd)
        {
            case CmdSettings:
                using (var settingsForm = new SettingsForm(_settings, this))
                    settingsForm.ShowDialog(this);
                return true;

            case CmdCastTo:
                _ = CastMenuHelper.ShowAsync(_castService, Cursor.Position);
                return true;

            case CmdStartWithWindows:
                bool startWithWindows = !StartupManager.IsEnabled();
                StartupManager.SetEnabled(startWithWindows);
                return true;

            case CmdStartMinimized:
                _settings.StartMinimizedToTray = !_settings.StartMinimizedToTray;
                SettingsStore.Save(_settings);
                return true;

            case CmdMinimizeToTrayOnClose:
                _settings.MinimizeToTrayOnClose = !_settings.MinimizeToTrayOnClose;
                SettingsStore.Save(_settings);
                return true;

            case CmdShowTrackChangeNotifications:
                _settings.ShowTrackChangeNotifications = !_settings.ShowTrackChangeNotifications;
                SettingsStore.Save(_settings);
                return true;

            case CmdExit:
                ExitRequested?.Invoke();
                return true;

            default:
                return false;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _formIcon?.Dispose();
            _smtc?.Dispose();
            _nowPlaying.Dispose();
            _volume.Dispose();
            _lastFm.Dispose();
            _discord.Dispose();
        }
        base.Dispose(disposing);
    }
}

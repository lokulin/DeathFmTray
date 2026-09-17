using System;
using System.Drawing;
using System.IO;
using System.Text.Json;
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
    private bool _allowClose;
    private Icon? _formIcon;
    private SmtcService? _smtc;

    // death.fm's chat module sends guests here (see OnNavigationStarting).
    private const string AccountPageUrlFragment = "modules.php?name=Your_Account";

    /// <summary>Raised whenever playback starts/stops/pauses - used by TrayAppContext to reflect state in the tray icon.</summary>
    public event Action<PlaybackState>? PlaybackStateChanged;

    public PlayerForm(AppSettings settings)
    {
        _settings = settings;
        _nowPlaying = new NowPlayingService(_webView);

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

            // Own SMTC session bound to this window, instead of relying on
            // WebView2/Chromium's navigator.mediaSession auto-bridging (which
            // registers under msedgewebview2.exe's identity, not ours - see
            // SmtcService for the full story).
            _smtc = new SmtcService(Handle);
            _smtc.ButtonPressed += OnSmtcButtonPressed;
            _nowPlaying.MetadataChanged += OnNowPlayingMetadataChanged;
            _nowPlaying.PlaybackStateChanged += OnNowPlayingPlaybackStateChanged;
            _nowPlaying.ThemeChanged += OnThemeChanged;

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
        if (!e.Uri.Contains(AccountPageUrlFragment, StringComparison.OrdinalIgnoreCase))
            return;

        e.Cancel = true;

        string urlJson = JsonSerializer.Serialize(e.Uri);
        _ = _webView.CoreWebView2?.ExecuteScriptAsync($"({LoginOverlayScript})({urlJson});");
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
        // f.src = f.src is a no-op (same URL, browsers skip the reload) -
        // contentWindow.reload() forces an actual refresh of just these two
        // iframes so newly-logged-in state shows up, without touching the
        // rest of the page (and so without interrupting playback).
        document.querySelectorAll('.chat-view, .chat-input').forEach(function (f) {
            try { f.contentWindow.location.reload(); } catch (e) { /* ignore */ }
        });
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
        _smtc?.SetPlaybackStatus(state switch
        {
            PlaybackState.Playing => MediaPlaybackStatus.Playing,
            PlaybackState.Paused => MediaPlaybackStatus.Paused,
            PlaybackState.Waiting => MediaPlaybackStatus.Changing,
            _ => MediaPlaybackStatus.Stopped,
        });
        PlaybackStateChanged?.Invoke(state);
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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _formIcon?.Dispose();
            _smtc?.Dispose();
            _nowPlaying.Dispose();
        }
        base.Dispose(disposing);
    }
}

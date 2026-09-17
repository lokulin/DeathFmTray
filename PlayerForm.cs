using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
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
            var options = new Microsoft.Web.WebView2.Core.CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments = "--disable-features=HardwareMediaKeyHandling"
            };

            var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: userDataFolder,
                options: options);

            await _webView.EnsureCoreWebView2Async(env);

            // Own SMTC session bound to this window, instead of relying on
            // WebView2/Chromium's navigator.mediaSession auto-bridging (which
            // registers under msedgewebview2.exe's identity, not ours - see
            // SmtcService for the full story).
            _smtc = new SmtcService(Handle);
            _smtc.ButtonPressed += OnSmtcButtonPressed;
            _nowPlaying.MetadataChanged += OnNowPlayingMetadataChanged;
            _nowPlaying.PlaybackStateChanged += OnNowPlayingPlaybackStateChanged;

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

    private void OnNowPlayingPlaybackStateChanged(PlaybackState state)
    {
        _smtc?.SetPlaybackStatus(state switch
        {
            PlaybackState.Playing => MediaPlaybackStatus.Playing,
            PlaybackState.Paused => MediaPlaybackStatus.Paused,
            PlaybackState.Waiting => MediaPlaybackStatus.Changing,
            _ => MediaPlaybackStatus.Stopped,
        });
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
        }
        base.Dispose(disposing);
    }
}

using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Microsoft.Web.WebView2.WinForms;

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

    public PlayerForm(AppSettings settings)
    {
        _settings = settings;
        _nowPlaying = new NowPlayingService(_webView);

        Text = "Death.FM Player";

        // Fixed size — the death.fm player layout doesn't reflow well when the
        // window is resized, so we lock both dimensions and remove the maximize
        // button / size grips.
        var size = new Size(
            Math.Max(settings.WindowWidth, 1024),
            Math.Max(settings.WindowHeight, 500));
        MinimumSize = size;
        MaximumSize = size;
        ClientSize = size; // sets the client area; Form will add chrome around it
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
            Icon = new Icon(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));
        }
        catch
        {
            // Missing/invalid icon file shouldn't stop the app from running.
        }
    }

    private async void PlayerForm_Load(object? sender, EventArgs e)
    {
        await _webView.EnsureCoreWebView2Async();

        // Registers the Media Session injection script before the first
        // navigation so it's guaranteed to run on page load (and every
        // reload thereafter) - see NowPlayingService.cs for what it does.
        await _nowPlaying.StartAsync();

        _webView.CoreWebView2.Navigate(_settings.StationUrl);
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
            _settings.WindowWidth = Width;
            _settings.WindowHeight = Height;
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
}
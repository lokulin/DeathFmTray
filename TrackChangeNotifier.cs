using System;
using System.IO;
using System.Net.Http;
using System.Security;
using System.Threading.Tasks;
using System.Windows.Forms;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace DeathFmTray;

/// <summary>
/// Shows a Windows toast notification whenever the currently playing track
/// changes, fed by the same <see cref="NowPlayingService"/> events
/// <see cref="SmtcService"/> and <see cref="LastFmScrobbler"/> already use.
///
/// Deliberately gated on actually playing, not just "metadata changed":
/// NowPlayingService's injected script polls the page's now-playing info
/// every 5s regardless of our own &lt;audio&gt; element's state (the live
/// stream keeps broadcasting whether or not we're pulling it), so without
/// this guard a track change while stopped/idle would still pop a
/// notification for something nobody's actually listening to.
/// </summary>
public sealed class TrackChangeNotifier
{
    // Must stay in sync with Program.AppUserModelId - same convention
    // AumidShortcutHelper already follows. Toasts from an unpackaged app are
    // silently dropped without a valid AUMID (already set up for SMTC/the
    // Start Menu shortcut, so nothing extra is needed beyond reusing it here).
    private const string AppUserModelId = "TerraEclectic.DeathFmTray.v2";

    // Windows' toast notification platform fetches a remote <image src>
    // itself, but on a cold ShellExperienceHost network stack (e.g. the very
    // first toast after launch) that fetch can be slow enough to miss
    // whatever internal timeout it uses - the toast still shows, just with no
    // art, and it never retries (confirmed live: SMTC's own art, fetched via
    // RandomAccessStreamReference from inside our own process, showed up fine
    // for the same track/URL that produced an art-less toast). Downloading it
    // ourselves first and pointing the toast at a local file sidesteps that
    // race entirely.
    private static readonly HttpClient s_http = new();
    private static readonly string ArtCachePath = Path.Combine(Path.GetTempPath(), "DeathFmTray", "now-playing-art.jpg");

    private readonly AppSettings _settings;
    private bool _isPlaying;

    public TrackChangeNotifier(AppSettings settings)
    {
        _settings = settings;
    }

    public void OnPlaybackStateChanged(PlaybackState state)
    {
        _isPlaying = state == PlaybackState.Playing;
    }

    public void OnMetadataChanged(NowPlayingMetadata metadata)
    {
        if (!_isPlaying || !_settings.ShowTrackChangeNotifications)
        {
            return;
        }

        _ = ShowAsync(metadata, reportErrors: false);
    }

    /// <summary>
    /// Fires a toast immediately, ignoring both the playing-state and the
    /// enabled-setting gates - used by the tray/system menu's "Send Test
    /// Notification" action so toggling the setting on can be verified
    /// without waiting for a real track change (or even needing to be
    /// playing at all).
    /// </summary>
    public void ShowTest(NowPlayingMetadata metadata)
    {
        _ = ShowAsync(metadata, reportErrors: true);
    }

    private static async Task ShowAsync(NowPlayingMetadata metadata, bool reportErrors)
    {
        try
        {
            string? localArtPath = await DownloadArtAsync(metadata.ArtUrl);
            Show(metadata, localArtPath);
        }
        catch (Exception ex)
        {
            if (reportErrors)
            {
                MessageBox.Show(
                    $"Couldn't show a test notification:\n\n{ex.Message}",
                    "Death.FM Player",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
            // Otherwise best-effort - a notification failing to show shouldn't affect playback.
        }
    }

    // Best-effort: any failure here (bad URL, network hiccup, slow response)
    // just means the toast goes out without art, same as ArtUrl being empty.
    private static async Task<string?> DownloadArtAsync(string? artUrl)
    {
        if (string.IsNullOrEmpty(artUrl))
        {
            return null;
        }

        try
        {
            byte[] bytes = await s_http.GetByteArrayAsync(artUrl);
            Directory.CreateDirectory(Path.GetDirectoryName(ArtCachePath)!);
            await File.WriteAllBytesAsync(ArtCachePath, bytes);
            return ArtCachePath;
        }
        catch
        {
            return null;
        }
    }

    private static void Show(NowPlayingMetadata metadata, string? localArtPath)
    {
        // hint-crop="circle" + appLogoOverride matches how most media-player
        // toasts present album art. localArtPath is a local file downloaded
        // by DownloadArtAsync above - toast image sources need to be either a
        // remote https URL or a local file URI, and a local one is both
        // faster and more reliable to load than fetching in place (see the
        // s_http remarks above).
        string imageNode = localArtPath is null
            ? ""
            : $"<image placement=\"appLogoOverride\" hint-crop=\"circle\" src=\"{SecurityElement.Escape(new Uri(localArtPath).AbsoluteUri)}\"/>";

        string xml = $"""
            <toast>
                <visual>
                    <binding template="ToastGeneric">
                        <text>{SecurityElement.Escape(metadata.Title)}</text>
                        <text>{SecurityElement.Escape(metadata.Artist)}</text>
                        {imageNode}
                    </binding>
                </visual>
            </toast>
            """;

        var doc = new XmlDocument();
        doc.LoadXml(xml);
        var toast = new ToastNotification(doc);
        ToastNotificationManager.CreateToastNotifier(AppUserModelId).Show(toast);
    }
}

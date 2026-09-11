using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DeathFmTray;

/// <summary>
/// Applies a dark, theme-matched titlebar using undocumented-but-stable DWM
/// window attributes. Two separate calls, layered for compatibility:
///
///   - DWMWA_USE_IMMERSIVE_DARK_MODE (Windows 10 1809+ / all of Windows 11):
///     switches the titlebar to Windows' built-in dark variant (dark
///     background, white text). No custom color control.
///
///   - DWMWA_CAPTION_COLOR / DWMWA_TEXT_COLOR (Windows 11 22H2+ only):
///     lets us set an exact custom color, so the titlebar can actually match
///     death.fm's near-black/red palette instead of just "generic dark".
///
/// On anything older than Windows 11 22H2 the caption/text color calls simply
/// fail (non-zero HRESULT, which we ignore) and the immersive-dark-mode call
/// is what's actually doing the work - still a big improvement over the
/// default white titlebar.
/// </summary>
internal static class WindowChromeHelper
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    public static void ApplyDarkTitleBar(Form form, Color captionColor, Color textColor)
    {
        // DwmSetWindowAttribute has existed since Vista, but the specific
        // attributes above only do anything on 10.0.17763+ - on anything
        // older this whole call is a harmless, ignored no-op.
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            return;
        }

        IntPtr hwnd = form.Handle;

        int enableDarkMode = 1;
        DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref enableDarkMode, sizeof(int));

        int captionColorRef = ToColorRef(captionColor);
        DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref captionColorRef, sizeof(int));

        int textColorRef = ToColorRef(textColor);
        DwmSetWindowAttribute(hwnd, DwmwaTextColor, ref textColorRef, sizeof(int));
    }

    // Win32 COLORREF is 0x00BBGGRR, not the 0x00RRGGBB most .NET code expects.
    private static int ToColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);
}

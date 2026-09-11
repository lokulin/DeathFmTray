using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace DeathFmTray;

internal static class Program
{
    // Named mutex so a second launch (e.g. double-clicking the shortcut again)
    // notifies the user instead of spawning a duplicate tray icon.
    private const string SingleInstanceMutexName = "DeathFmTray_SingleInstance_2f6a2f0e";

    // Identifies this process to Windows for taskbar grouping, Alt+Tab, and
    // (importantly) the name shown in the volume mixer / media flyout for the
    // WebView2-hosted audio - without this it shows up as "Unknown app".
    // Must be set before the WebView2 environment is created (i.e. before
    // EnsureCoreWebView2Async runs in PlayerForm), so we do it here, first thing.
    private const string AppUserModelId = "TerraEclectic.DeathFmTray";

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    [STAThread]
    private static void Main()
    {
        SetCurrentProcessExplicitAppUserModelID(AppUserModelId);

        // Unpackaged apps need a Start Menu shortcut stamped with the same
        // AUMID before Windows will show a friendly name (instead of
        // "Unknown app") in the volume mixer / media flyout. Harmless no-op
        // when the shortcut already exists.
        AumidShortcutHelper.EnsureStartMenuShortcut();

        ApplicationConfiguration.Initialize();

        using var singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool isNewInstance);

        if (!isNewInstance)
        {
            MessageBox.Show(
                "Death.FM Player is already running - check your system tray.",
                "Death.FM Player",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        Application.Run(new TrayAppContext());

        GC.KeepAlive(singleInstanceMutex);
    }
}
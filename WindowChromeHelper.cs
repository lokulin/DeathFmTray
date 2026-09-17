using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DeathFmTray;

/// <summary>
/// Applies a dark, theme-matched titlebar using DWM window attributes, and
/// stamps the process AppUserModelID onto the window so Windows groups it
/// correctly for taskbar / media flyout purposes.
/// </summary>
internal static class WindowChromeHelper
{
    // Must stay in sync with Program.AppUserModelId / AumidShortcutHelper.
    private const string AppUserModelId = "TerraEclectic.DeathFmTray.v2";

    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHGetPropertyStoreForWindow(
        IntPtr hwnd,
        ref Guid riid,
        out IPropertyStore ppv);

    public static void ApplyDarkTitleBar(Form form, Color captionColor, Color textColor)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
            return;

        IntPtr hwnd = form.Handle;

        int enableDarkMode = 1;
        DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref enableDarkMode, sizeof(int));

        int captionColorRef = ToColorRef(captionColor);
        DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref captionColorRef, sizeof(int));

        int textColorRef = ToColorRef(textColor);
        DwmSetWindowAttribute(hwnd, DwmwaTextColor, ref textColorRef, sizeof(int));

        // Also stamp AUMID at the window level (in addition to the process-level
        // SetCurrentProcessExplicitAppUserModelID call in Program.Main). Window-
        // level overrides process-level and helps the shell associate this HWND
        // with the Start Menu shortcut we create for the friendly display name.
        TrySetWindowAppUserModelId(hwnd, AppUserModelId);
    }

    private static void TrySetWindowAppUserModelId(IntPtr hwnd, string appId)
    {
        try
        {
            Guid iid = typeof(IPropertyStore).GUID;
            int hr = SHGetPropertyStoreForWindow(hwnd, ref iid, out IPropertyStore? store);
            if (hr < 0 || store is null)
                return;

            try
            {
                var key = new PropertyKey(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5); // PKEY_AppUserModel_ID
                IntPtr strPtr = Marshal.StringToCoTaskMemUni(appId);
                try
                {
                    var pv = new PropVariant { vt = 31, pointerValue = strPtr }; // VT_LPWSTR
                    store.SetValue(ref key, ref pv);
                    store.Commit();
                }
                finally
                {
                    Marshal.FreeCoTaskMem(strPtr);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(store);
            }
        }
        catch
        {
            // Best-effort only.
        }
    }

    // Win32 COLORREF is 0x00BBGGRR.
    private static int ToColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey
    {
        public Guid fmtid;
        public int pid;
        public PropertyKey(Guid fmtid, int pid) { this.fmtid = fmtid; this.pid = pid; }
    }

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr pointerValue;
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint cProps);
        [PreserveSig] int GetAt(uint iProp, out PropertyKey pkey);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant pv);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant pv);
        [PreserveSig] int Commit();
    }
}

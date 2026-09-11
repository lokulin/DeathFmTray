using System;
using System.IO;
using System.Runtime.InteropServices;

namespace DeathFmTray;

/// <summary>
/// Ensures a Start Menu shortcut exists that carries the process AppUserModelID.
/// Windows uses that shortcut to resolve a friendly display name for the media
/// flyout / volume mixer; without it an unpackaged exe shows as "Unknown app".
/// Completely best-effort — any failure is swallowed so the app always starts.
/// </summary>
internal static class AumidShortcutHelper
{
    // Must stay in sync with Program.AppUserModelId.
    private const string AppUserModelId = "TerraEclectic.DeathFmTray";
    private const string ShortcutName = "Death.FM Player.lnk";

    public static void EnsureStartMenuShortcut()
    {
        try
        {
            string? exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
                return;

            string programsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Windows\Start Menu\Programs");

            Directory.CreateDirectory(programsDir);
            string shortcutPath = Path.Combine(programsDir, ShortcutName);

            // 1. Create / refresh the basic .lnk via the reliable WScript.Shell COM object.
            CreateBasicShortcut(shortcutPath, exePath);

            // 2. Stamp the AUMID property (separate step; failure here still leaves a usable shortcut).
            TrySetAppUserModelId(shortcutPath, AppUserModelId);
        }
        catch
        {
            // Never prevent the app from launching.
        }
    }

    private static void CreateBasicShortcut(string shortcutPath, string exePath)
    {
        // WScript.Shell is the most reliable way to create a .lnk from managed code.
        Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null)
            return;

        object? shell = Activator.CreateInstance(shellType);
        if (shell is null)
            return;

        try
        {
            object? shortcut = shellType.InvokeMember(
                "CreateShortcut",
                System.Reflection.BindingFlags.InvokeMethod,
                null,
                shell,
                new object[] { shortcutPath });

            if (shortcut is null)
                return;

            Type shortcutType = shortcut.GetType();
            shortcutType.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { exePath });
            shortcutType.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { Path.GetDirectoryName(exePath)! });
            shortcutType.InvokeMember("IconLocation", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { exePath });
            shortcutType.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, shortcut, null);

            Marshal.FinalReleaseComObject(shortcut);
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
    }

    private static void TrySetAppUserModelId(string shortcutPath, string appId)
    {
        try
        {
            var link = (IShellLinkW)new CShellLink();
            var file = (IPersistFile)link;
            file.Load(shortcutPath, 2); // STGM_READWRITE

            var store = (IPropertyStore)link;
            var key = new PropertyKey(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5); // PKEY_AppUserModel_ID

            IntPtr strPtr = Marshal.StringToCoTaskMemUni(appId);
            try
            {
                var pv = new PropVariant { vt = 31, pointerValue = strPtr }; // VT_LPWSTR
                int hr = store.SetValue(ref key, ref pv);
                if (hr >= 0)
                    hr = store.Commit();
                if (hr < 0)
                    return;
            }
            finally
            {
                Marshal.FreeCoTaskMem(strPtr);
            }

            file.Save(shortcutPath, true);
        }
        catch
        {
            // AUMID stamp failed — the plain shortcut is still better than nothing.
        }
    }

    // -------------------------------------------------------------------------
    // Minimal COM interop. Only the methods we actually call are declared,
    // and they are in the correct vtable order so QI works.
    // -------------------------------------------------------------------------

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class CShellLink { }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszFile, int cch, IntPtr pfd, int fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
        void Resolve(IntPtr hwnd, int fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey
    {
        public Guid fmtid;
        public int pid;
        public PropertyKey(Guid fmtid, int pid) { this.fmtid = fmtid; this.pid = pid; }
    }

    // PROPVARIANT is 16 bytes on x64 (vt + reserved + pointer)
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
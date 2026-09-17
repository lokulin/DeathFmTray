# Death.FM Tray Player

A tiny Windows tray app that wraps the [Death.FM](https://death.fm) web player
(`death.fm/player.php?station=dfm`) so it launches like a native app, sits in
the system tray instead of the taskbar, and minimizes cleanly instead of
cluttering your desktop.

## What's here

| File | Purpose |
|---|---|
| `Program.cs` | Entry point. Guards against a second instance being launched. |
| `TrayAppContext.cs` | Owns the `NotifyIcon`, tray context menu, and overall app lifetime. |
| `PlayerForm.cs` | The window itself - hosts the WebView2 control pointed at the player page, handles minimize/close-to-tray. |
| `SettingsStore.cs` | Loads/saves user preferences as JSON in `%AppData%\DeathFmTray\settings.json`. |
| `StartupManager.cs` | Adds/removes a "run at Windows startup" entry via the per-user registry Run key (no installer/admin rights needed). |
| `NowPlayingService.cs` | Scrapes the page's now-playing DOM/audio element and bridges it to C# via `postMessage`, instead of `navigator.mediaSession`. |
| `SmtcService.cs` | Drives Windows' System Media Transport Controls (volume flyout / Now Playing) directly from our own process, fed by `NowPlayingService`. |
| `AumidShortcutHelper.cs` | Creates a Start Menu shortcut stamped with the process AUMID so the media flyout shows "Death.FM Player" instead of "Unknown app". |
| `WindowChromeHelper.cs` | Applies a dark, theme-matched titlebar via DWM window attributes. |
| `tools/create-start-menu-shortcut.ps1` | Standalone PowerShell equivalent of the AUMID shortcut helper (optional; the app now does this itself). |
| `Assets/app.ico`, `Assets/tray.ico` | App and tray icons, embedded into the assembly at build time. |

## Prerequisites

- **.NET 8 SDK** ([dotnet.microsoft.com](https://dotnet.microsoft.com/download))
- **VS Code** with the C# Dev Kit extension (or plain `dotnet` CLI - both work)
- **WebView2 Runtime** - already preinstalled on Windows 11 and most up-to-date
  Windows 10 machines. If it's missing, Windows will prompt for the
  [Evergreen Bootstrapper](https://developer.microsoft.com/microsoft-edge/webview2/)
  the first time you run the app.

## Building & running

```powershell
cd DeathFmTray
dotnet restore
dotnet run
```

Or just hit F5 in VS Code once you've opened the folder (it'll offer to
generate `.vscode/launch.json` / `tasks.json` for you the first time).

To produce a single portable .exe you can drop a shortcut to:

```powershell
dotnet publish DeathFmTray.csproj -c Release -r win-x64 --self-contained false `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:DebugType=embedded `
  -p:GenerateDocumentationFile=false `
  -p:CopyDebugSymbolFilesFromPackages=false `
  -p:CopyDocumentationFilesFromPackages=false `
  -o publish
```

(Drop `--self-contained false` and add `--self-contained true` instead if you
want it to run on a machine without the .NET 8 runtime installed - the output
will just be a lot bigger.)

## Using it

- Launching the app opens the player window and drops an icon in the tray.
- Closing the window (the X button) minimizes it to the tray rather than
  quitting - use the tray icon's right-click menu → **Exit** to actually quit.
- Right-click the tray icon for:
  - **Show Player** - restore/focus the window
  - **Start with Windows** - toggles a registry Run-key entry
  - **Start Minimized to Tray** - skip showing the window on launch
  - **Minimize to Tray on Close** - untick if you'd rather the X button
    actually close the app
  - **Exit**

Double-clicking the tray icon also restores the window.

## Windows media controls (volume flyout / Now Playing)

An earlier version of this used the browser's
[Media Session API](https://developer.mozilla.org/en-US/docs/Web/API/Media_Session_API)
(`navigator.mediaSession`) and let WebView2/Chromium auto-bridge it to
Windows' System Media Transport Controls (SMTC). That turned out to be a
dead end: WebView2 runs the actual browser engine in a separate
`msedgewebview2.exe` child process, and *that* process - not ours - is what
registers the SMTC session, under its own identity. No AUMID/shortcut work on
`DeathFmTray.exe` itself can fix that, and even worse, Chromium auto-creates
a *second*, generic SMTC session for any page playing audio - using the page
title as a fallback - regardless of whether the page touches
`navigator.mediaSession` at all. Windows would show that second, wrongly-
attributed session instead of ours.

The actual fix, in two parts:

- **`NowPlayingService.cs`** scrapes `#np-track` / `#np-artist` / `#np-album`
  / `#now-playing-art` and the `<audio id="audio-engine">` element's
  `playing`/`pause`/`waiting` events, and forwards them to C# via
  `window.chrome.webview.postMessage(...)` - instead of setting
  `navigator.mediaSession` at all.
- **`SmtcService.cs`** takes that data and drives
  `Windows.Media.SystemMediaTransportControls` directly from our own process,
  via the classic `ISystemMediaTransportControlsInterop.GetForWindow` desktop
  interop entry point (there's no managed API for this - `GetForCurrentView()`
  only works for UWP apps with a `CoreWindow`). Since our own process already
  carries the correct AUMID (see below), the resulting SMTC session correctly
  shows up as "Death.FM Player".
- `PlayerForm.cs` also passes
  `--disable-features=HardwareMediaKeyHandling` as a WebView2 browser
  argument, which stops Chromium from creating its own competing SMTC session
  in the first place.

SMTC's `GetForWindow` interop is notoriously undocumented in managed .NET;
getting it working took a few rounds of live debugging (an
`AccessViolationException` from an assumed vtable slot that turned out to be
wrong - the factory interface actually derives from `IInspectable`, not
`IUnknown` - then an `InvalidCastException` from requesting the wrong
interface IID). If you ever need to touch that file again, `Marshal.
QueryInterface` against a live pointer is a much safer way to check vtable
assumptions than guessing and re-running.

## Fixing "Unknown app" in the volume mixer / media flyout

Two things have to both be true for Windows to show "Death.FM Player" instead
of "Unknown app" or a raw AUMID string:

1. **The process needs an AUMID**, so `Program.cs` calls
   `SetCurrentProcessExplicitAppUserModelID` before the WebView2 environment
   is created (and `SmtcService` piggybacks on that same identity when it
   registers with SMTC).
2. **Windows needs a way to resolve that AUMID to a friendly name.** For an
   app with no installer/MSIX package, that means a Start Menu shortcut
   carrying the same AUMID as a file property. `AumidShortcutHelper` creates
   (or refreshes) one automatically on every launch, at
   `%AppData%\Microsoft\Windows\Start Menu\Programs\Death.FM Player.lnk` -
   no manual step required, and you can safely delete it from the Start Menu
   afterwards if you don't want it listed there (Windows caches the
   AUMID→name resolution once it's been resolved once).

If the friendly name doesn't appear immediately after the first run, sign
out/in or reboot; Windows can be slow to invalidate its cache. If you ever
change the AUMID constant (in `Program.cs`, kept in sync with
`AumidShortcutHelper.cs`), bump the string (e.g. add a `.v2` suffix) rather
than reusing the old one - Windows can otherwise keep an old "Unknown app"
resolution cached against it indefinitely.

A standalone PowerShell version of the shortcut-creation logic remains in
`tools/create-start-menu-shortcut.ps1` if you ever need to re-apply it by
hand.

## Custom titlebar

`WindowChromeHelper.cs` sets a dark, theme-matched titlebar via DWM window
attributes (`DWMWA_CAPTION_COLOR`/`DWMWA_TEXT_COLOR`), applied in
`PlayerForm`'s constructor once the window handle exists. On Windows 11 22H2+
you get an exact color match to death.fm's near-black palette; on older
Windows 10/11 builds that don't support custom caption colors, it falls back
to Windows' generic dark titlebar (`DWMWA_USE_IMMERSIVE_DARK_MODE`) instead -
still dark, just not an exact color match. Both calls silently no-op on
anything older than Windows 10 1809, so this is safe to leave in regardless
of target OS. Adjust the colors passed to `ApplyDarkTitleBar` in
`PlayerForm.cs` if you want something other than the current near-black/white
combo.

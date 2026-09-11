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
| `NowPlayingService.cs` | Injects Media Session API wiring into the page so Windows' volume flyout/media controls show the current track, artist, and album art. |
| `WindowChromeHelper.cs` | Applies a dark, theme-matched titlebar via DWM window attributes. |
| `tools/create-start-menu-shortcut.ps1` | One-time script to fix the "Unknown app" label in the media flyout - see below. |
| `Assets/app.ico`, `Assets/tray.ico` | Placeholder icons (a plain red-on-black "D" badge) - swap these for real artwork whenever you like, same filenames. |

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
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
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

## Replacing the placeholder icons

`Assets/app.ico` and `Assets/tray.ico` are simple generated placeholders.
Replace them with real `.ico` files of the same name (multi-resolution .ico
files - e.g. 16/32/48/256px - work best so Windows can pick the right size for
the taskbar, alt-tab, and tray). No code changes needed; both are loaded by
filename at runtime and `app.ico` also doubles as the compiled exe's icon via
`<ApplicationIcon>` in the `.csproj`.

## Windows media controls (volume flyout / Now Playing)

Implemented via `NowPlayingService.cs` - and it turns out to be simpler than
raw WinRT SMTC code. WebView2 is Chromium under the hood, and Chromium
already publishes whatever the page sets via the browser's
[Media Session API](https://developer.mozilla.org/en-US/docs/Web/API/Media_Session_API)
(`navigator.mediaSession`) straight through to Windows' System Media
Transport Controls - no manual `Windows.Media.*` wiring needed on the C# side.

The death.fm page itself never calls that API, so `NowPlayingService` injects
a small script (`AddScriptToExecuteOnDocumentCreatedAsync`) that:

- Mirrors `#np-track` / `#np-artist` / `#np-album` / `#now-playing-art` into
  `navigator.mediaSession.metadata` (title, artist, album, artwork) whenever
  they change, which is what feeds the title/artist text and album art shown
  in Windows' volume flyout and media overlay.
- Wires SMTC's Play/Pause/Stop actions back to the page's own
  `#btn-play` / `#btn-stop` buttons. Death.FM is a live stream with no real
  "pause" position, so Pause is mapped to the same action as Stop.
- Reflects the `<audio id="audio-engine">` element's native `playing` /
  `pause` / `waiting` events into `navigator.mediaSession.playbackState`.

If Windows still shows a generic name instead of "Death.FM Player": `Program.cs`
sets an explicit AppUserModelID (`SetCurrentProcessExplicitAppUserModelID`)
before the WebView2 environment is created, and the `.csproj` sets the exe's
`Product`/`AssemblyTitle` metadata as a fallback - but for an unpackaged .exe
that's necessary and *not* sufficient. See "Fixing 'Unknown app'" below.

## Fixing "Unknown app" in the volume mixer / media flyout

Setting the AUMID at runtime tells Windows how to *group* the app's windows,
but for an app with no installer, Windows still needs a Start Menu shortcut
carrying that same AUMID as a file property before it has a friendly name to
actually display. Without one, everything works except the label.

Run `tools/create-start-menu-shortcut.ps1` once, pointing it at your built
exe:

```powershell
.\tools\create-start-menu-shortcut.ps1 -ExePath "C:\path\to\DeathFmTray.exe"
```

This creates a Start Menu shortcut with the AUMID property stamped onto it
(using a small inline COM interop helper, since PowerShell's built-in
`WScript.Shell` shortcut object can't set that property on its own). It only
needs to be run once per machine/exe location - after that, Windows resolves
the AUMID to this shortcut's name regardless of how the app is actually
launched, and you can delete the shortcut from your Start Menu afterwards if
you don't want it cluttering there (Windows caches the resolution).

If it doesn't take effect immediately, sign out/in or reboot - Windows can be
slow to invalidate its AUMID→name cache.

I wrote this as a standalone script rather than baking the shortcut-creation
logic into the app itself: it relies on some fairly fiddly native COM
interop (`IPropertyStore`/`PROPVARIANT` marshaling), and getting that wrong
inside the shipped app risks a hard crash rather than just a script error you
can safely re-run. I also haven't been able to test it on a live Windows
machine myself, so treat it as best-effort - if it errors out or doesn't
work, the app still functions fine, you'll just keep seeing "Unknown app" as
a cosmetic issue.

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

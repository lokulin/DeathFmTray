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
| `NowPlayingService.cs` | Scrapes the page's now-playing DOM/audio element and bridges it to C# via `postMessage`, instead of `navigator.mediaSession`. Also runs a watchdog that reloads the page if playback gets stuck buffering. |
| `SmtcService.cs` | Drives Windows' System Media Transport Controls (volume flyout / Now Playing) directly from our own process, fed by `NowPlayingService`. |
| `LastFmScrobbler.cs` | Scrobbles now-playing tracks to Last.fm, fed by the same now-playing data. |
| `DiscordPresenceService.cs` | Shows the current track as a Discord Rich Presence status, fed by the same now-playing data. |
| `AumidShortcutHelper.cs` | Creates a Start Menu shortcut stamped with the process AUMID so the media flyout shows "Death.FM Player" instead of "Unknown app". |
| `WindowChromeHelper.cs` | Applies a dark titlebar via DWM window attributes, recolored per-station by `PlayerForm`. |
| `tools/create-start-menu-shortcut.ps1` | Standalone PowerShell equivalent of the AUMID shortcut helper (optional; the app now does this itself). |
| `tools/clear-webview-cache.ps1` | Stops the app and wipes its WebView2 profile (cookies/cache/storage) - handy for re-testing login/chat without a real logout. |
| `Assets/app.ico`, `Assets/tray.ico` | App and tray icons, embedded into the assembly at build time. |

## Prerequisites

- **.NET 10 SDK** ([dotnet.microsoft.com](https://dotnet.microsoft.com/download))
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
want it to run on a machine without the .NET 10 runtime installed - the output
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
  - **Connect Last.fm...** / **Disconnect Last.fm (username)** - see
    [Last.fm scrobbling](#lastfm-scrobbling) below
  - **Exit**

Double-clicking the tray icon also restores the window. The tray icon itself
gets a small green dot overlaid on it while a stream is actively playing.

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

## Chat login, ratings, and external links

A few bits of `PlayerForm.cs` exist purely to work around how the embedded
death.fm page behaves inside a WebView2 shell instead of a normal browser tab:

- **Chat login/register.** The chat tab's guest login/register links do
  `window.top.location.href = '/modules.php?name=Your_Account'`, which
  replaces the *entire* player with death.fm's full site and no way back.
  `PlayerForm` intercepts that specific navigation
  (`CoreWebView2.NavigationStarting`) and shows it as an in-page overlay
  `<iframe>` instead - same WebView2, same cookie jar, and it doesn't touch
  the fixed 1050×550 layout. **Known limitation:** closing the overlay is
  supposed to refresh the chat panel to reflect the new login, but doesn't
  reliably pick it up yet, even though the session cookie is confirmed set
  correctly (checked the WebView2 profile's cookie database directly) - a
  full page reload does show it correctly, so `OnLoginOverlayClosed` does
  that instead (auto-resuming playback afterward so it isn't disruptive),
  but the underlying "why doesn't the chat iframe alone pick it up" question
  is still open. Parked as a known issue rather than chased further for now.
- **Album rating popup.** Rating an album (`window.open` from the Now
  Playing panel) used to refresh the *entire* player from the popup's
  `window.opener` after you rated, reloading the `<audio>` element and
  interrupting playback. That's just the same page navigating to itself, so
  it's cancelled and the page's own `updateTrackData()` is called instead -
  it already re-fetches the rating bar along with everything else on a
  timer.
- **External links.** Album art and the Amazon shop links use
  `target="_blank"`, which WebView2 otherwise handles by opening its own
  embedded popup window - fine for the rating popup, not what you want for
  "buy this on Amazon." `CoreWebView2.NewWindowRequested` redirects anything
  other than the rating popup to the OS default browser via
  `Process.Start(..., UseShellExecute = true)`.

## Last.fm scrobbling

`LastFmScrobbler.cs` talks to Last.fm's Audioscrobbler API (plain REST/JSON
over HTTPS - no native interop needed, unlike `SmtcService`). To use it:

1. Register a free API application at
   [last.fm/api/account/create](https://www.last.fm/api/account/create)
   (any name; leave the callback URL blank) to get an **API key** and
   **shared secret**.
2. Add them to `%AppData%\DeathFmTray\settings.json`:

   ```json
   "LastFmApiKey": "...",
   "LastFmApiSecret": "..."
   ```

   (This file is per-user and never committed to the repo - if you're
   building this for other people rather than just yourself, each person
   needs their own key rather than one baked into the source, since the
   repo is public.)
3. Right-click the tray icon → **Connect Last.fm...**. This opens Last.fm's
   authorization page in your default browser (desktop-app auth flow:
   `auth.getToken` → you approve in the browser → `auth.getSession`); once
   you confirm you've approved it, the resulting session key is saved and
   doesn't expire until you disconnect or revoke it from Last.fm's side.

Scrobbling itself is fed by `PlayerForm`, not `NowPlayingService` directly,
because the page's now-playing display updates on a timer regardless of
whether you've actually pressed Play - only forwarding track changes while
genuinely playing avoids scrobbling tracks you never actually heard. Since
death.fm is a live stream with no track-length metadata, "has this track
played long enough to scrobble" is approximated by elapsed wall-clock time
since the track was first seen (Last.fm's own guidance is roughly half the
track's length or 4 minutes, whichever is shorter, and only for tracks over
30 seconds - 30 seconds of elapsed time is used here as a simple proxy for
that, given real durations aren't available). Brief rebuffering doesn't
reset that timer - only an actual Stop does - since this stream rebuffers
often enough that treating every blip as a stop meant a track could play
for minutes total, across a few short interruptions, and never accumulate
enough continuous time to ever qualify.

## Discord Rich Presence

`DiscordPresenceService.cs` shows the current track as your Discord status,
via Discord's local RPC (a named pipe the desktop client listens on - the
[DiscordRichPresence](https://github.com/Lachee/discord-rpc-csharp) library
handles connecting/reconnecting to it in the background; nothing shows up
unless Discord is actually running). To use it:

1. Create a free application at
   [discord.com/developers/applications](https://discord.com/developers/applications)
   (any name) and copy its **Application ID** from the General Information
   page.
2. Add it to `%AppData%\DeathFmTray\settings.json`:

   ```json
   "DiscordClientId": "..."
   ```

   Same reasoning as Last.fm's key: per-user, not committed to source, since
   each person needs their own registered application.
3. Optional: under **Rich Presence → Art Assets** in that same application,
   upload a fallback image and add its asset key as
   `"DiscordDefaultImageKey"` in settings.json - shown when a track has no
   album art of its own yet. Album art (when available) is passed directly
   as an external image URL, which Discord accepts without needing to be
   pre-uploaded.

No further "connect" step is needed beyond that (unlike Last.fm) - it just
needs your own Discord client already running locally, no OAuth/user consent
involved. **Note:** this always shows as "Playing Death.FM Player" rather
than "Listening to ..." - the "Listening to Spotify" verb is a first-party
integration Discord built specifically for Spotify, not something exposed
through the general RPC protocol third-party apps use. Track/artist details
only show in the full profile popout (click your own name/avatar in
Discord), not the compact member-list entry.

## Custom titlebar

`WindowChromeHelper.cs` sets a dark titlebar via DWM window attributes
(`DWMWA_CAPTION_COLOR`/`DWMWA_TEXT_COLOR`), applied once the window handle
exists and re-applied by `PlayerForm.OnThemeChanged` whenever the page
reports a new `--theme-bg` CSS value - each station
(`?station=80s/afm/dfm/efm/sst`) bakes in its own color server-side, and
switching stations is a full page navigation, so the titlebar ends up
matching whichever station is currently loaded rather than always showing
death.fm's own near-black/red. On Windows 11 22H2+ you get an exact color
match; on older Windows 10/11 builds that don't support custom caption
colors, it falls back to Windows' generic dark titlebar
(`DWMWA_USE_IMMERSIVE_DARK_MODE`) instead - still dark, just not an exact
color match. Both calls silently no-op on anything older than Windows 10
1809, so this is safe to leave in regardless
of target OS. Adjust the colors passed to `ApplyDarkTitleBar` in
`PlayerForm.cs` if you want something other than the current near-black/white
combo.

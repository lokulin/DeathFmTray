# Death.FM Tray Player

A tiny Windows tray app that wraps the [Death.FM](https://death.fm) web player.

## About

Death.FM Tray Player wraps the [Death.FM](https://death.fm) web player
(`death.fm/player.php?station=dfm`) so it launches like a native app, sits in
the system tray instead of the taskbar, and minimizes cleanly instead of
cluttering your desktop - complete with proper Windows media controls,
Last.fm scrobbling, Discord Rich Presence, and Chromecast support.

## Installation

1. Download the latest release zip from the
   [Releases page](https://github.com/lokulin/DeathFmTray/releases).
2. Extract it anywhere you like.
3. Run `DeathFmTray.exe`.

Windows SmartScreen will likely warn that this is an unrecognized app the
first time you run it, since it isn't code-signed - click **More info**, then
**Run anyway**.

The app needs the **WebView2 Runtime**, which is already preinstalled on
Windows 11 and most up-to-date Windows 10 machines. If it's missing, Windows
will prompt you to install the
[Evergreen Bootstrapper](https://developer.microsoft.com/microsoft-edge/webview2/)
the first time you run the app.

## Developing

Requires the **.NET 10 SDK**. Quick start:

```powershell
cd DeathFmTray
dotnet restore
dotnet run
```

See [DEVELOPING.md](DEVELOPING.md) for the full architecture breakdown,
file-by-file notes, and release process.

## Known bugs

- Closing the chat login overlay is supposed to refresh the chat panel to
  reflect the new login, but doesn't reliably pick it up, even though the
  session cookie is set correctly. A full page reload works around it, and
  happens automatically right after the overlay closes.

## License

MIT - see [LICENSE](LICENSE).

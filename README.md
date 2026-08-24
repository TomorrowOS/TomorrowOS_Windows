# TomorrowOS Windows Player

Windows 11 Pro player for TomorrowOS — native `.NET` host + WebView2 renderer + Watchdog, speaking the same CMS device contract as Tizen and BrightSign.

## Architecture

| Process | Role |
| --- | --- |
| `TomorrowOS-Windows-Setup.exe` | Interactive / silent installer |
| `TomorrowOS.Watchdog.exe` | Login autostart, single-instance, restart on crash / stale heartbeat |
| `TomorrowOS.Player.exe` | Borderless fullscreen WebView2 host + native bridge |

Shared player UI/protocol lives in `core/` (`main.js`, `platform-windows.js`, `config.js`).

Native bridge methods (via `chrome.webview.postMessage`):

- `host.getBootstrap`, `app.heartbeat`
- `fs.resolve` / `fs.list` / `fs.mkdir`
- `download.start` / `download.cancel`
- `archive.extract`
- `device.captureScreenshot`, `device.reboot`
- `display.setMuted` (quiet / black overlay for V1)

Storage root: `%ProgramData%\TomorrowOS\storage`  
Settings: `%ProgramData%\TomorrowOS\settings.json`

## Requirements

- Windows 11 Pro x64 (V1 certification target)
- [.NET 8 SDK](https://dotnet.microsoft.com/download) to build
- WebView2 Runtime (Evergreen) on target machines

## Build

```bash
npm run build
```

Output:

- `build/windows/TomorrowOS-Windows-Setup.exe` — **single-file installer** (share this one file)
- `build/windows/payload/` — unpacked Player + Watchdog + uninstaller (dev / debugging)

## Install

### Interactive

Run `TomorrowOS-Windows-Setup.exe`, set:

- CMS endpoint
- Orientation
- Display index
- Maintenance passcode
- Optional hardening

### Silent

```bat
TomorrowOS-Windows-Setup.exe /silent /cms "https://your-cms.example.com" /passcode "change-me" /orientation landscape /display 0 /harden
```

### Uninstall

After install, run `TomorrowOS.Uninstall.exe` in the install folder. Confirm **Uninstall TomorrowOS Windows**, wait for the progress bar, then **Uninstall successful**.

## Runtime notes

- Playback starts from locally cached content when available (same local-first model as other players).
- Press **Ctrl+Shift+Alt+M**, then enter the maintenance passcode, to exit / restart Windows.
- Lab default passcode (only if installer settings are missing): `tomorrow`
- V1 video uses HTML/Media playback inside WebView2; native Media Foundation dual-buffer can replace it later without changing the CMS contract.
- HTML widgets are architecturally allowed via WebView2 isolation but are not the V1 certification focus.

## Dev launch (without installer)

```bat
dotnet publish host\TomorrowOS.Player\TomorrowOS.Player.csproj -c Debug -r win-x64 --self-contained false -o build\dev\player
dotnet publish host\TomorrowOS.Watchdog\TomorrowOS.Watchdog.csproj -c Debug -r win-x64 --self-contained false -o build\dev\watchdog
copy /Y build\dev\player\* build\dev\watchdog\
build\dev\watchdog\TomorrowOS.Player.exe
```

Set `core/config.js` `cmsEndpoint` before testing, or leave it empty to use the on-device CMS setup UI.

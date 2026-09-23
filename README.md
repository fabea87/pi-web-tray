# pi-web-tray

[![build](https://github.com/fabea87/pi-web-tray/actions/workflows/build.yml/badge.svg)](https://github.com/fabea87/pi-web-tray/actions/workflows/build.yml)

A tiny Windows tray app that supervises [pi-web](https://github.com/agegr/pi-web)
(`@agegr/pi-web`): it starts the server with Windows, keeps it alive when you close the
browser, never lets a second server start, and upgrades pi, its packages and pi-web from
one dialog.

`pi-web-tray.exe` is about 50 KB, needs no installer and no runtime download — it is built
against the .NET Framework 4.x that ships with Windows and uses roughly 30 MB of private
working set.

## Features

- **Autostart** — starts pi-web in the background at login (no console window). Toggleable
  from the tray menu.
- **Click to open** — left-click the tray icon to open `http://127.0.0.1:30141` in your
  default browser. Closing the browser tab does not stop the server.
- **Single instance** — if the port already answers, it only opens the browser. A second
  copy of the tray app exits by itself.
- **Crash watchdog** — restarts pi-web automatically (3 attempts, 5 s apart, reset after
  30 s of uptime).
- **Upgrades dialog** — shows installed vs latest versions for pi (agent), every package in
  `~/.pi/agent/settings.json`, and pi-web; updates the selected component or everything,
  streams every command's output, and restarts the pi-web server after a pi-web upgrade.
- **Settings file** — port, bind host, autostart-with-browser and log rotation in
  `%LOCALAPPDATA%\pi-web-tray\config.json`, reloaded the moment you save it.

## Requirements

- Windows 10 or 11 (x64).
- Node.js with pi-web installed globally:

  ```
  npm install -g @agegr/pi-web
  ```

- Optional: pi installed globally (`npm install -g @earendil-works/pi-coding-agent`) if you
  want the dialog to upgrade pi and its packages too.

## Install

1. Download `pi-web-tray.exe` (or the zip) from the
   [latest release](https://github.com/fabea87/pi-web-tray/releases/latest).
2. Put it wherever you like — no installer, no admin rights. Run it.
3. Right-click the tray icon → **Start with Windows**.

The exe is unsigned, so SmartScreen may ask once: *More info* → *Run anyway*.

It writes only two things outside its own folder: `%LOCALAPPDATA%\pi-web-tray\` (logs and
`config.json`) and, if you enable autostart, the value `PiWebTray` under
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.

## Tray menu

| Item | Action |
| --- | --- |
| Open Pi Web | Open the page in the default browser (starts the server first if needed) |
| Restart server | Kill the pi-web process tree and start it again |
| Upgrades... | Open the upgrades dialog |
| Start with Windows | Toggle the autostart registry entry |
| Auto-restart if it crashes | Toggle the watchdog |
| Edit settings (config.json) | Open the settings file (created with defaults on first use) |
| Open server log | `%LOCALAPPDATA%\pi-web-tray\server.log` |
| Exit (stop server) | Stop pi-web and remove the tray icon |
| status line | `Server: running on port 30141` / `stopped` / `starting...` |

The icon dot shows the state: green = running, grey = stopped, amber = starting/restarting.

## Upgrades dialog

| Component | Update command |
| --- | --- |
| pi (agent) | `pi update --self` |
| pi packages | `pi update --extension "npm:<source>"`, or `pi update --extensions` for all |
| pi-web | stop server → `npm install -g @agegr/pi-web@latest` → start server again |

Installed versions are read from the local `package.json` files; latest versions come
straight from the npm registry (one HTTP request per package, using the `registry` from
`~/.npmrc` when set). Rows with an available update are highlighted, and everything the
commands print is streamed into the dialog and appended to
`%LOCALAPPDATA%\pi-web-tray\upgrade.log`.

Upgrading **pi** does not restart your running `pi` session — the process keeps the code it
loaded, so start a new session to use the new version. pi-web is restarted automatically.

## Settings

`%LOCALAPPDATA%\pi-web-tray\config.json`, reloaded on save:

```json
{
  "_help": "port, host, openBrowserAtLogin, autoRestart, maxLogMB. Save the file; the tray reloads it automatically.",
  "port": 30141,
  "host": "127.0.0.1",
  "openBrowserAtLogin": false,
  "autoRestart": true,
  "maxLogMB": 2
}
```

Changing `port` or `host` restarts the server on the new address; `maxLogMB` rotates
`server.log` to `server.log.old` (0 = unlimited); `openBrowserAtLogin` also opens the page
when the tray starts. Invalid or missing values fall back to the defaults.

## Build from source

```
git clone https://github.com/fabea87/pi-web-tray
cd pi-web-tray
build.cmd
```

`build.cmd` compiles `src\*.cs` with the in-box .NET Framework compiler
(`%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`, with a Visual Studio Roslyn
fallback) and writes `pi-web-tray.exe` next to the script. Stop the running tray first
(right-click → Exit) — a running exe locks the output file.

Pushing a `v*` tag makes GitHub Actions build, package (`exe` + `zip` + `sha256`) and
publish a release automatically; branch and pull-request builds just upload the artifacts.

| Path | Role |
| --- | --- |
| `src/pi-web-tray.cs` | Tray app, settings, server supervision |
| `src/upgrade-form.cs` | Upgrades dialog |
| `src/paths.cs` | Shared paths, npm-registry lookups, version comparison |
| `src/AssemblyInfo.cs` | Version metadata (rewritten from the git tag during releases) |
| `src/app.manifest` | DPI awareness, Windows 10/11 compatibility, asInvoker |
| `assets/pi-web-tray.ico` | Application icon |

## How it works

The server is launched as:

```
node "<npm global dir>\@agegr\pi-web\bin\pi-web.js" --no-open -p 30141 -H 127.0.0.1
```

The tray opens the browser itself, which is why `--no-open` is used. Node, npm, the pi CLI
and the pi-web entry point are located at runtime, so upgrading pi-web needs no rebuild.

Stopping uses `taskkill /PID <listening pid> /T /F` (the Next.js child dies first and
`pi-web.js` then exits by itself), with a WMI sweep of leftover `node.exe ...pi-web...`
processes as a safety net. The port is probed with a TCP connect every 2 seconds.

## Notes

- Killing `pi-web-tray.exe` with Task Manager while pi-web is writing output can take
  pi-web down with it (broken stdout pipe). Run the exe again — it detects the dead server
  and starts it.
- If an npm upgrade warns `install-scripts ... not yet covered by allowScripts` (npm 11+),
  the install still completed: pi-web's only postinstall script is a macOS `chmod`
  workaround, and node-pty loads from its bundled prebuilds.
- Uninstall: **Exit (stop server)**, delete the exe, remove
  `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\PiWebTray` (or uncheck *Start with
  Windows* first) and delete `%LOCALAPPDATA%\pi-web-tray`.

## License

MIT — see [LICENSE](LICENSE). Not affiliated with the pi-web or pi projects.

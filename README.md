# DesktopSwitcher

A numbered button strip that docks in the Windows 10 taskbar, just left of the tray
icons, showing one button per virtual desktop with the current one highlighted.

```
 [1][2][3][4][+] │ ^ 🔊 ENG  17:22 
  ^current                  clock
```

Windows 10 gives no persistent indicator of which virtual desktop you are on, and no way
to jump straight to one — only `Win+Tab` and `Win+Ctrl+Left/Right`, which steps one
desktop at a time through an animation.

## Requirements

Windows 10, and nothing else. It builds with the C# compiler that ships in the box
(`.NET Framework 4.8`) and needs no SDK, no admin rights, and no installer.

Developed and verified against **Windows 10 22H2, build 19045.7548**. It relies on
undocumented shell COM interfaces whose GUIDs differ between Windows builds; 19045 is
the terminal Windows 10 build, so they are stable there. **It will not work on
Windows 11** without new interface GUIDs.

## Build

```cmd
build.cmd            :: windowed build (normal use)
build.cmd console    :: console build, keeps the selftest output visible
```

Output goes to `%LOCALAPPDATA%\DesktopSwitcher\DesktopSwitcher.exe`.

> The compiler used is **C# 5 only** — no string interpolation, `nameof`, `?.` or
> expression-bodied members anywhere in this tree.

## Use

Run the exe with no arguments. Then, on the strip:

| Action | Effect |
|---|---|
| Left click a number | switch to that desktop |
| Right click a number | send the window you were last using to that desktop |
| Middle click a number | remove that desktop |
| Left click `+` | create a desktop |

The button count follows your actual desktops: create one in Task View and a button
appears; remove one and it disappears. The strip is right-anchored, so the gap to the
clock never changes as it grows.

Right-click the tray icon for **Start with Windows**, config and log files, a manual
**Reload strip**, and **Exit**. There is no other way to quit — the strip has no chrome.

Windows forgets how many desktops you had after a reboot, so the count you were last
using is saved and restored at login.

## Configuration

`%APPDATA%\DesktopSwitcher\config.ini`, created on first run. Unknown keys are preserved,
malformed values fall back to defaults, and sizes are authored for 96 DPI and scaled to
your display automatically.

| Key | Default | Meaning |
|---|---|---|
| `lastCount` | `2` | desktop count restored at login |
| `buttonWidth` | `34` | per-button width at 96 DPI |
| `plusWidth` | `26` | width of the `+` button at 96 DPI |
| `margin` | `6` | gap between the strip and the tray icons |
| `reconcileMs` | `2000` | safety-net poll interval |
| `highlightColor` | `#0078D7` | underline bar under the current desktop |
| `backgroundColor` | *(blank)* | blank samples the live taskbar colour |
| `diagnostics` | `false` | enable the rolling log |

## Design notes

**The strip is a child of `Shell_TrayWnd`, not a floating always-on-top window.** That
inherits for free what a floating window would have to reimplement: it moves with the
taskbar, hides when the taskbar hides or a fullscreen app takes over, and shows on every
virtual desktop. The cost is that an Explorer restart destroys it, which is why the strip
is disposable and a watchdog rebuilds it.

**Desktop identity is a `Guid`, never an index.** Removing a desktop renumbers every one
after it, so an index captured before an async notification arrives can easily refer to a
different desktop by the time it is used.

**Updates are event-driven, with polling as a safety net.** Shell notifications drive the
UI, so the highlight changes instantly. A slow reconcile tick covers a dead notification
sink, missed events and Explorer restarts. Notification callbacks arrive on arbitrary RPC
threads and are marshalled onto the UI thread before touching any state.

**The process is DPI-aware.** Explorer is, and a DPI-unaware child of the taskbar has its
coordinates silently scaled — on a 125% display a strip positioned at x=1110 lands at
x=888, correct in every log and wrong on screen.

## Troubleshooting

Set `diagnostics = true` in the config, restart, and open the log from the tray menu. It
records Explorer restarts, strip rebuilds, COM re-acquisition, sink registration, desktop
changes and any unexpected failure.

The exe also has a selftest mode — run `DesktopSwitcher.exe --help` (console build) for
commands that list desktops, drive switches, dump taskbar geometry, and soak-test
recovery from an Explorer restart.

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
| Right click a number | menu of everything that button can do |
| Shift + right click a number | send the window you were last using to that desktop |
| Middle click a number | remove that desktop |
| Left click `+` | create a desktop |
| Right click `+` | menu — **New desktop** |

The menu exists because a touchpad cannot middle-click, which otherwise leaves removing a
desktop with no gesture at all. It also says what it is about to do, which the bare
gestures cannot: **Send "Chrome — Inbox" here** names the window that is going to move, and
**Remove Desktop 3 (2 windows)** tells you what is over there before you take it away.
Anything impossible right now is greyed rather than hidden — switching to the desktop you
are already on, sending a window that is already there.

The desktop's name titles the menu. Click it to rename — Enter saves, Escape or clicking
anywhere else leaves it alone, and an empty name puts the desktop back to **Desktop N**.
The name is set through the shell, so Task View shows it too.

The button count follows your actual desktops: create one in Task View and a button
appears; remove one and it disappears. The strip is right-anchored, so the gap to the
clock never changes as it grows.

Hover a number and a panel appears above it listing the windows open on that desktop —
Windows 10 otherwise gives you no way to see that without `Win+Tab`. Desktops with nothing
on them say so, which is how you find the one that is safe to remove.

A desktop's name — set from the menu, or in Task View (`Win+Tab`, right-click a desktop,
**Rename**) — is what the panel is titled with. Renaming from the menu shows up
immediately; a rename made in Task View is picked up the next time you switch desktops.

Right-click the tray icon for **Start with Windows**, config and log files,
**Reload settings**, and **Exit**. There is no other way to quit — the strip has no chrome.

**Reload settings** re-reads `config.ini` and rebuilds the strip, so editing the file does
not need a restart.

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
| `contextMenu` | `true` | right-click menu; `false` puts right click back to sending the window |
| `tooltips` | `true` | hover panel showing each desktop's windows |
| `tooltipDelayMs` | `400` | hover delay before the panel appears |
| `tooltipMaxWindows` | `8` | titles listed before the rest collapse into `+N more` |
| `tooltipWidth` | `440` | panel width at 96 DPI; every panel is this wide, longer rows are trimmed |
| `highlightColor` | `#0078D7` | underline bar under the current desktop |
| `backgroundColor` | *(blank)* | blank samples the live taskbar colour, and re-samples when you switch theme or accent |
| `diagnostics` | `false` | enable the rolling log |

## How it works

The strip is a child of `Shell_TrayWnd`, not a floating always-on-top window. That inherits
for free what a floating window would have to reimplement: it moves with the taskbar, hides
when the taskbar hides or a fullscreen app takes over, and shows on every virtual desktop.
The cost is that an Explorer restart destroys it, which is why the strip is disposable and
a watchdog rebuilds it.

The rest — the layering, the threading, and the reasoning behind the parts that look
strange — is in [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md). If you are going to change
anything, start with [CLAUDE.md](CLAUDE.md).

## Troubleshooting

Set `diagnostics = true` in the config, hit **Reload settings**, and open the log from the
tray menu. It
records Explorer restarts, strip rebuilds, COM re-acquisition, sink registration, desktop
changes and any unexpected failure.

The exe also has a selftest mode — run `DesktopSwitcher.exe --help` (console build) for
commands that list desktops, drive switches, dump taskbar geometry, and soak-test
recovery from an Explorer restart.

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
Windows 11** without new interface GUIDs. On any other build the app declines to start and
says which build it found, rather than calling interfaces it cannot vouch for; if Windows 10
is older than 22H2, Windows Update brings it up for free.

If the app ever hits an unexpected error it writes `%APPDATA%\DesktopSwitcher\crash.txt` and
says so - that file is what to send with a bug report.

## Install

Download `DesktopSwitcher-<version>.zip` from the project's Releases page, unzip it anywhere
and run `DesktopSwitcher.exe`. It asks whether to install: **Yes** copies it to
`%LOCALAPPDATA%\DesktopSwitcher` and starts it with Windows, so the unzipped folder can go;
**No** runs it from where it is, and does not ask again (the tray menu's **Install to this PC**
does it later). Running a newer download offers to replace the installed copy, and the tray
menu's **Uninstall** removes it, optionally with your settings. Nothing needs administrator
rights. Windows SmartScreen will warn about an unsigned program; **More info, Run anyway**
gets past it, and the `.sha256` file beside the download lets you check you have the file
that was published. The zip carries a plain-text `README.txt` that says the same
(source: [docs/INSTALL.txt](docs/INSTALL.txt)).

## Build

```cmd
build.cmd            :: windowed build (normal use)
build.cmd console    :: console build, keeps the selftest output visible
```

Output goes to `%LOCALAPPDATA%\DesktopSwitcher\DesktopSwitcher.exe`.

`build.cmd release` builds into `dist\` instead and leaves `DesktopSwitcher-<version>.zip` and
a `.sha256` beside it, ready to upload. The version comes from
[src/app/AssemblyInfo.cs](src/app/AssemblyInfo.cs), so bump it there first. It never touches
the installed copy, so it works while the app is running.

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
| Left click the signal tile | open the CPU, memory and network panel; click again to close it |
| Right click the signal tile | menu — **Keep open**, **Task Manager** |

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

Switching desktops slides the underline across to the new button rather than blinking it
there, so a `Win+Ctrl+Left/Right` you did not look at still leaves you knowing which way
you went. Hold the key down and it keeps up, tracking each switch from wherever it had got
to. Set `animationMs = 0` if you would rather it snapped.

Hover a number and a panel appears above it listing the windows open on that desktop —
Windows 10 otherwise gives you no way to see that without `Win+Tab`. Desktops with nothing
on them say so, which is how you find the one that is safe to remove.

A dim dot beside a number means that desktop has windows on it; no dot means it is empty —
the same thing the hover panel tells you, without having to hover. It follows the desktops
themselves, refreshed every couple of seconds, so a window opened or closed elsewhere
catches up shortly rather than only at the next hover.

The two small bars after `+`, marked **C** and **M**, are the system tile: CPU on the left,
memory on the right, each filling from the bottom in your highlight colour. Past 75% a bar
turns amber, past 90% red, so a machine under heavy load shows it at a glance. They follow the machine every couple of
seconds - every second while the panel is open - so a glance says whether it is busy.
Hovering the tile only names it; click it for the panel, refreshed every second: three
tiles, CPU, memory and network. CPU and memory each show a ring gauge with the percentage in
it; network shows its download and upload rates. Under each is a detail line - the peak over
the last minute for CPU and network (for network, download and upload each, in the larger
one's unit), gigabytes used of total for memory - and a chart of the
last minute, so a spike that has already passed still shows. The network chart scales to the
busiest moment in that minute, with a floor of 64 KB/s so background chatter stays near the
bottom; download is the filled shape, upload the thin line. The tiles share the panel's
width, so a much narrower `tooltipWidth` makes them cramped. The panel stays open once clicked: it still
follows the pointer to describe other buttons, but goes back to the stats rather than
disappearing when the pointer leaves. The bars brighten and the cell stays lit while the
panel is open. Clicking it again,
or clicking anywhere else, closes it. To have it stay through clicks elsewhere, right-click
the tile and tick **Keep open**; it then closes only from the tile or by unticking. It steps aside while the taskbar is out of sight —
auto-hidden, or behind a fullscreen video or game — and comes back with it.

How the figures compare with Task Manager, measured side by side:

- **CPU** is "% Processor Utility" by default, the figure Task Manager shows, and tracks it to
  within about a point. Set `statsCpu = time` for "% Processor Time" instead: the plain share
  of time not idle, which ignores clock speed. On a laptop at idle that reads two to three
  times higher than Task Manager, since the cores are running well under their base clock,
  and under boost it reads lower. `--stats` prints both.
- **Memory** matches Task Manager's percentage exactly, and "used" is within about 0.1 GB of
  its "In use"; ours also counts the small amount of modified memory waiting to be written.
  Sizes are GiB labelled GB, as Task Manager labels them.
- **Network** reads the same adapter counters Task Manager graphs, and over a speed test
  agreed with them to within 1%. It is in bytes per second, the unit a browser shows
  downloads in; Task Manager shows bits, so its number is eight times larger. It also
  counts the protocol overhead on the wire, so it reads 2-5% above what a download or speed
  test reports for the data itself. Virtual adapters — WSL, Hyper-V, VMware, VirtualBox —
  are left out, because their traffic also passes through the real adapter and would be
  counted twice; `--stats` lists which adapters are counted.

Each figure is an average over the interval since the last reading: a second while the panel
is open, two seconds otherwise. So the tile's bars, and the charts' history from before the
panel opened, smooth out spikes shorter than that, and "peak" is the highest of those
averages rather than the highest instant.

A desktop's name — set from the menu, or in Task View (`Win+Tab`, right-click a desktop,
**Rename**) — is what the panel is titled with. Renaming from the menu shows up
immediately; a rename made in Task View is picked up the next time you switch desktops.

Right-click the tray icon for **Settings**, **Start with Windows**, the config and log
files, **Reload settings**, and **Exit**. There is no other way to quit — the strip has no
chrome. Double-clicking the tray icon opens Settings.

Windows forgets how many desktops you had after a reboot, so the count you were last
using is saved and restored at login.

## Configuration

**Settings** on the tray menu has every option on four tabs — Strip, Hover panel, Stats,
Advanced — with switches, sliders, choices and colour pickers. Each change shows on the
strip as you make it and is saved straight away. **Revert** goes back to how things were
when the window opened, **Defaults** to how they started, and **Close** just closes:
nothing is waiting to be applied. Options that do nothing given the others — the hover
delay with the hover panel off, the bar sizes when the tile draws a gauge — are greyed out.

Everything lives in `%APPDATA%\DesktopSwitcher\config.ini`, created on first run, and the
file can be edited by hand. It is grouped into the same sections, and every key has a
comment above it saying what it does, what values it takes and what its default is.
Saving the file applies it at once; there is no need to restart or reload. Unknown keys are
preserved, a number out of range is pulled back into it, a value that cannot be read
falls back to its default, and sizes are authored for 96 DPI and scaled to your display
automatically. The app rewrites the file when a setting changes, so comments of your own
are not kept.

**Reload settings** on the tray menu re-reads the file and rebuilds the strip. With the
file applied as it is saved, that is now mostly a way to make the strip sample the taskbar
colour again.

| Key | Default | Meaning |
|---|---|---|
| `lastCount` | `2` | desktop count restored at login |
| `buttonWidth` | `34` | per-button width at 96 DPI |
| `plusWidth` | `26` | width of the `+` button at 96 DPI |
| `margin` | `6` | gap between the strip and the tray icons |
| `reconcileMs` | `2000` | safety-net poll interval |
| `contextMenu` | `true` | right-click menu; `false` puts right click back to sending the window |
| `occupancyDots` | `true` | dot beside a number that has windows on it; `false` stops the sweep entirely |
| `systemStats` | `true` | the system tile after `+` and its CPU / memory / network panel; `false` removes the tile and stops the sampling |
| `statsWidth` | `38` | width of the system tile at 96 DPI |
| `statsCpu` | `utility` | CPU measure: `utility` matches Task Manager, `time` is the plain share of time not idle |
| `statsTileStyle` | `meters` | what the tile draws: `meters` is the CPU and memory bars, `gauge` a half-ring gauge whose needle follows CPU, `rings` two concentric rings, CPU outside and memory inside, `chip`, `monitor` and `fan` icons that turn amber and red when CPU or memory is under heavy load; the bar settings below apply to `meters` only |
| `statsIconSize` | `20` | height of the icon, for every style but `meters`, at 96 DPI; capped to the taskbar's height and the tile's width, so a much bigger icon needs `statsWidth` raised too |
| `statsFanSpin` | `false` | keeps the `fan` icon turning, faster as CPU rises; costs a small repaint about thirty times a second |
| `statsFanSpeed` | `100` | how fast the spinning fan turns, in percent of the built-in pace (an eighth of a turn a second at idle, one at full CPU); 10 to 400 |
| `statsBarWidth` | `6` | width of each of the tile's two bars at 96 DPI |
| `statsBarGap` | `3` | space between the two bars at 96 DPI |
| `statsBarHeight` | `20` | height of the bars at 96 DPI; capped to what the taskbar has room for |
| `statsBarLabels` | `true` | the small C and M under the bars; `false` gives the bars that height back |
| `statsBarRounded` | `false` | round ends on the bars, like the panel's gauges; `false` draws plain rectangles |
| `statsWarnPercent` | `75` | load at which a bar turns amber |
| `statsAlertPercent` | `90` | load at which a bar turns red |
| `tooltips` | `true` | hover panel showing each desktop's windows |
| `tooltipDelayMs` | `400` | hover delay before the panel appears |
| `tooltipMaxWindows` | `8` | titles listed before the rest collapse into `+N more` |
| `tooltipWidth` | `440` | panel width at 96 DPI; every panel is this wide, longer rows are trimmed |
| `animationMs` | `80` | how long the underline takes to slide to the new desktop and the hover to fade; `0` snaps instead |
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

Turn on **Diagnostics log** under Settings → Advanced (or set `diagnostics = true` in the
config) and open the log from the tray menu. It
records Explorer restarts, strip rebuilds, COM re-acquisition, sink registration, desktop
changes and any unexpected failure.

The exe also has a selftest mode — run `DesktopSwitcher.exe --help` (console build) for
commands that list desktops, drive switches, dump taskbar geometry, and soak-test
recovery from an Explorer restart.

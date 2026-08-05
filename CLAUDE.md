# Project context

Read this before touching anything. It records the constraints that are not visible in the
code and the workflow gotchas that otherwise cost an hour to rediscover.

For how the code is organised, see [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md). For what
the app does, see [README.md](README.md).

## What this is

A numbered button strip docked inside the Windows 10 taskbar, one button per virtual
desktop, with the current one highlighted. Windows 10 gives no persistent indicator of
which desktop you are on and no way to jump straight to one; this fills that gap.

Single process, single assembly, no dependencies, no installer, no admin rights.

## Hard constraints

These are not preferences. Violating any of them breaks the build or the app.

**C# 5 only.** The compiler is the one in the box — `.NET Framework 4.8`'s `csc.exe` —
because the whole point is that nothing needs installing. No string interpolation, no
`nameof`, no `?.`, no expression-bodied members, no `async`/`await` niceties beyond what
C# 5 has. There is no SDK and no project file, and adding one would defeat the design.

**Windows 10 only, build 19045.** The virtual desktop COM interfaces are undocumented and
their GUIDs and vtable layouts change between Windows builds. Everything here is verified
against **19045.7548**, the terminal Windows 10 build, so it is stable there. It will not
work on Windows 11 without new interface GUIDs. Interfaces are commented `do not reorder`
for exactly this reason.

**Source files stay ASCII.** The in-box compiler reads BOM-less files in the system
codepage, so a literal bullet or em dash byte comes through as mojibake. Write them as
escapes — see `Bullet` and `Sep` in [Controller.cs](src/app/Controller.cs).

**`build.cmd` uses `-recurse:`.** `src/` is foldered by layer. A plain `src\*.cs` wildcard
does not descend into subdirectories and would silently compile nothing.

## Building and running from WSL

This repo is developed from WSL but targets Windows. Both work from the WSL shell, with
several non-obvious workarounds.

### Build

The in-box compiler is at
`/mnt/c/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe`.

Passing WSL-relative paths fails — `csc` resolves them against the wrong UNC base and gives
`CS1504 ... could not be opened` for every file. Convert first:

```bash
OUT=$(wslpath -w /mnt/c/Users/Public/dswtest.exe)
ICO=$(wslpath -w "$PWD/assets/DesktopSwitcher.ico")
/mnt/c/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe \
  -nologo -target:exe -platform:x64 -optimize+ -out:"$OUT" -win32icon:"$ICO" \
  -r:System.dll -r:System.Core.dll -r:System.Drawing.dll -r:System.Windows.Forms.dll \
  -recurse:$(wslpath -w "$PWD/src")\\*.cs
```

`-target:exe` gives the console build, which is the one that can print selftest output.
`-target:winexe` is the shipped, windowed build. `-win32icon:` is what puts the icon on the
file; the version fields beside it in Explorer come from the attributes in
[src/app/AssemblyInfo.cs](src/app/AssemblyInfo.cs), which `csc` reads straight out of
source. `assets/DesktopSwitcher.ico` is generated once and committed - there is no build
step that redraws it.

`build.cmd` itself cannot be invoked directly from WSL: `cmd.exe` refuses a UNC working
directory, and the repo is one. `pushd` maps it to a drive letter first, which works —

```bash
cmd.exe /c 'pushd \\wsl$\Ubuntu-24.04\home\mark\projects\desktopSwitcher && call build.cmd && popd'
```

— but the manual `csc` line above is usually less trouble, and it can write somewhere other
than the live install path.

### Run

Executing the exe from a WSL path gives `Permission denied`. Copy it to a Windows-local
path first — `/mnt/c/Users/Public/` works — run it there, then delete it. Running it this
way launches a real process in the user's session, so the COM interfaces work and the
selftests return live data.

### Replacing the live app

The installed exe lives at
`/mnt/c/Users/Siddh/AppData/Local/DesktopSwitcher/DesktopSwitcher.exe`. Windows locks a
running exe, so `csc` fails with `CS0016 ... being used by another process` unless the
process is killed first. Kill it, rebuild with `-target:winexe` straight to that path, then
relaunch with `cmd.exe /c start "" "<windows path>"` **backgrounded** — a foreground
`start` never returns to the WSL shell.

Two gotchas reading the PID out of `tasklist.exe`: its output begins with a blank line, so
`awk '{print $2}' | head -1` yields an empty string — grep for the image name first. And
every field carries a trailing `\r`, which makes `taskkill /PID` report `process "" not
found` — pipe through `tr -d '\r'`.

## Verifying a change

There are no unit tests and there is no sensible way to add any — see the `src/selftest/`
section in the architecture doc. Verification means running commands against the live
shell.

**Safe to run unprompted** — read-only, they change nothing:

| Command | Checks |
|---|---|
| `--list` | desktop enumeration, names, which is current |
| `--desktops` | the full window inventory, per desktop, with app names |
| `--taskbar` | taskbar geometry, DPI scale, computed strip bounds, sampled colour |
| `--where <title>` | whether a window is on the current desktop |
| `--anim [ms]` | steps the strip's easing headlessly and prints each frame; needs no shell at all |
| `--help` | the command list |

**Ask first** — these mutate desktop state or take over the screen: `--switch`, `--create`,
`--remove`, `--rename`, `--move`, `--soak`, `--watch`, `--service`, `--testwindow`,
`--strip`.

A clean compile with zero warnings plus `--list`, `--desktops` and `--taskbar` returning
sane live data is the baseline any change should clear.

Some things can only be checked by eye, and no command covers them: hover panel placement
and timing, menu appearance, how the highlight animation *looks*, behaviour across an
Explorer restart, and anything on a second monitor. `--anim` covers the arithmetic under
the animation - that it converges, lands exactly and stops - but nothing about how it
reads on screen.

## Where state lives

| Path | What |
|---|---|
| `%LOCALAPPDATA%\DesktopSwitcher\DesktopSwitcher.exe` | the installed binary — `build.cmd` writes here |
| `%APPDATA%\DesktopSwitcher\config.ini` | settings, written on first run |
| `%APPDATA%\DesktopSwitcher\log.txt` | rolling log, only when `diagnostics = true` |
| `HKCU\...\CurrentVersion\Run\DesktopSwitcher` | autostart, toggled from the tray menu |

Desktop *names* are Explorer's, stored per-GUID under its own registry key. We read them
from there and write them through the shell.

## Implementation history

Built as a sequence of milestones, each one a working layer with a selftest command proving
it before the next went on top.

| | What it added |
|---|---|
| **M1–M3** | Foundation: `Config`, `Log`, `Desktop`, `Native`. The COM interop in `VirtualDesktopApi` — list, switch, create, remove, move. The notification sink. |
| **M4** | `DesktopService`: the authoritative model, merging notifications with a reconcile watchdog and marshalling everything onto the UI thread. |
| **M5** | `TaskbarHost`: finding the taskbar, DPI awareness, computing strip bounds, docking a child window into `Shell_TrayWnd`. |
| **M6** | `SwitcherStrip`: owner-drawn buttons, animation, mouse input, and the events that carry intent back up. |
| **M7** | `Controller`: tray icon, watchdog, Explorer-restart recovery, autostart, config persistence. The app became an app. |
| **M8** | Hover tooltips: `TooltipWindow` and `WindowInventory` — see what is on a desktop without `Win+Tab`. |
| **M9** | Right-click menus: every action reachable without a middle click, which a touchpad cannot do. `MenuTheme` to stop them looking borrowed. |
| **M10** | Rename a desktop from the menu, through the shell, so Task View agrees. |

Between M8 and M9, two fixes worth knowing: tooltip rows lead with the owning app resolved
from the process, and the tooltip width is fixed rather than sized to content so it does not
jump as the pointer moves along the strip.

## House style

The code is commented densely and unusually — comments explain *why*, at length, and often
record the failure that motivated the current shape. That is deliberate, given how much of
this rests on undocumented behaviour that cannot be looked up. Match it. A change that
strips the reasoning out of a comment loses more than it saves.

Prose in the docs and README follows the same grain: plain, specific, no marketing.

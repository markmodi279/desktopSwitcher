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
WPF='C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF'
/mnt/c/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe \
  -nologo -target:exe -platform:x64 -optimize+ -out:"$OUT" -win32icon:"$ICO" \
  -r:System.dll -r:System.Core.dll -r:System.Drawing.dll -r:System.Windows.Forms.dll \
  -r:"$WPF\\UIAutomationClient.dll" -r:"$WPF\\UIAutomationTypes.dll" \
  -recurse:$(wslpath -w "$PWD/src")\\*.cs
```

The two UI Automation references are how close-all reads what a save prompt asks
([DialogText.cs](src/model/DialogText.cs)). They ship with .NET 4.8 like the rest, but in
the `WPF\` subfolder, which `csc` does not search, so they are named in full.

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
process is stopped first. Stop it with the console build's `--quit` (it asks the running copy to
exit the way the tray's Exit does), rebuild with `-target:winexe` straight to that path, then
relaunch with `cmd.exe /c start "" "<windows path>"` **backgrounded** — a foreground
`start` never returns to the WSL shell.

**Do not `taskkill /F` it.** A killed process never removes its tray icon, so Explorer keeps
drawing a dead one beside the live one until the pointer crosses it. That widens the
notification area and the strip sits 30 px to the left until someone opens the `^` flyout and
hovers - which looks like a bug in the strip and is not. Plain `taskkill` (no `/F`) does
nothing: it only signals visible windows and the controller's is hidden. `--quit` posts a
registered message (`DesktopSwitcher.Quit`) that the controller handles. With `diagnostics =
true` the log shows `asked to quit`, `shutting down`; if the next start reads a wider
`notify.Left` than the one before it, a ghost is back.

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
| `--desktops` | the full window inventory, per desktop, with app names, and the windows pinned to all desktops as a group of their own, each with its kind |
| `--occupancy` | the occupancy dot's cheap sweep against the full sweep, per desktop |
| `--pinned` | every alt-tab window's pin kind, read off the fixed GUID a pinned window reports in place of its desktop, checked against `IsViewPinned` and the visibility check. Pinned windows are in no desktop's inventory, which is why it walks the windows itself |
| `--marks` | desktop marks: text form, the store (carry, prune, adoption, Recent), the strip's drag arithmetic over mixed widths; lists `marks.ini` against the live desktops without writing it |
| `--stats [N]` | the stats panel's rows a second apart, what each reading cost, and which network adapters are counted |
| `--taskbar` | taskbar geometry, DPI scale, computed strip bounds, sampled colour |
| `--where <title>` | whether a window is on the current desktop |
| `--anim [ms]` | steps the strip's easing headlessly and prints each frame; needs no shell at all |
| `--glide` | the strip's glide to a moved tray edge, headlessly: no jump on the first frame, only toward the target, lands exactly, a mid-glide retarget is absorbed |
| `--fold [ms]` | a removed desktop's button folding away, headlessly: the flash holds the gap and fades, the gap only shrinks, lands on zero and stops, a second removal restarts cleanly, animation off means no fold; and which row changes count as exactly one removal |
| `--slide [ms]` | the same for the hover panel's travel between buttons, in both styles: placement, the pinned bottom edge, the row-block origin, the accent stub |
| `--publicip` | asks each public IP service once over HTTPS and prints the address, which one answered, and how long it took - the one selftest that goes online |
| `--hotkeys` | the configured desktop shortcuts, each registered and released at once: `free`, or taken. Beside the running app its own keys read as taken |
| `--winver` | the Windows build, and whether startup would accept it (19041-19045 only; Windows 11 is refused) |
| `--config` | every setting with its value, range and default, and that `config.ini` round-trips through the settings table |
| `--help` | the command list |

**Ask first** — these mutate desktop state or take over the screen: `--quit` (closes the running app), `--switch`, `--create`,
`--remove`, `--pin <title> on|off` (pins or unpins a real window, printing its pin kind either side), `--rename`, `--move`, `--reorder`, `--reordertest` (adds three scratch desktops on the end, reorders only those, removes them), `--soak`, `--watch`, `--service`, `--testwindow`,
`--strip`, `--settings` (opens the Settings window standalone; prints changes, saves nothing),
`--markstress [seed]` (about a minute: a test window of strips driven with synthetic mouse messages - leave the pointer alone - then four scratch desktops reordered, added and removed; writes no marks, cleans up after itself, leaves `%TEMP%\markstress-sheet.png` to look over),
`--closeall [filter]` (about a minute of throwaway desktops and test windows flashing up; it only ever closes
its own windows, on desktops it creates, and cleans up after every scenario. One scenario also opens a test
window on the current desktop for about a second, a second window of a test app, to prove Force asks twice.
A filter runs only the scenarios whose name contains it - `--closeall "answered Save"`).

A clean compile with zero warnings plus `--list`, `--desktops` and `--taskbar` returning
sane live data is the baseline any change should clear.

Some things can only be checked by eye, and no command covers them: how a drag to reorder looks and feels, how the close-all window looks through its three phases - the checklist turning into progress, rows folding and stuck ones rising, an armed Force, the done state fading - and a removed desktop's button folding away on the strip, hover panel placement
and timing, menu appearance, the Settings window's look, how the highlight animation *looks*, the pin mark and its flash in the modern style, on a marked button and on a light taskbar, behaviour across an
Explorer restart, and anything on a second monitor. `--anim` and `--slide` cover the arithmetic under
the animation - that it converges, lands exactly and stops - but nothing about how it
reads on screen. For the hover panel specifically, `--slide` cannot see trails behind a
moving `WS_EX_TRANSPARENT` topmost window, which is the one thing worth checking over a
playing video rather than over the desktop. The two styles draw the panel through
different windows - plain and painted for classic, layered for modern - so a change to the
panel wants that check in each.

## Where state lives

| Path | What |
|---|---|
| `%LOCALAPPDATA%\DesktopSwitcher\DesktopSwitcher.exe` | the installed binary — `build.cmd` writes here |
| `%APPDATA%\DesktopSwitcher\config.ini` | settings, written on first run |
| `%APPDATA%\DesktopSwitcher\marks.ini` | desktop marks by GUID, and the picker's Recent row; read at startup only |
| `%APPDATA%\DesktopSwitcher\log.txt` | rolling log, only when `diagnostics = true` |
| `HKCU\...\CurrentVersion\Run\DesktopSwitcher` | autostart, toggled from the tray menu |

Desktop *names* are Explorer's, stored per-GUID under its own registry key. We read them
from there and write them through the shell.

## Implementation history

The milestone record, M1 onward, is in [docs/HISTORY.md](docs/HISTORY.md): what each layer
added and what testing it against the live shell turned up. Read it when a change touches a
feature and you need to know why it has its current shape. When a milestone lands, add its row
there, not here; this file is loaded into every session and should hold only what every
session needs.

## House style

The code is commented densely and unusually — comments explain *why*, at length, and often
record the failure that motivated the current shape. That is deliberate, given how much of
this rests on undocumented behaviour that cannot be looked up. Match it. A change that
strips the reasoning out of a comment loses more than it saves.

Prose in the docs and README follows the same grain: plain, specific, no marketing.

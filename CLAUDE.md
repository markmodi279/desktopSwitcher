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
| **M11** | The underline bar travels between buttons instead of snapping. `Motion`, and the easing model everything animated now shares. |
| **M12** | The hover panel travels too. Its edges are eased rather than an origin and a size, the strip's frame timer drives both, and `--slide` proves the arithmetic. |
| **M13** | Occupancy dots: a cheap sweep in `WindowInventory` answers "does this desktop have windows" without the cost of the full one, pushed to the strip on the reconcile tick. `--occupancy` proves the cheap sweep against the full one. |
| **M14** | The stats tile: CPU, memory and network in the hover panel, refreshed live, pinned open by a click. `SystemStats` samples on the reconcile tick so the first panel already has rates; `--stats` checks the figures and the adapter filter. |
| **M15** | Settings without Notepad: `SettingsWindow`, owner-drawn and themed off the taskbar, applying every change live with Revert and Defaults. `Config.All` became the one table of settings, so `config.ini` is now written grouped and commented, and `ConfigWatcher` applies a hand edit the moment it is saved. `--config` proves the table round-trips; `--settings` opens the window on its own. |
| **M16** | The modern style, set separately for the strip (`stripStyle`) and the panel (`panelStyle`): rounded cells and a pill on the strip, and a layered hover panel with antialiased corners, a nine-sliced shadow, app icons from `AppIcons` and two-tone rows. Classic draws exactly as before and stays the default for both. `--slide` runs both panel styles from `TooltipWindow.Metrics`. Later, from a browser mock of seven strip variants, all seven made reachable: a third strip-only style, **segmented** - the desktop buttons in one faint track, a thumb sliding behind the current one, no underline, filled per `stripThumb` (neutral, accent with its ink picked by the thumb's luma, or neutral with an accent edge); `modernSlide`, the modern cell travelling on the bar's own position instead of fading button to button (`DrawMovingCell`; a cell redrawn under a button the drag laid on the taskbar colour, `DrawUnderlay`); `plusIcon`, the `+` stroked as a Lucide icon at the marks' weight; `markNumber = dimmed`, a marked button's number full size and faded, the button 4 px wider (`MarkExtraFull`); and `statsMonitorHeavy`. Checked by eye from a scratch rendering sheet, settled and mid-slide, dark and light. |
| **M17** | The public IP at the stats panel's top right, copied by Ctrl+click on the tile (`statsCopyGesture`) or **Copy public IP** on its menu, behind `statsPublicIp` (off). Middle click keeps the panel open (`statsKeepGesture`), Alt+click opens Task Manager (`statsTaskManagerGesture`). `PublicIp` asks Cloudflare by IPv4 literal on a pool thread, cached ten minutes or until the network changes - the app's first and only network request. `--publicip` checks both services. |
| **M18** | Keyboard shortcuts that switch without the slide: Ctrl+Alt+1..9 jumps to a desktop, Ctrl+Alt+Left/Right steps and wraps round (`jumpShortcut`, `stepShortcut`, `stepWrap`, on a Shortcuts tab). `Hotkeys` claims them with `RegisterHotKey` on the controller's window, and they end in the same `SwitchDesktop` a click does. A combination another app holds is named in a tray balloon. `--hotkeys` checks which are free. |
| **M19** | **Close all and remove** on a desktop's menu. `WindowCloser` posts `WM_CLOSE` to each window (a packaged app through its frame only) and tells a closed one from one with a real dialog up, which disables its owner; a question drawn inside the window cannot be seen and reads as "still open". `CloseAllRun` drives a live `CloseProgressWindow`, a tool window so it shows on every desktop, with Go there, Force close and Cancel, and removes the desktop the tick it empties. While it runs it holds `LockSetForegroundWindow`, because an app that grabs focus for its question (Terminal) otherwise drags the user to its desktop. An earlier Bring here moved the windows to the user instead and was dropped: Notepads vanished mid-move with its save prompt unanswered. `WindowInventory` now pairs a packaged app's frame with its CoreWindow when the frame's title only ends with the core's. `ConfirmDialog` takes any buttons. Pinned windows are skipped, detected by `VirtualDesktopApi.IsPinned`; `--desktops` tags them `[pinned]`. `--closeall` drives real runs against scripted test windows, each in its own process: instant, tray-hiding, slow, real dialog answered either way, in-window question, hung, respawning, pinned, vanished, late-arriving, twenty at once. Later, **Close all windows** beside it: the same run built with `remove` false, which keeps the desktop and skips the sweep for late windows. It is the only close-all the last desktop has, since Windows will not remove that one, and its confirmation says there that it means every open window. `--closeall` covers it on a scratch desktop, since the flag and not the count is what differs. Then **Show**: on the run's own desktop the panel's Go there becomes Show, which restores the window holding things up and gives the keyboard to its dialog if it has one - Notepads asked "Save?" inside a minimized window with nothing on the panel to reach it. Chasing why `--closeall`'s current-desktop focus check always failed found a real bug in the switch's focus nudge: it judged an owned dialog as "not on this desktop", because a dialog is never an Alt+Tab window, and handed the keyboard to the dialog's disabled owner 10 ms after the dialog took it. It now judges a dialog by its root owner, and when it picks a window disabled behind a dialog, focuses the dialog. Later, a **checklist** in the confirmation (`CloseSelectDialog`, two windows or more): every window ticked, untick to keep. Remove is enabled only with every row ticked - the shell would carry kept windows to a neighbour - and **Close N** keeps the desktop otherwise. Pinned windows show greyed. `CloseSelection` decides what closes after the answer: keeping, only ticked windows still there, so one that arrived mid-dialog stays. `--closeall` drives it. |

| **M20** | **Move left** and **Move right** on a desktop's menu. 19045 has no call to reorder desktops, so `VirtualDesktopApi.Reorder` rebuilds the row from the target position onward: create a new desktop, `RemoveDesktop` the old one with the new one as fallback (the shell carries every window across), copy the name. About 1 ms a call; moved desktops get new GUIDs, so it is refused while a close-all runs. `--reordertest` proves it on scratch desktops, `--reorder N M` on real ones. Then drag to reorder on the strip: a press arms it, `DragThreshold` starts it and takes the capture, the button follows the pointer while the ones it passes ease aside on a per-button `Offset`, and the drop glides into its slot. The reorder runs inside `ReorderRequested` and comes back through `SetDesktops` as one change - `--reordertest` checks that through a real `DesktopService`, since COM's modal loop could in principle have pumped the queued notifications mid-run. Release a strip's height off it to cancel. Driving the live strip with synthetic input then found three ways a drag went wrong, all fixed: a middle click mid-drag removed the desktop under the pointer and a right click opened its menu (other buttons are now ignored while dragging); a desktop added elsewhere mid-drag let the drag go, and the release then switched desktops (`LetGo` swallows it); and a strip rebuilt mid-drag by a config change took the release as a click (a left release counts only if that strip saw the press). |

| **M21** | **Desktop marks**: two letters, a symbol or a colour beside a desktop's shrunk number, set from **Set mark...** on its menu, so desktops holding the same apps can be told apart. `MarkPicker` previews every edit on the strip live. Marked buttons are wider, so the strip's `Layout` now gives every button its own width and left edge, and the drag's step-aside, drop slot and landing (`PassOffset`, `DropSlot`, `SlotLeft`) work over mixed widths. Marks are kept by GUID in `marks.ini` (`MarkStore`); a reorder's new GUIDs reach it through `DesktopService.Recreated`, raised before `DesktopsChanged`, and pruning is held off during a reorder. A removed desktop's mark goes to the picker's Recent row; in the first 15 s after start a vanished desktop's mark can be adopted by the unmarked desktop at its old position, in case Windows recreated the desktops at login. An empty marked desktop dims its mark in place of the dot. `--marks` checks it all without writing; `--reordertest` now checks `Recreated` too. `MarkSync` holds the controller's rules so `--markstress` can drive the same code: synthetic drags, clicks and stray buttons over random mixed-width rows checked against an independent oracle, the settled underline read back off the screen, a rendering sheet, and random reorders of marked scratch desktops through the real shell. It found three bugs, all fixed: a second "Set mark..." while a picker was open opened a second picker, and OK in the first then saved onto the second desktop (one picker at a time now, and the dialog-open flag is a count); at `buttonWidth` 16 a marked button was 26 px and its mark drew over the neighbours (`MarkMinWidth` 40, and the mark is clipped to its button); and, from before marks, the classic underline and lit cell were drawn antialiased, half a pixel soft at both ends (`FillCrisp`). The selftests log to the app's own `log.txt`, so `--marks`'s synthetic adoptions show up there - they are not the app's. Later the 32 Segoe MDL2 symbols, too thin beside the numbers, became Lucide icons stroked at 2.6 (`MarkIcons`, drawn by `MarkPainter` through `SvgPath`), chosen from mocks of other Segoe faces, fattened MDL2 outlines and filled sets. Stored as `icon:<key>`; an old `glyph:` the picker offered reads back as its icon. The strip's numbers and mark letters then moved from Segoe UI to Bahnschrift SemiBold to match the icons' weight (`SwitcherStrip.StripFace`; the modern current number is Bahnschrift Bold), Segoe UI Semibold and Bold having barely shown at strip size. |

| **M22** | **Pin a window to all desktops**: Ctrl+Alt+P (`pinShortcut`) and **Pin/Unpin** beside Send here on a desktop's menu, both through `VirtualDesktopApi.SetViewPinned` (`IVirtualDesktopPinnedApps`, moved out of `CloseAllStress`). `--pinned` found that a pinned window reports one of two fixed GUIDs in place of its desktop, one per kind - `GetPinKind` reads them, and is the only answer that tells a window pin from an app pin (`IsViewPinned` is true for both). `--pin` found what UnpinView does to an app pin: it exempts that one window and leaves the app pinned. Feedback is on the strip, not in tray balloons: a small pin in the current desktop's corner while the focused window is pinned (`pinMark`; accent for an app pin), pushed on focus change and the reconcile tick, flashed by the shortcut on its own 650 ms clock - mocked in four placements first. Sending a pinned window to a desktop unpins it, so Send stays enabled. The hover panel lists pinned windows on every desktop under "Pinned to all desktops" - before, they were on no panel, filed under their pin GUID. That also found an occupancy bug: the cheap sweep counted pin GUIDs as desktops and could stop before reaching a real one, dropping its dot; reproduced against the old build, fixed. |

| **M23** | **Close-all as one window**, from a browser mock. `CloseSelectDialog` and `CloseProgressWindow` became `CloseAllWindow`: the checklist turns into the progress in the same rows (`CloseList`), unticked rows stay as "kept", the one-window case uses it too (no box), and it is modeless throughout - the controller holds `BeginAsking` while it confirms. Stuck rows rise to the top and closed ones fold away, under an eased bar; done holds 1.5 s and fades. Each stuck row has its own **Show** (across desktops by activating the window, so Windows follows it there - switching first and focusing after was refused once Explorer took the activation), **Force** (armed first, "End Chrome + 2 others?", when `WindowCloser.OthersOfProcess` finds windows outside the run - a force ends the process) and **Ask again** after a cancelled prompt. The row quotes the prompt: `DialogText` reads a Static, else UI Automation (a TaskDialog's text has no Win32 handle), else the caption, on a pool thread, two ticks after the prompt appears and once more if only the caption came back. Hung windows read **Not responding**. A removed desktop's button now flashes and folds shut on the strip (`StripFold`, for every removal), the strip re-placed each frame like the tray glide; `--fold` steps it. `--closeall` gained seven scenarios and a name filter. Measured against the old build under `--closeall`, scenario by scenario from a CPU series lined up with the log: the first version cost 16% of a core inside runs against 6.9%, nearly all of it re-laying-out the window every frame (9 ms a resize) and repainting every row; one `SetWindowPos` per resize, a resize every third frame, row bitmaps redrawn only on change and paint skipped outside the dirty area brought it to 12.4%, about 0.1-0.2 s of CPU per close-all. GDI and USER counts stay flat across 26 runs. UI Automation costs about 6 MB and four threads, once, from the first prompt that needs it. `Perf` writes the before-and-after lines. Then, from a second mock, the **cascade**: windows that close in one tick are told one by one - a row the run reports closed keeps its spinner until its turn (`ClosePacing`: 400 ms least progress, 120 ms apart, the whole burst inside 1200 ms), its tick pops, holds and folds, and the done message waits for the last fold while the desktop is still removed on time. A **Close all** settings tab holds the pace, and `foldRemoved` on the Strip tab turns the strip's fold off. `--closeall` checks the list catches up inside the pacing's bound and that done waits for it. |

Between M8 and M9, two fixes worth knowing: tooltip rows lead with the owning app resolved
from the process, and the tooltip width is fixed rather than sized to content so it does not
jump as the pointer moves along the strip.

## House style

The code is commented densely and unusually — comments explain *why*, at length, and often
record the failure that motivated the current shape. That is deliberate, given how much of
this rests on undocumented behaviour that cannot be looked up. Match it. A change that
strips the reasoning out of a comment loses more than it saves.

Prose in the docs and README follows the same grain: plain, specific, no marketing.

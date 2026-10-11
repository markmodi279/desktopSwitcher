# Architecture

How DesktopSwitcher is put together, and why it is put together that way. For what the
app *does*, see the [README](../README.md).

## The shape of it

One process, one assembly, no dependencies beyond what ships in Windows. It compiles with
the in-box `csc.exe` and runs as a tray application whose only visible surface is a strip
of buttons living inside somebody else's window.

That last part drives most of the design. The strip is a `WS_CHILD` of `Shell_TrayWnd` —
Explorer's taskbar — which buys a great deal for free and costs one thing dearly. It moves
with the taskbar, hides when the taskbar hides, disappears under a fullscreen app, and
shows on every virtual desktop, none of which we implement. In exchange, an Explorer
restart destroys our window without warning, so everything below the strip has to be able
to rebuild it from nothing.

```
              ┌─────────────────────────────────────────────┐
              │  Controller  (hidden Form, message pump)     │
              │  tray icon · watchdog · timers · marshalling │
              └───────┬─────────────────────────┬───────────┘
                      │                         │
          ┌───────────▼──────────┐   ┌──────────▼───────────┐
          │  DesktopService      │   │  SwitcherStrip       │
          │  authoritative model │   │  child of the taskbar│
          └───────────┬──────────┘   └──────────┬───────────┘
                      │                         │
          ┌───────────▼──────────┐   ┌──────────▼───────────┐
          │  VirtualDesktopApi   │   │  TooltipWindow       │
          │  VirtualDesktopNotify│   │  MenuTheme / menus   │
          └───────────┬──────────┘   └──────────────────────┘
                      │
              ┌───────▼────────┐
              │  Explorer COM  │
              └────────────────┘
```

## The layers

`src/` is foldered by layer, and the dependencies only ever point downward: `ui/` may use
`model/`, `model/` may use `shell/`, and nothing reaches back up. `app/` sits on top of all
three. If you find yourself wanting a `shell/` file to know about a button, something has
gone wrong.

`build.cmd` compiles with `-recurse:` rather than a plain wildcard, so a new folder needs no
build change — but a plain `src\*.cs` would silently compile nothing, which is worth
remembering if the build script is ever edited.

### `src/shell/` — talking to Explorer

Everything undocumented lives here, and nothing above this folder knows a GUID.

| File | Purpose |
|---|---|
| [VirtualDesktopApi.cs](../src/shell/VirtualDesktopApi.cs) | The COM interfaces and every operation on a desktop: list, switch, create, remove, rename, move a window. No caching, no policy. |
| [VirtualDesktopNotify.cs](../src/shell/VirtualDesktopNotify.cs) | The notification sink. Registers a COM object with the shell and turns its callbacks into .NET events. |
| [Native.cs](../src/shell/Native.cs) | P/Invoke. Window enumeration, geometry, parenting, painting, DPI, process lookup. |
| [ClickWatcher.cs](../src/shell/ClickWatcher.cs) | A low-level mouse hook on a thread of its own, posting every button press on the system to a window. Installed only while the stats panel is pinned, so a click elsewhere can close it. |

Two rules hold this layer together. **`IVirtualDesktop` pointers are never held across
calls** — they go stale the instant a desktop is added or removed, so every operation
re-resolves from a `Guid`. And **every call runs through `Do()`**, which drops the cached
shell objects and retries once when COM fails, because an Explorer restart invalidates all
of them at once.

The interface layouts are verified against **Windows 10 build 19045.7548** and are
commented `do not reorder`. They are not stable across Windows versions; see
[CLAUDE.md](../CLAUDE.md).

### `src/model/` — state worth trusting

| File | Purpose |
|---|---|
| [DesktopService.cs](../src/model/DesktopService.cs) | The only type the UI talks to. Owns the authoritative desktop list and merges the two sources it arrives from. |
| [Desktop.cs](../src/model/Desktop.cs) | Immutable snapshot of one desktop: id, index, name, whether it is current. |
| [WindowInventory.cs](../src/model/WindowInventory.cs) | Which windows are on which desktop, and which app owns each one. Built on demand, cached for a second. Also answers the cheaper question of which desktops merely *have* windows, for the occupancy dot. |
| [ForegroundTracker.cs](../src/model/ForegroundTracker.cs) | Remembers the last real window to hold focus, so "send the active window here" has something to send. |
| [SystemStats.cs](../src/model/SystemStats.cs) | CPU, memory and network throughput for the stats tile. Rates from the difference between two readings, network adapters cached between change notifications. |

### `src/ui/` — pixels and input

| File | Purpose |
|---|---|
| [TaskbarHost.cs](../src/ui/TaskbarHost.cs) | Finds the taskbar, works out where the strip goes, docks it there, and samples the taskbar's colour. |
| [SwitcherStrip.cs](../src/ui/SwitcherStrip.cs) | The strip itself: owner-drawn buttons, animation, mouse input. Raises intent as events; decides nothing. Owns the one frame timer, and drives the hover panel's travel from it as well as its own. |
| [Motion.cs](../src/ui/Motion.cs) | The easing every moving thing is stepped by — frame interval, epsilons, and the exponential `Rate`/`Ease` pair. Belongs to neither the strip nor the panel, so the panel does not have to reach back through the class that owns it. |
| [TooltipWindow.cs](../src/ui/TooltipWindow.cs) | The hover panel. A top-level, click-through window that draws whatever text it is handed. |
| [MenuTheme.cs](../src/ui/MenuTheme.cs) | Dresses a `ContextMenuStrip` in colours derived from the sampled taskbar colour. |
| [SettingsWindow.cs](../src/ui/SettingsWindow.cs) | The Settings window. Edits a copy of the config, one row per entry in `Config.All`, and hands a fresh copy to `Changed` after every edit; it never touches the live config. Revert, Defaults, retheme in place, and its own per-monitor DPI handling. |
| [SettingsControls.cs](../src/ui/SettingsControls.cs) | The window's owner-drawn controls — toggle, slider, number box, segmented choice, colour swatch, sliding tab row, the setting row — and the shared, mutable `SettingsTheme` they all draw from. |
| [Palette.cs](../src/ui/Palette.cs) | The one place that knows which way "away from the background" is. Lifts a surface off the taskbar colour and picks text tones for it, in whichever direction that colour demands. |

Note what is *not* here: neither the strip nor the tooltip knows what a desktop is called or
what is open on one. `SwitcherStrip` raises `ContextMenuRequested` and asks a
`TooltipProvider` delegate for content; `Controller` answers both. That is deliberate — the
captions and rows need the model, the inventory and the foreground tracker at once, and
only `Controller` holds all three.

### `src/app/` — the application

| File | Purpose |
|---|---|
| [Controller.cs](../src/app/Controller.cs) | Owns everything. Hidden window, tray icon, timers, watchdog, menu construction, tooltip content, config persistence. |
| [Program.cs](../src/app/Program.cs) | Entry point. DPI awareness, config load, single-instance mutex, then `Controller` or `SelfTest`. |
| [Config.cs](../src/app/Config.cs) | The `config.ini` round trip, and `All` — the one table of settings that the parser, the commented file, the Settings window and `--config` all read. Unknown keys preserved, malformed values fall back, a missing key or an older file layout triggers a rewrite. |
| [Setting.cs](../src/app/Setting.cs) | One entry in that table: key, section, label, help text, kind, range, accessors, and the parse/format/describe for its kind. |
| [ConfigWatcher.cs](../src/app/ConfigWatcher.cs) | Applies a hand edit to `config.ini` as soon as it is saved. Debounces an editor's burst of events, retries a locked file, and ignores the app's own writes by comparing against `Config.LastWritten`. |
| [Log.cs](../src/app/Log.cs) | Rolling diagnostic log, off by default, thread-safe because notifications are not. |
| [AssemblyInfo.cs](../src/app/AssemblyInfo.cs) | Name, description and version. Attributes only; `csc` stamps the Win32 version resource from them, which is how an exe with no project file gets a Details tab. |

### `src/selftest/` — proving it against the real shell

[SelfTest.cs](../src/selftest/SelfTest.cs) is the largest file in the tree and none of it
runs in the shipped app. There are no unit tests here and there is no good way to write
any: the interfaces are undocumented, live only inside a running Explorer, and have no
stand-in worth building. So each layer got a command that drives it against the real thing
and prints what it saw. Those are how a change is checked, and how a new Windows build
would be proved out.

`--anim` and `--slide` are the exceptions that need nothing running: they step the easing
headlessly and print a frame at a time. How the animation *looks* is still an eye check, but
whether it converges, lands exactly on its target and ever reports itself finished is
arithmetic, and that is the part that fails quietly — a value settling at 0.996 draws an
identical pixel and leaves the frame timer running for the rest of the session.

`--slide` covers the hover panel, where being a whole window rather than a rectangle inside
one adds questions `--anim` never had to ask. It checks that consecutive buttons actually
place the panel somewhere different — `Place` centres a wide panel on a narrow button and
then clamps it into the work area, so past a certain point every button on the right resolves
to the same x and the travel is silently a no-op — that the rounded bottom edge is the same
integer on every frame while the height changes, that `Measure` and `Render` still agree
about where the row block starts now that one builds downward and the other lays out upward,
and that the accent stub rides at a fixed offset inside the panel instead of racing ahead of
it.

## How a change reaches the screen

The interesting path is the one nobody clicked — you press `Win+Ctrl+Right` and the strip
keeps up:

1. Explorer calls `CurrentVirtualDesktopChanged` on our sink, **on an arbitrary RPC thread**.
2. `VirtualDesktopNotify` reads the id straight out of the shell's object and raises a .NET
   event — still off the UI thread.
3. `DesktopService` **posts** the work to the marshaller (the `Controller` window) and
   returns immediately. Nothing has touched the model yet.
4. On the UI thread, the model updates and `CurrentChanged` fires.
5. `Controller` hands the new list to `SwitcherStrip`, which animates the highlight across.

A click travels the other way and is just as indirect. `SwitcherStrip` raises
`SwitchRequested(Guid)` — it does not switch anything. `Controller` routes that to
`DesktopService.SwitchTo`, the shell obliges, and the highlight moves only when the
notification comes back around the loop above. The strip never paints a state it has
merely assumed.

Behind all of it, a reconcile tick (2s by default) compares the model against the shell and
corrects any divergence. It exists for the cases notifications cannot cover: a sink that
died, an event that never arrived, an Explorer that restarted. When the shell is
unavailable — mid-restart — it holds the last known model rather than blanking the strip.

## Threading

There is one UI thread and everything of consequence happens on it.

Notification callbacks are the only exception, and they are quarantined: they arrive on RPC
threads, read what they need out of the shell object, and post. `DesktopService`'s model and
both its events are single-threaded from any subscriber's point of view, which is what lets
the rest of the codebase ignore threading entirely.

`Log` serialises its writes, because it is the one thing an RPC thread may touch directly.

**That quarantine depends on `Controller` having a window handle, and it creates one
deliberately.** `Post` decides whether to marshal by asking `ISynchronizeInvoke.InvokeRequired`,
and a `Control` with no handle answers `false` from every thread — there is no window whose
owning thread it could compare against. `Controller` overrides `SetVisibleCore` so it is never
shown, and a form that is never shown never creates a window, so for a long time the answer was
always `false` and every notification ran inline on the RPC thread that delivered it. The model,
the strip's list, the tooltip and the repaint all tolerated it, so nothing looked wrong. The
animation timer did not: a WinForms timer started from a thread with no message pump never ticks,
which froze the strip's animation and is how this was found. Touching `Handle` in the constructor
is what makes the marshalling real; do not remove it as dead code.

`Controller` runs eight timers, all on the UI thread, and a ninth that only runs while the strip is gliding:

| Timer | Interval | Job |
|---|---|---|
| startup | 500 ms | waits for the taskbar to exist at login, then stops |
| reconcile | `reconcileMs` (2000) | model safety net, the occupancy dot's cheap window sweep, and a stats reading |
| watchdog | 1000 ms | is the taskbar still there, is the strip still in it |
| anchor | 40 ms | reads where the tray edge is, and places the strip only if it has moved; also (re)makes the location hook on MediaController's tile |
| glide | 15 ms | runs only while the strip eases to a moved tray edge; `--glide` checks the arithmetic |
| focus | 300 ms | sample the foreground window |
| save | 2000 ms | debounced config write |
| theme | 600 ms | debounced rebuild after `WM_SETTINGCHANGE`/`ImmersiveColorSet` |
| settings | 150 ms | debounced apply of the Settings window's latest change; created when the window first changes something |

The strip's position follows the tray in two different ways. With no MediaController tile, a
moved notification area is eased: `TaskbarHost` remembers how far the strip lags and `Motion`
eases that to zero over `GlideMs` (300, the same as the tile's, and deliberately not
`animationMs`: at 100 ms an exponential ease puts over half of a 30 px move into its first
frame). With the tile beside the clock, the strip does *not* ease on its own: it sits against
the tile, the tile eases itself, and a location hook on the tile's process makes the strip
follow it exactly. A second ease on top left the strip late and the tile drawn over it.
MediaController also moves the strip in the same step as the tile, because following from
another process is always a few milliseconds behind and that is a frame the compositor can
present.

`SwitcherStrip` owns three more, and they are the only timers in the tree that are not always
running. The animation frame timer, 15 ms, is started when a visual target changes and
stopped on the first frame where nothing moved; it steps the strip's own buttons and bar
*and* the hover panel's travel, because the two always move together — the same
`WM_MOUSEMOVE` that lifts a button's hover tone is what aims the panel at it — and a second
timer would only duplicate the elapsed-time bookkeeping to run in lockstep with the first.
The re-show timer, 80 ms, is a one-shot that gates the panel's *text* while its geometry
follows the pointer immediately. The stats timer, 250 ms, runs only while the stats panel is
pinned: each tick checks that the taskbar is still on screen, and every fourth re-reads the
figures. While it runs, the reconcile tick takes no stats reading of its own, so each rate is
measured over a steady second rather than over whatever gap two unrelated clocks left.

Everything above shares the UI thread with them, which is why each frame steps by measured
elapsed time rather than by a fixed amount — a reconcile tick or an inventory sweep delays
frames, and the animation should lose smoothness to that, not time.

## Lifetimes, and what survives what

`Controller` lives for the whole process. `SwitcherStrip` does not, and the split is
deliberate: the strip is disposable, rebuilt from scratch whenever the watchdog finds it
missing or `TaskbarCreated` arrives. That is the price of being a taskbar child, and paying
it in one place keeps an Explorer restart from being an application-level event.

The recovery path drops the COM objects, invalidates the notification sink, reconciles, and
rebuilds. It also refuses to rebuild too early: Explorer creates `Shell_TrayWnd` before
`TrayNotifyWnd`, and anchoring to a taskbar without a notification area parks the strip to
the right of the clock. The watchdog simply tries again a second later.

## Design notes

**Desktop identity is a `Guid`, never an index.** Removing a desktop renumbers every one
after it, so an index captured before an async notification arrives can easily refer to a
different desktop by the time it is used. Anything that mutates state takes a `Guid`;
`Desktop.Index` is for drawing a number on a button and nothing else.

**Updates are event-driven, with polling as a safety net.** Shell notifications drive the
UI, so the highlight changes instantly. A slow reconcile tick covers a dead notification
sink, missed events and Explorer restarts.

**The strip animates by easing toward targets, never by playing a tween.** `SyncVisuals`
is the one place that decides what "current" and "hovered" look like, so it sets targets
and a frame timer eases toward them; every caller that already went through it animates
without knowing it does. Exponential smoothing rather than a fixed-duration tween because
retargeting mid-flight is then free — hold `Win+Ctrl+Right` and the current desktop changes
several times before anything settles, and a tween has to re-base its start value and start
time on every one of those or the highlight jumps. The timer stops as soon as every value
has arrived, and values snap onto their targets once the remaining distance is under 1/255,
where no pixel can change: this process runs from login to logout, and a permanently
ticking 60 Hz repaint is not something it gets to do. A change to the desktop *set* snaps
instead of animating — a removal renumbers everything after it, so animating across one
animates between two things that are not the same thing.

**The hover panel travels, and its edges are what get eased — not an origin and a size.**
Moving between buttons used to teleport it. It now slides, retargeting mid-flight for the
same reason the bar does, so sweeping along the strip is one continuous movement rather than
a queue of jumps. The state it eases is `left`, `top`, `bottom` and the anchored button's
centre, which is less obvious than easing x/y/height and is the point: for a bottom taskbar
the bottom edge is *the same float as its own target*, so `Ease` reports it unmoved and the
edge is stationary in device pixels by construction. Easing y and height instead makes that
an identity in exact arithmetic which independent rounding, per-value epsilon snapping and
`Place`'s work-area clamps can each break. The button centre is eased alongside at the same
rate so the accent stub keeps a constant offset inside the panel: the two are meant to read
as one object, and a stub derived from a live window rect instead jumps a full button ahead
and rubber-bands back.

**The underline bar is strip-level state, not per-button.** Two independent per-button
floats can only cross-fade, which reads as one bar going out and another coming on. The bar
is a rectangle that eases as geometry and is drawn once outside the per-button loop, so it
travels from the old current button to the new one. That is the one place the original M6
plan was wrong: per-button tweening needed no change to `Render`, and the bar did.

**The hover panel is a top-level window, unlike the strip.** A child window is clipped to
its parent, and the taskbar is exactly as tall as the strip, so a panel parented there would
be invisible. It is `WS_EX_NOACTIVATE` and click-through: taking focus would break click
handling and overwrite the window the foreground tracker is holding, which is what
right-click-to-send depends on.

**Full window lists are built on hover, never on a timer — but occupancy, being a boolean,
can afford one.** Windows on other desktops stay enumerable, so one sweep plus one
`GetWindowDesktopId` per window buckets the whole machine at once; the result is cached for
a second. Cloaking cannot be used as the filter, because the shell cloaks suspended UWP
frames with the same flag it uses for windows on an inactive desktop — what separates them
is that the ghosts have no desktop at all. That sweep also resolves a process name per
window, which is the part a timer cannot be allowed to pay for repeatedly. The occupancy
dot needs none of it: `WindowInventory.OccupiedDesktops` runs the same enumeration and the
same `GetWindowDesktopId` calls, skips everything after, and stops early once every desktop
passed in has turned up occupied — cheap enough for the reconcile tick to call it every
couple of seconds, which is what feeds the dot.

**The stats tile is the panel's resting place when pinned, not a second panel.** It reuses
the hover panel rather than opening a window of its own, so it slides, themes and places
exactly as the desktop panels do. Only a click shows the figures. Hovering the tile gets the
same kind of one-line hint `+` gets, because a live readout appearing on a passing hover was
more than anyone asked for. Pinning changes one thing: every path that used to hide
the panel — the pointer leaving, a click, a desktop change — goes through `RestOrHide`
instead, which sends a pinned panel back to the tile. The panel still follows the pointer to
describe other buttons in between. A desktop change leaves a stats panel untouched rather
than re-anchoring it, and this is safe because the strip is right-anchored and the tile is
its last button, so the tile does not move on screen when the count changes. The pin lives
in `Controller` as well as the strip, because the strip is thrown away on every rebuild and
an unlock is not the user closing the panel.

**The tile is a reading too.** It draws two meters, CPU and memory, instead of a mark.
`Controller.PushStatsLevels` hands them the latest reading wherever one is taken — the
reconcile tick, or the open panel's once-a-second refresh — and `SetStatsLevels` only
retargets, so the frame timer eases the bars like every other value on the strip. Network
has no meter because it has no top: any fixed scale would sit full on a fast link or flat on
a slow one.

`statsTileStyle = gauge` swaps the two meters for a half-ring gauge with one needle on CPU
alone, for a tile that should read as an icon. It is drawing only - the needle rides the
same eased `_cpuLevel` the CPU bar does, so it sweeps rather than jumps with no animation
code of its own. `statsTileStyle = chip` goes further and draws a processor chip that says
nothing until the heavier of CPU and memory crosses `statsWarnPercent`; its colour rides
the same eased levels, so it shifts to amber rather than flicking. `monitor` and `fan` are
the same idea in other shapes, sharing the chip's inks through `IconInks`. The fan can
spin with `statsFanSpin`, on a timer of its own at about 30 fps that repaints only the tile:
folding it into the frame timer would keep every ease on the strip stepping forever, since
a spinning fan never arrives. `rings` is the panel's ring gauges in miniature, CPU outside and memory inside,
on the same eased levels as the bars. Every style but the meters is sized by
`statsIconSize`, not the bars' height.

**The stats panel is tiles, drawn from history `SystemStats` already keeps.** Every reading
taken is appended to `History` and aged out after a minute, so the charts have data the
moment the panel opens - two seconds apart from the reconcile tick, one second apart once
it is open. Points are placed by their own timestamp, not their index, which is what makes
the uneven spacing harmless. `TooltipContent` can carry a list of `StatsTile` - label,
figure, ring gauge, detail, chart series - already normalised to 0..1 by
`Controller.StatsContent`, so `TooltipWindow` only maps them onto rectangles and knows
nothing about percent or bytes. The tiles go into the panel as a single row of their own
height, so `RowsHeight`, `Measure` and `RowOrigin` - and with them the pinned-edge slide that
`--slide` checks - cover the tiles without a second height calculation. The figures were text
rows in tab-stop columns at first, then rows with a boxed graph strip beside each; the strips
read as progress bars and were too short to show a shape.

**A pinned panel closes on a click elsewhere, and hides with the taskbar.** Both follow from
the panel being a top-level topmost window rather than the taskbar's child. Nothing the app
owns hears a click on another window, so `ClickWatcher` installs a low-level mouse hook while
the panel is pinned, and any press other than on the tile unpins it. "Keep open" from the
tile's menu pins it without the hook, for a panel that is meant to sit through clicks. The hook runs on a
thread that does nothing but pump messages, because every mouse event on the system waits
for the hook's thread to answer, and the UI thread is sometimes busy for tens of
milliseconds. The strip is hidden along with the taskbar, but the panel is not, so it would
float over a fullscreen video or hang in mid-air above an auto-hidden taskbar.
`SwitcherStrip.StatsCovered` checks for both — the strip off its monitor, or
`SHQueryUserNotificationState` reporting a fullscreen app — and the panel is put away
without being unpinned until the taskbar is back.

**Stats readings are cheap because the adapter list is cached.** CPU and memory are kernel32
reads costing microseconds. `NetworkInterface.GetAllNetworkInterfaces` measured ~70 ms a
call, too much for the UI thread every reconcile tick, while reading byte counters off
adapters already enumerated measured 0.1 ms. So the list is rebuilt only when
`NetworkChange` reports an address change, or every five minutes as a fallback.
`PerformanceCounter` was avoided for the same kind of reason: the first one constructed in
a process can stall for a second, and its category names are localised.

**The foreground window cannot be read at click time.** Clicking the taskbar hands focus to
Explorer, so by the time a handler runs, the foreground window is `Shell_TrayWnd` — and
moving that fails with `E_ACCESSDENIED`, which is the correct answer to the wrong question.
Hence sampling on a timer.

**The process is DPI-aware.** Explorer is, and a DPI-unaware child of the taskbar has its
coordinates silently scaled — on a 125% display a strip positioned at x=1110 lands at
x=888, correct in every log and wrong on screen. Every size in `config.ini` is authored for
96 DPI and scaled through `TaskbarHost.Scale`.

**Renames go through the shell, not the registry.** `VirtualDesktopApi.GetName` reads the
name out of the registry, but `SetName` reaches an undocumented vtable slot instead of
writing that value back — so Explorer is told rather than found out, Task View picks the
name up, and its own cache does not go stale behind us.

## Adding to this

The grain of the codebase, if you are extending it:

- **A new gesture on the strip** — `SwitcherStrip` raises an event carrying a `Guid` or an
  index; `Controller` decides what it means. Do not let the strip call `DesktopService`.
- **A new shell operation** — it goes in `VirtualDesktopApi`, wrapped in `Do()`, exposed
  through `DesktopService`, and gets a `--command` in `SelfTest` so it can be checked
  against a live shell.
- **A new setting** — add the field to `Config` and one entry to `Config.All`: key,
  section, label, help text, range, accessors, and `.When(...)` if it only means something
  while another setting is on. That is all. Parsing, the commented line in `config.ini`,
  the row in the Settings window and the `--config` round trip all come from that entry,
  and `Incomplete` checks the file against the same table, so existing config files get
  rewritten with the new key on next launch.
- **Where settings take effect** — `Controller.ApplyConfig` is the only way in, for the
  tray's reload, a hand edit `ConfigWatcher` noticed, and every change in the Settings
  window. Something that has to happen when a setting changes goes there or in
  `RebuildStrip`, which it calls, and then holds for all three.
- **Anything drawn** — sizes are authored at 96 DPI and passed through `Scale`, and colours
  come from `Palette` rather than being hardcoded or lightened by hand. Nothing outside
  that file may assume the taskbar is dark; three files each assumed it independently, and
  each of them was invisible on a light one.

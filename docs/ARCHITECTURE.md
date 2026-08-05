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
| [WindowInventory.cs](../src/model/WindowInventory.cs) | Which windows are on which desktop, and which app owns each one. Built on demand, cached for a second. |
| [ForegroundTracker.cs](../src/model/ForegroundTracker.cs) | Remembers the last real window to hold focus, so "send the active window here" has something to send. |

### `src/ui/` — pixels and input

| File | Purpose |
|---|---|
| [TaskbarHost.cs](../src/ui/TaskbarHost.cs) | Finds the taskbar, works out where the strip goes, docks it there, and samples the taskbar's colour. |
| [SwitcherStrip.cs](../src/ui/SwitcherStrip.cs) | The strip itself: owner-drawn buttons, animation, mouse input. Raises intent as events; decides nothing. |
| [TooltipWindow.cs](../src/ui/TooltipWindow.cs) | The hover panel. A top-level, click-through window that draws whatever text it is handed. |
| [MenuTheme.cs](../src/ui/MenuTheme.cs) | Dresses a `ContextMenuStrip` in colours derived from the sampled taskbar colour. |
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
| [Config.cs](../src/app/Config.cs) | The `config.ini` round trip. Unknown keys preserved, malformed values fall back, missing keys trigger a rewrite. |
| [Log.cs](../src/app/Log.cs) | Rolling diagnostic log, off by default, thread-safe because notifications are not. |
| [AssemblyInfo.cs](../src/app/AssemblyInfo.cs) | Name, description and version. Attributes only; `csc` stamps the Win32 version resource from them, which is how an exe with no project file gets a Details tab. |

### `src/selftest/` — proving it against the real shell

[SelfTest.cs](../src/selftest/SelfTest.cs) is the largest file in the tree and none of it
runs in the shipped app. There are no unit tests here and there is no good way to write
any: the interfaces are undocumented, live only inside a running Explorer, and have no
stand-in worth building. So each layer got a command that drives it against the real thing
and prints what it saw. Those are how a change is checked, and how a new Windows build
would be proved out.

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

`Controller` runs six timers, all on the UI thread:

| Timer | Interval | Job |
|---|---|---|
| startup | 500 ms | waits for the taskbar to exist at login, then stops |
| reconcile | `reconcileMs` (2000) | model safety net |
| watchdog | 1000 ms | is the taskbar still there, is the strip still in it |
| focus | 300 ms | sample the foreground window |
| save | 2000 ms | debounced config write |
| theme | 600 ms | debounced rebuild after `WM_SETTINGCHANGE`/`ImmersiveColorSet` |

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

**The hover panel is a top-level window, unlike the strip.** A child window is clipped to
its parent, and the taskbar is exactly as tall as the strip, so a panel parented there would
be invisible. It is `WS_EX_NOACTIVATE` and click-through: taking focus would break click
handling and overwrite the window the foreground tracker is holding, which is what
right-click-to-send depends on.

**Window lists are built on hover, never on a timer.** Windows on other desktops stay
enumerable, so one sweep plus one `GetWindowDesktopId` per window buckets the whole machine
at once; the result is cached for a second. Cloaking cannot be used as the filter, because
the shell cloaks suspended UWP frames with the same flag it uses for windows on an inactive
desktop — what separates them is that the ghosts have no desktop at all.

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
- **A new setting** — add the field, the `Apply` case and the `Render` line in `Config`.
  `Incomplete` reads the expected keys back out of what `Render` writes, so existing config
  files get rewritten with the new key on next launch; there is no second list to update.
- **Anything drawn** — sizes are authored at 96 DPI and passed through `Scale`, and colours
  come from `Palette` rather than being hardcoded or lightened by hand. Nothing outside
  that file may assume the taskbar is dark; three files each assumed it independently, and
  each of them was invisible on a light one.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DesktopSwitcher
{
    /// <summary>
    /// Owns the application. A hidden top-level window that never shows, existing to
    /// pump messages, host the tray icon, and marshal notification callbacks.
    ///
    /// The strip is deliberately NOT owned by this window's lifetime. It is a child of
    /// Shell_TrayWnd, so an Explorer restart destroys it; the watchdog notices and
    /// rebuilds. Keeping the two separate is what makes that survivable.
    /// </summary>
    sealed class Controller : Form
    {
        // Not readonly: the tray's reload item re-reads config.ini and swaps this for what
        // the file now says. Nothing holds a reference to the old one - every reader goes
        // through this field - so the swap is the whole of it.
        Config _config;

        readonly VirtualDesktopApi _api = new VirtualDesktopApi();
        readonly TaskbarHost _host = new TaskbarHost();
        readonly ForegroundTracker _foreground = new ForegroundTracker();
        readonly SystemStats _stats = new SystemStats();
        readonly PublicIp _publicIp;     // made once the handle exists - it marshals through it

        DesktopService _service;
        WindowInventory _inventory;
        WindowCloser _closer;           // the menu's close-all, over the same inventory
        CloseAllRun _closeRun;          // the one in progress, panel and all, or null
        SwitcherStrip _strip;
        NotifyIcon _tray;
        Icon _trayIcon;

        ContextMenuStrip _buttonMenu;   // the open one, rebuilt per click - contents follow the model
        MenuTheme _menuTheme;
        ConfigWatcher _configWatcher;   // applies hand edits to config.ini as they are saved
        Hotkeys _hotkeys;               // the desktop shortcuts, held on this window
        SettingsWindow _settings;       // the open one, or null - there is only ever one
        Config _settingsPending;        // the window's latest change, waiting out the debounce
        Timer _settingsTimer;           // that debounce

        Timer _glideTimer;       // runs only while the strip eases to a moved anchor
        int _glideTick;
        Timer _startupTimer;     // waits for the taskbar to exist at login
        Timer _reconcileTimer;   // model safety net
        Timer _watchdogTimer;    // strip / taskbar health
        WinEventWatcher _foregroundWatch;   // remembers the last real foreground window
        Timer _saveTimer;        // debounced config write
        Timer _settleTimer;      // debounced rebuild after a theme, accent, or display change

        uint _taskbarCreatedMessage;
        uint _quitMessage;           // see Installer.QuitMessageName
        int _buttonWidth, _plusWidth, _statsWidth, _margin, _barHeight;
        Color _background;
        bool _started;
        bool _statsPinned;            // outlives the strip, which a rebuild throws away
        bool _statsKept;              // likewise: opened by "Keep open", not by a click
        int _ipCopiedAt;              // TickCount of the last copy, for the "copied" feedback
        bool _ipCopied;               // whether _ipCopiedAt means anything yet
        bool _sessionNotifications;   // whether the unlock message is being delivered
        bool _paused;                 // session locked: the reconcile and watchdog ticks are stopped
        string _settleReason = "";    // what queued the pending rebuild, for the log
        int _pendingCount = -1;

        // Desktop marks. The store is loaded once, here, and written on every change; the
        // rules that keep it in step with the desktops are MarkSync's.
        readonly MarkSync _markSync = new MarkSync(MarkStore.Load());
        MarkStore _marks { get { return _markSync.Store; } }
        int _markedWidth;             // a marked button's width, DPI-scaled
        MarkPicker _picker;           // the open one, or null - there is only ever one
        int _asking;                  // dialogs open over the strip - see BeginAsking
        Guid _pickerId;               // the desktop the open picker marks; follows a reorder
        DesktopMark _preview;         // what the open picker shows on that desktop
        bool _previewing;

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunValue = "DesktopSwitcher";

        public Controller(Config config)
        {
            _config = config;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            WindowState = FormWindowState.Minimized;
            Text = "DesktopSwitcher";

            _taskbarCreatedMessage = Native.RegisterWindowMessage("TaskbarCreated");
            _quitMessage = Native.RegisterWindowMessage(Installer.QuitMessageName);

            // Force the window into existence, here, on the UI thread.
            //
            // SetVisibleCore below means WinForms never shows this form, and a form that is
            // never shown never creates a window. Everything downstream of that is quiet
            // until it is not: Control.InvokeRequired answers false when there is no handle
            // to compare threads against, so DesktopService.Post - which asks exactly that
            // question - concluded no marshalling was needed and ran every shell
            // notification inline, on the RPC thread it arrived on. The model, the strip's
            // list, the tooltip and the repaint were all being touched from a thread that
            // was never meant to see them; it went unnoticed because each of those happens
            // to survive it. The animation timer does not - a WinForms timer started from a
            // thread with no message pump never ticks once - and that is what dragged this
            // into the light.
            IntPtr pump = Handle;
            GC.KeepAlive(pump);

            // After the handle, for the reason above: its answers arrive on a pool thread
            // and are marshalled back through this form.
            _publicIp = new PublicIp(this);

            // Needs the handle, so it goes after the line above and not before it.
            _sessionNotifications =
                Native.WTSRegisterSessionNotification(pump, Native.NOTIFY_FOR_THIS_SESSION);
            if (!_sessionNotifications)
                Log.Write("controller: session notifications unavailable - unlock resample disabled");

            BuildTrayIcon();

            // Explorer may not be serving windows yet when a startup entry fires.
            _startupTimer = new Timer();
            _startupTimer.Interval = 500;
            _startupTimer.Tick += TryStart;
            _startupTimer.Start();
        }

        /// <summary>Never becomes visible, whatever WinForms or the user tries.</summary>
        protected override void SetVisibleCore(bool value)
        {
            base.SetVisibleCore(false);
        }

        // --- startup ----------------------------------------------------------

        void TryStart(object sender, EventArgs e)
        {
            if (!_host.Locate() || !_host.HasAnchor) return;

            _startupTimer.Stop();
            _startupTimer.Dispose();
            _startupTimer = null;

            // Metrics are computed inside RebuildStrip, below, and deliberately not here:
            // the taskbar colour is sampled from a point derived from the desktop count,
            // and there is no count until the service exists.
            _service = new DesktopService(_api, this);
            _inventory = new WindowInventory(_api);
            _closer = new WindowCloser(_api, _inventory);
            _service.Start();

            // Windows forgets the desktop count across a reboot; put it back.
            if (_config.LastCount > _service.Count)
                _service.EnsureCount(_config.LastCount);

            // Before the first strip, so it comes up already marked. Recreated is subscribed
            // here rather than with the events below because it is about the marks, not the
            // strip, and has to be in place before any reorder can run.
            _service.Recreated += OnRecreated;
            _markSync.Start(MarkSync.AdoptMs);
            SyncMarks();

            // Subscribed only after the initial model settles, so the first
            // DesktopsChanged does not build a strip that RebuildStrip immediately
            // replaces.
            _service.DesktopsChanged += OnDesktopsChanged;
            _service.CurrentChanged += OnCurrentChanged;
            _service.NamesChanged += OnNamesChanged;

            // A window moved between desktops: the one change to which desktops have windows
            // that no WinEvent reports, since nothing is shown, hidden or destroyed.
            _service.WindowsMoved += delegate { _inventory.MarkChanged(); };

            RebuildStrip();

            // The baseline the first reconcile tick will measure against, so a hover in the
            // first two seconds after login already has a rate to show. The CPU measure is
            // set first, because the baseline is per measure.
            _stats.UseUtility = _config.StatsCpuUtility;
            SampleStats();

            _reconcileTimer = StartTimer(_config.ReconcileMs, delegate
            {
                _service.Tick();
                RefreshOccupancy(true);
                SampleStats();
                if (SyncMarks()) ApplyMarks();
            });
            _watchdogTimer = StartTimer(1000, delegate { Watchdog(); });

            // The watchdog is too slow to follow a tray change: the tile beside the strip
            // moves at once and would sit over it for up to a second. These hear the change
            // as it happens instead - see WatchTray. Made here and then kept current by the
            // watchdog, which remakes them when an Explorer restart changes the process.
            WatchTray();
            WatchTile();

            // Every focus change as it happens, so "send the active window here" always has
            // the window the user last meant. Was a 300 ms poll, which both woke the process
            // three times a second for nothing and could miss a window focused just before
            // the click on the strip took focus away again. System-wide, because focus moves
            // between every process; rare enough that it costs nothing.
            _foregroundWatch = new WinEventWatcher(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND,
                                                   0, false, delegate { _foreground.Sample(); });
            _foreground.Sample();

            _saveTimer = new Timer();
            _saveTimer.Interval = 2000;
            _saveTimer.Tick += SavePending;

            // First run: write a complete file so every setting is visible and editable
            // rather than having to be guessed at.
            if (!File.Exists(Config.FilePath))
            {
                _config.LastCount = _service.Count;
                _config.Save();
                Log.Write("controller: wrote initial config");
            }
            else if (_config.Outdated)
            {
                // Same reasoning one upgrade later: a file written before a setting existed
                // leaves it on its default with no line to edit, which is the very state
                // the first-run write exists to prevent. A file written before the comments
                // existed is rewritten too, so it explains itself the next time it is
                // opened. Every value already in the file survives, having just been loaded
                // from it.
                _config.Save();
                Log.Write("controller: config rewritten in the current layout");
            }

            // After the writes above, so the directory is certain to exist and the rewrite is
            // recorded as this process's own rather than seen as a hand edit.
            _configWatcher = new ConfigWatcher(this, OnConfigFileChanged);

            // Only once the service exists: a key pressed before it does would have nothing
            // to switch with.
            _hotkeys = new Hotkeys(Handle);
            RegisterHotkeys();

            _started = true;
            Log.Write("controller: started");
        }

        /// <summary>
        /// Positions the strip, and starts the glide if that found the anchor had moved.
        /// Every placement goes through here, so none of them can make the strip jump.
        /// </summary>
        void PlaceStrip()
        {
            _host.Reassert(_strip.Handle, _strip.Width, _margin);
            if (_host.Gliding) StartGlide();
        }

        // MediaController's tile moves first and the strip follows it. Polled, the strip was
        // always a few frames behind a tile that is itself easing, so the tile sat over the
        // strip. A location hook on the tile's process says when it moves; the tile arriving
        // or leaving is heard by the show/hide hook in WatchTray, which is when this hook is
        // (re)made.
        WinEventWatcher _tileWatch;

        void WatchTile()
        {
            uint pid = _host.TileProcessId;
            if (_tileWatch != null && _tileWatch.ProcessId == pid) return;
            if (_tileWatch != null) { _tileWatch.Dispose(); _tileWatch = null; }
            if (pid != 0)
                _tileWatch = new WinEventWatcher(Native.EVENT_OBJECT_LOCATIONCHANGE, Native.EVENT_OBJECT_LOCATIONCHANGE,
                                                 pid, false, delegate { OnAnchorEvent(); });
        }

        // The tray's own two ways of moving the anchor, heard rather than polled. They replace
        // a 40 ms timer that woke the process twenty-five times a second, all day, to find out
        // something that happens a few times an hour.
        //
        // _trayWatch: Explorer moving a window of its own. A tray icon promoted out of the
        // overflow widens TrayNotifyWnd, which Explorer does with SetWindowPos, which raises a
        // location event for that child. Hooked on Explorer's process only - our strip is a
        // child of the same taskbar but belongs to us, so placing it cannot echo back here -
        // and filtered to the two windows the anchor is read from. Explorer moves plenty of
        // other windows (a File Explorer window being dragged is a burst of them), and each
        // of those costs two compares.
        //
        // _windowWatch: a window shown, hidden or destroyed anywhere. Two listeners share it.
        // The occupancy dots hear every one (OnWindowEvent hands it to the inventory, which
        // keeps only the windows its sweep would count). The anchor keeps only children of the
        // taskbar: that is MediaController's tile arriving or leaving: it lives in another
        // process, so Explorer's hook cannot see it, and until it has arrived there is no
        // process to hook. These are rare next to location changes, and our own windows -
        // the hover panel above all - are skipped by the system before they reach us. Destroy
        // is in the range because a visible child destroyed outright raises no hide (a probe
        // creating and destroying one under Shell_TrayWnd saw the show and nothing else), and
        // it is matched against the tile's handle rather than by parent, because the window
        // has none left by the time the event is delivered.
        //
        // The watchdog still places the strip every second, and that stays the fallback for
        // anything these miss.
        WinEventWatcher _trayWatch;
        WinEventWatcher _windowWatch;

        void WatchTray()
        {
            uint pid = 0;
            IntPtr tray = _host.TrayWindow;
            if (tray != IntPtr.Zero) Native.GetWindowThreadProcessId(tray, out pid);

            if (_trayWatch == null || _trayWatch.ProcessId != pid)
            {
                if (_trayWatch != null) { _trayWatch.Dispose(); _trayWatch = null; }
                if (pid != 0)
                    _trayWatch = new WinEventWatcher(Native.EVENT_OBJECT_LOCATIONCHANGE, Native.EVENT_OBJECT_LOCATIONCHANGE,
                                                     pid, false, OnTrayMoved);
            }

            if (_windowWatch == null)
                _windowWatch = new WinEventWatcher(Native.EVENT_OBJECT_DESTROY, Native.EVENT_OBJECT_HIDE,
                                                   0, true, OnWindowEvent);
        }

        void OnTrayMoved(uint eventType, IntPtr hwnd)
        {
            if (hwnd != _host.NotifyArea && hwnd != _host.TrayWindow) return;
            OnAnchorEvent();
        }

        void OnWindowEvent(uint eventType, IntPtr hwnd)
        {
            if (_inventory != null)
            {
                if (eventType == Native.EVENT_OBJECT_SHOW) _inventory.WindowShown(hwnd);
                else _inventory.WindowGone(hwnd);
            }

            IntPtr tray = _host.TrayWindow;
            if (tray == IntPtr.Zero) return;
            if (hwnd != _host.TileWindow && Native.GetParent(hwnd) != tray) return;
            OnAnchorEvent();
        }

        /// <summary>
        /// Something the anchor is read from may have moved. Places the strip only if it did,
        /// then makes sure the tile hook follows whichever window is now the anchor - in that
        /// order, because it is the placement that finds out the tile has become the anchor.
        /// </summary>
        void OnAnchorEvent()
        {
            if (_strip == null) return;
            if (_host.AnchorMoved()) PlaceStrip();
            WatchTile();
        }

        void StartGlide()
        {
            if (_glideTimer == null)
            {
                _glideTimer = new Timer();
                _glideTimer.Interval = Motion.FrameMs;
                _glideTimer.Tick += OnGlideFrame;
            }
            if (_glideTimer.Enabled) return;
            _glideTick = Environment.TickCount;
            _glideTimer.Start();
        }

        void OnGlideFrame(object sender, EventArgs e)
        {
            int now = Environment.TickCount;
            int elapsed = now - _glideTick;
            _glideTick = now;

            if (_strip == null) { _glideTimer.Stop(); return; }

            _host.StepGlide(elapsed);
            _host.Reassert(_strip.Handle, _strip.Width, _margin);
            if (!_host.Gliding) _glideTimer.Stop();
        }

        Timer StartTimer(int interval, EventHandler tick)
        {
            var t = new Timer();
            t.Interval = interval;
            t.Tick += tick;
            t.Start();
            return t;
        }

        /// <summary>
        /// Every DPI-scaled size the strip is built from, and the colour it sits on.
        ///
        /// Takes the desktop count because the colour sample depends on it: the probe point
        /// sits just left of the strip, so it has to be told how wide the strip will be. It
        /// used to assume two desktops whatever the count, which with four at 125% put the
        /// probe inside the strip's own rectangle.
        ///
        /// Only ever called from RebuildStrip, and only after DisposeStrip - see there.
        /// </summary>
        void ComputeMetrics(int desktopCount)
        {
            _buttonWidth = _host.Scale(_config.ButtonWidth);
            _markedWidth = _host.Scale(SwitcherStrip.MarkedWidthFor(_config.ButtonWidth));
            _plusWidth = _host.Scale(_config.PlusWidth);
            _statsWidth = _config.StatsTile ? _host.Scale(_config.StatsWidth) : 0;
            _margin = _host.Scale(_config.Margin);
            _barHeight = _host.Scale(3);

            if (!_config.BackgroundColor.IsEmpty)
            {
                _background = _config.BackgroundColor;
                return;
            }

            // The marked buttons counted in: the probe sits just off the strip's left edge,
            // and marks make the strip wider than the count alone says.
            Color sampled;
            int probe = SwitcherStrip.MeasureWidth(desktopCount, MarkedCount(), _buttonWidth, _markedWidth,
                                                   _plusWidth, _statsWidth);

            if (_host.TrySampleBackground(probe, _margin, out sampled))
                _background = sampled;
            else if (_background.IsEmpty)
                _background = Color.FromArgb(0x1F, 0x1F, 0x1F);

            // The third case is deliberate: a failed read keeps the last colour that did
            // come back. Sampling used to happen three times in a run and now happens on
            // every rebuild, the watchdog's included, so a single unanswered GetPixel mid
            // Explorer restart must not repaint a strip that was perfectly fine into the
            // hardcoded dark grey - which on a light taskbar is a black block sitting there
            // until something else happens to rebuild.
        }

        // --- the strip --------------------------------------------------------

        void RebuildStrip()
        {
            DisposeStrip();
            _host.ResetGlide();

            int count = _service.Count > 0 ? _service.Count : 1;

            // After the strip is gone, never before, and never anywhere else. The sample
            // point is derived from the strip's width and sits a few pixels off its left
            // edge, so with a strip still on screen - which is what the tray's reload item
            // and a display change both used to do - what is read back is at best a pixel
            // the strip has been influencing and at worst the strip itself. The one gesture
            // meant to notice the taskbar had changed could only ever confirm itself.
            //
            // The window is destroyed but Explorer has not necessarily repainted what it
            // was covering yet. That is why the probe steps clear of the strip rather than
            // reading where it stood: the pixel under it may be stale for another frame,
            // the one beside it was never ours.
            ComputeMetrics(count);

            int width = SwitcherStrip.MeasureWidth(count, MarkedCount(), _buttonWidth, _markedWidth,
                                                   _plusWidth, _statsWidth);

            Rectangle bounds;
            if (!_host.TryComputeBounds(width, _margin, out bounds))
            {
                Log.Write("controller: could not compute strip bounds");
                return;
            }

            // Creating a child of Shell_TrayWnd can fail, and at login it does: the taskbar
            // window exists and has a notification area, which is all TryStart waits for, but
            // Explorer may still be replacing it, and CreateWindowEx on a parent that is
            // gone or not yet accepting children throws Win32Exception "Error creating
            // window handle". Uncaught, that escaped the timer tick as WinForms' "Unhandled
            // exception" dialog, and the damage was not the dialog. TryStart had already
            // stopped its startup timer and had not yet reached the lines that start the
            // watchdog or set _started, so the app sat in the tray for good with no strip
            // and nothing left running that would ever try again.
            //
            // So a failure here is an ordinary "no strip yet": leave _strip null, which is
            // exactly the state the watchdog already treats as "strip missing - rebuilding",
            // and let it retry every second until Explorer has settled. A stale parent is
            // also caught by its IsHealthy check, which re-locates the taskbar first.
            try
            {
                _strip = new SwitcherStrip(_host.TrayWindow, bounds,
                                           _buttonWidth, _markedWidth, _plusWidth, _statsWidth, _barHeight,
                                           _background, _config.HighlightColor,
                                           (uint)_config.TooltipDelayMs, _config.TooltipWidth,
                                           _config.AnimationMs, _host.DpiScale,
                                           _config.StripLook, _config.PanelLook);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                Log.Write("controller: strip window creation failed (win32 " + ex.NativeErrorCode
                          + ") - " + ex.Message + "; watchdog will retry");
                _strip = null;
                return;
            }

            _strip.SwitchRequested += delegate(Guid id) { _service.SwitchTo(id); };
            _strip.CreateRequested += delegate { _service.Create(); };
            _strip.RemoveRequested += delegate(Guid id) { RequestRemove(id); };
            _strip.MoveWindowRequested += OnMoveWindowRequested;
            _strip.ReorderRequested += delegate(int from, int to) { Reorder(from, to, "drag"); };
            _strip.TooltipProvider = BuildTooltip;
            _strip.StatsProvider = BuildStats;
            _strip.MarkProvider = MarkFor;
            _strip.OccupancyShown = _config.OccupancyDots;

            // A strip rebuilt under an open dialog - an Explorer restart, a theme change -
            // keeps its panel away like the one it replaced.
            _strip.Asking = _asking > 0;
            _strip.SetStatsMeter(_host.Scale(_config.StatsBarWidth), _host.Scale(_config.StatsBarGap),
                                 _host.Scale(_config.StatsBarHeight), _config.StatsBarLabels, _config.StatsBarRounded,
                                 _config.StatsWarnPercent, _config.StatsAlertPercent, _config.StatsStyle,
                                 _host.Scale(_config.StatsIconSize), _config.StatsFanSpin,
                                 _config.StatsFanSpeed);
            // Off while the address is not being shown: the gesture then falls through to
            // what that click did before - a pin toggle, or nothing.
            _strip.StatsCopyGesture = _config.StatsPublicIp ? _config.StatsCopyGesture : TileGesture.Off;
            _strip.StatsCopyRequested += delegate { CopyPublicIp(); };
            _strip.StatsKeepGesture = _config.StatsKeepGesture;
            _strip.StatsTaskManagerGesture = _config.StatsTaskManagerGesture;
            _strip.StatsTaskManagerRequested += delegate { OpenTaskManager(); };
            _strip.StatsPinChanged += delegate(bool pinned, bool kept)
            {
                _statsPinned = pinned;
                _statsKept = kept;
            };

            // Left unsubscribed when the menu is off, which is what puts right click back to
            // sending the active window: the strip falls through when nothing is listening.
            if (_config.ContextMenu)
            {
                _menuTheme = new MenuTheme(_background, _host.DpiScale);
                _strip.ContextMenuRequested += ShowButtonMenu;
            }

            _foreground.Ignore(_strip.Handle);

            _strip.SetDesktops(_service.Desktops);
            PlaceStrip();
            RefreshOccupancy();
            PushStatsLevels();

            // After Reassert, so the panel anchors to where the tile actually is. A pinned
            // panel survives a rebuild - an unlock or a theme change is not the user putting
            // it away - unless the tile itself was just turned off in config, which drops the
            // pin with it rather than leaving one to spring back if the tile is turned on.
            if (_statsPinned)
            {
                if (_statsWidth > 0) _strip.SetStatsPinned(true, _statsKept);
                else _statsPinned = _statsKept = false;
            }

            // Every rebuild resamples the taskbar colour, so this is where an open Settings
            // window hears that it changed - a theme switch, or a custom background it just
            // applied itself.
            if (_settings != null) _settings.Retheme(_background);

            Log.Write("controller: strip rebuilt");
        }

        /// <summary>
        /// Which desktops have windows, pushed to the strip's occupancy dots. Called from
        /// the reconcile tick, which is what "refreshed on the existing 2s tick" means, and
        /// also right after every relayout - a rebuild or a desktop-set change - so the
        /// strip does not sit undotted for up to 2s after either.
        ///
        /// The tick passes onlyIfStale, and then does nothing at all unless a window has
        /// come, gone or moved since the last answer (see WindowInventory.MarkChanged): no
        /// sweep, and no push, which would repaint the strip with the dots it already has.
        /// A relayout pushes regardless, because a new strip has no dots yet; when nothing
        /// changed that costs no sweep either, since the inventory hands back its last answer.
        ///
        /// Skipped below two desktops, where the dot would only ever answer a question
        /// nobody is asking: with one desktop there is nowhere else for a window to be.
        /// </summary>
        void RefreshOccupancy()
        {
            RefreshOccupancy(false);
        }

        void RefreshOccupancy(bool onlyIfStale)
        {
            if (_strip == null || _service == null || _inventory == null) return;
            if (!_config.OccupancyDots) return;
            if (_service.Count < 2) return;
            if (onlyIfStale && !_inventory.OccupancyStale(_service.Count)) return;

            _strip.SetOccupancy(_inventory.OccupiedDesktops(_service.Count));
        }

        /// <summary>
        /// Keeps a recent CPU and network reading on hand, so the stats panel can show real
        /// figures the moment it opens. Both are rates and need a previous reading to
        /// measure from; without this the first panel of every hover could only say it was
        /// measuring. On the reconcile tick because that is already running - the sample
        /// costs well under a millisecond, against the occupancy sweep beside it - and
        /// skipped entirely with the tile turned off.
        ///
        /// Also skipped while the panel is open, because the panel then samples once a
        /// second on its own. Two samplers on unrelated clocks made each rate cover anywhere
        /// from 0.6s to 1.4s, depending on where the reconcile tick happened to land, and the
        /// CPU figure jumped with it. See SwitcherStrip.StatsShowing.
        ///
        /// The tile's meters are fed either way: with the panel open this pushes the panel's
        /// own latest reading again, which the strip ignores as unchanged.
        /// </summary>
        void SampleStats()
        {
            if (!_config.StatsTile) return;
            if (_strip == null || !_strip.StatsShowing) _stats.Sample();
            PushStatsLevels();
        }

        /// <summary>
        /// The latest CPU and memory reading, to the tile's meters. Called wherever a reading
        /// is taken, and after a rebuild so a new strip does not sit with empty meters until
        /// the next tick.
        /// </summary>
        void PushStatsLevels()
        {
            if (_strip == null || !_config.StatsTile) return;
            _strip.SetStatsLevels(_stats.CpuPercent, _stats.HasMemory ? (float)_stats.MemoryLoad : -1f);
        }

        void DisposeStrip()
        {
            if (_strip == null) return;

            // Before the strip goes, so an Explorer restart cannot leave a menu on screen
            // anchored to a button that no longer exists.
            CloseButtonMenu();

            _strip.Dispose();
            _strip = null;

            if (_menuTheme != null) { _menuTheme.Dispose(); _menuTheme = null; }
        }

        void OnMoveWindowRequested(Guid id)
        {
            IntPtr target = _foreground.Resolve();
            if (target == IntPtr.Zero)
            {
                Log.Write("controller: move requested but no candidate window");
                return;
            }

            Log.Write(delegate { return "controller: moving \"" + Native.GetText(target) + "\""; });
            _service.MoveWindow(target, id);
        }

        // --- button menu ------------------------------------------------------

        /// <summary>
        /// The menu behind a right click on a button.
        ///
        /// Built here rather than in the strip for the same reason tooltip text is: the
        /// items name windows and count them, which needs the model, the inventory and the
        /// foreground tracker together. Built fresh every time, because every caption
        /// describes the state at the moment of the click.
        ///
        /// The strip lights the button on the way in and only MenuClosed puts it out, so
        /// every path out of here has to reach one.
        /// </summary>
        void ShowButtonMenu(int index, Rectangle anchor)
        {
            if (_service == null || _strip == null) return;

            // Whatever was here is already closed - the strip cannot raise this while a menu
            // is up, since that click would have gone to the menu - so this is only freeing
            // the last one. Forgotten before it is disposed, so that if disposing does emit a
            // Closed the handler sees a menu that is no longer current and stays quiet: it
            // would otherwise put out the button that was just lit for the menu being built.
            if (_buttonMenu != null)
            {
                ContextMenuStrip stale = _buttonMenu;
                _buttonMenu = null;
                stale.Dispose();
            }

            ContextMenuStrip menu;
            try
            {
                menu = BuildButtonMenu(index);
            }
            catch (Exception ex)
            {
                Log.Write("controller: menu build failed - " + ex.Message);
                _strip.MenuClosed();
                return;
            }

            if (menu == null) { _strip.MenuClosed(); return; }

            _menuTheme.Apply(menu);
            menu.Closed += delegate
            {
                // Only the menu still current reports back; one closed to make way for
                // another has nothing to say about the button.
                if (menu == _buttonMenu && _strip != null) _strip.MenuClosed();
            };

            _buttonMenu = menu;

            // A drop-down whose owner is not the foreground window does not dismiss
            // reliably, and can be left on screen with nothing to close it. Safe for
            // send-window-here: ForegroundTracker only ever keeps alt-tab windows, and this
            // form is never visible and has no caption, so what it holds survives untouched.
            Native.ForceForeground(Handle);

            ToolStripDropDownDirection direction = DirectionFor(anchor);
            int y = direction == ToolStripDropDownDirection.BelowRight ? anchor.Bottom : anchor.Top;
            menu.Show(new Point(anchor.Left, y), direction);
        }

        /// <summary>
        /// Which way the menu grows out of the button: above it, or below when the taskbar
        /// is at the top of the screen. The same single rule the hover panel places itself
        /// by, and WinForms clamps the result onto the screen itself.
        /// </summary>
        static ToolStripDropDownDirection DirectionFor(Rectangle anchor)
        {
            Rectangle work = Screen.FromRectangle(anchor).WorkingArea;
            return anchor.Top <= work.Top
                ? ToolStripDropDownDirection.BelowRight
                : ToolStripDropDownDirection.AboveRight;
        }

        ContextMenuStrip BuildButtonMenu(int index)
        {
            IList<Desktop> desktops = _service.Desktops;
            var menu = new ContextMenuStrip();

            // Before the '+' case, which would otherwise claim it: the tile's index is also
            // past the last desktop.
            if (index == _strip.StatsIndex)
            {
                // The one way to a panel that stays put. A left click opens it as a popup,
                // which any click elsewhere closes; this opens it - or turns an open popup -
                // into one that only the tile or this item again will close. Unticking
                // closes it rather than dropping back to a popup: "do not keep it open" is
                // most naturally read as "close it".
                var keep = new ToolStripMenuItem("Keep open");
                keep.Checked = _statsKept;
                keep.ShortcutKeyDisplayString = GestureName(_config.StatsKeepGesture);
                keep.Click += delegate
                {
                    if (_strip == null) return;
                    if (_statsKept) _strip.SetStatsPinned(false);
                    else _strip.SetStatsPinned(true, true);
                };
                menu.Items.Add(keep);

                // The address in the caption, so the menu alone answers "what is it" without
                // opening the panel. Disabled until one is known; opening the menu is itself
                // a reason to ask, so a stale one is refreshed for next time.
                if (_config.StatsPublicIp)
                {
                    _publicIp.Refresh(false);
                    string address = _publicIp.Address;
                    AddItem(menu, "Copy public IP  (" + _publicIp.Display + ")", address != null,
                            delegate { CopyPublicIp(); });

                    // The gesture beside the item, where a menu says its shortcuts.
                    ((ToolStripMenuItem)menu.Items[menu.Items.Count - 1]).ShortcutKeyDisplayString =
                        GestureName(_config.StatsCopyGesture);
                }

                // Where to go when the panel's three figures raise a question it cannot
                // answer, which is usually "what is using it".
                AddItem(menu, "Task Manager", true, delegate { OpenTaskManager(); });
                ((ToolStripMenuItem)menu.Items[menu.Items.Count - 1]).ShortcutKeyDisplayString =
                    GestureName(_config.StatsTaskManagerGesture);
                return menu;
            }

            if (index >= desktops.Count)
            {
                AddItem(menu, "New desktop", true, delegate { _service.Create(); });
                return menu;
            }

            Desktop desktop = desktops[index];
            Guid id = desktop.Id;

            // First, where Task View puts it: the name is what the menu is about, and it
            // titles the rest rather than sitting among the actions.
            AddRenameBox(menu, desktop);

            // The mark is the desktop's identity too, so it sits with the name rather than
            // among the actions.
            // Greyed while a picker is open, for any desktop - see RequestMark.
            AddItem(menu, _marks.Get(id) != null ? "Change mark..." : "Set mark...", _picker == null,
                    delegate { RequestMark(id); });

            AddItem(menu, "Switch here", !desktop.IsCurrent, delegate { _service.SwitchTo(id); });

            // Resolved now, not when the item is clicked, so the window that moves is the
            // one the item named.
            IntPtr target = _foreground.Resolve();
            AddItem(menu, SendCaption(target), target != IntPtr.Zero && !IsOn(target, id),
                    delegate
                    {
                        Log.Write(delegate
                        {
                            return "controller: menu moving \"" + Native.GetText(target) + "\"";
                        });
                        _service.MoveWindow(target, id);
                    });

            // Reordering, one place at a time. Windows 10 has no call for it, so it is rebuilt
            // out of create and remove - see VirtualDesktopApi.Reorder - which is about 1 ms a
            // call on 19045 and so reads as instant. Greyed out while a close-all is running:
            // that run holds its desktop by GUID, and a reorder can hand the desktop a new one
            // underneath it.
            bool canReorder = _closeRun == null;
            AddItem(menu, "Move left", canReorder && index > 0,
                    delegate { RequestReorder(id, -1); });
            AddItem(menu, "Move right", canReorder && index < desktops.Count - 1,
                    delegate { RequestReorder(id, +1); });

            menu.Items.Add(new ToolStripSeparator());

            // Greyed out on an empty desktop, where it would only be Remove again. Pinned
            // windows are left alone by the click, so they do not count here either.
            AddItem(menu, "Close all and remove", desktops.Count > 1 && _closer.HasCloseable(id),
                    delegate { RequestCloseAll(id); });

            // Removing is the one item worth hesitating over, so it says what is at stake.
            // Windows moves the windows to another desktop rather than closing them, but
            // finding them again is still a nuisance.
            AddItem(menu, RemoveCaption(desktop), desktops.Count > 1,
                    delegate { RequestRemove(id); });

            return menu;
        }

        /// <summary>
        /// The one gate both removal paths - middle click and the menu's Remove item -
        /// go through. An empty desktop is removed on the spot, exactly as before;
        /// one with windows on it gets a themed confirmation first, since the OS moves
        /// those windows to a neighbour rather than closing them, and finding them again
        /// afterward is a nuisance worth a second click to avoid by accident.
        /// </summary>
        void RequestRemove(Guid id)
        {
            Desktop desktop = Find(id);
            if (desktop == null) return;

            int count = _inventory.WindowsOn(id).Count;
            if (count == 0)
            {
                _service.Remove(id);
                return;
            }

            string body = count == 1
                ? "1 window will move to the next desktop."
                : count + " windows will move to the next desktop.";

            if (Ask("Remove \"" + desktop.DisplayName + "\"?", body, "Remove", "Cancel") == 0)
                _service.Remove(id);
        }

        /// <summary>
        /// A themed confirmation over the strip. Returns the index of the button chosen,
        /// left to right. Escape picks the last, the safe one; -1 means closed some other way.
        /// </summary>
        int Ask(string title, string body, params string[] buttons)
        {
            Rectangle anchor = _strip != null ? _strip.ScreenBounds() : Rectangle.Empty;

            using (var dialog = new ConfirmDialog(title, body, buttons, _background,
                                                   _host.DpiScale, anchor))
            {
                // The hover panel would otherwise open over the dialog; see SwitcherStrip.Asking.
                BeginAsking();
                try
                {
                    dialog.ShowDialog(this);
                }
                finally
                {
                    EndAsking();
                }
                return dialog.Choice;
            }
        }

        /// <summary>
        /// The menu's "Close all and remove". Confirms first, with the count - a menu row is
        /// one misplaced click from the one above it, and this cannot be undone - then hands
        /// over to a CloseAllRun, which shows each window closing and removes the desktop
        /// once it is empty.
        ///
        /// One run at a time. A second request, for any desktop, brings the open panel
        /// forward instead: two panels over the strip would hide each other, and the
        /// common case is a second click on the same thing while the first is still going.
        ///
        /// The list is taken again here rather than carried from the menu, so the count
        /// in the dialog is the one acted on.
        /// </summary>
        void RequestCloseAll(Guid id)
        {
            if (_closeRun != null)
            {
                _closeRun.Activate();
                return;
            }

            Desktop desktop = Find(id);
            if (desktop == null) return;

            List<WindowEntry> windows = _closer.Closeable(id);
            if (windows.Count == 0)
            {
                // Emptied between the menu and the click. Removing an empty desktop never
                // asks, so neither does this.
                if (_service.Count > 1) _service.Remove(id);
                return;
            }

            string body = (windows.Count == 1 ? "1 window" : windows.Count + " windows")
                + " will be asked to close, as if you clicked each one's X. Apps with unsaved "
                + "work will ask to save first. The desktop is removed once it is empty.";

            if (Ask("Close all windows and remove \"" + desktop.DisplayName + "\"?", body,
                    "Close and remove", "Cancel") != 0) return;

            // Everything again, now that the user has answered. The dialog pumps messages
            // while it is up, so the world moved on underneath it: the desktop may have been
            // removed from Task View or by a middle click - which hands its windows to a
            // neighbour, where the list taken above would now close them on a desktop
            // nobody chose to clear - or a window may have been moved off it, or opened on
            // it. Only what is on the desktop now is closed.
            desktop = Find(id);
            if (desktop == null)
            {
                Log.Write("controller: close-all target removed while confirming, nothing closed");
                return;
            }

            _closer.Invalidate();
            windows = _closer.Closeable(id);
            if (windows.Count == 0)
            {
                if (_service.Count > 1) _service.Remove(id);
                return;
            }

            var run = new CloseAllRun(_service, _closer, desktop, windows,
                                      delegate(WindowEntry w) { return Ellipsize(Describe(w), MenuTextMax); });
            run.Ended += delegate
            {
                // Not current means shutdown let go of it first: the strip is about to be
                // torn down, and a sweep to repaint it would only slow the exit.
                if (_closeRun != run) return;
                _closeRun = null;
                _inventory.MarkChanged();
                RefreshOccupancy();
            };
            _closeRun = run;

            Rectangle anchor = _strip != null ? _strip.ScreenBounds() : Rectangle.Empty;
            run.Start(this, _background, _host.DpiScale, anchor);
        }

        /// <summary>
        /// Moves a desktop one place left or right. The position is looked up again from
        /// the GUID the menu was built with, not carried from it, so a desktop added or
        /// removed while the menu was open moves the desktop the item named.
        /// </summary>
        void RequestReorder(Guid id, int delta)
        {
            Desktop desktop = Find(id);
            if (desktop == null) return;

            Reorder(desktop.Index, desktop.Index + delta, "menu");
        }

        /// <summary>
        /// The one way into a reorder, for the menu's Move left and right and for a drag on
        /// the strip. Refused while a close-all runs, for the reason the menu greys its items
        /// out: that run holds its desktop by GUID, and a reorder can give it a new one. A
        /// refused drag needs nothing more from here - the strip eases its buttons back by
        /// itself when no new order arrives.
        /// </summary>
        void Reorder(int from, int to, string how)
        {
            if (_closeRun != null)
            {
                Log.Write("controller: " + how + " reorder refused, a close-all is running");
                return;
            }
            if (from == to || from < 0 || to < 0 || from >= _service.Count || to >= _service.Count) return;

            Log.Write(delegate { return "controller: " + how + " moving desktop " + (from + 1) + " to " + (to + 1); });

            // Pruning held off for the length of it - see MarkSync.BeginReorder.
            _markSync.BeginReorder();
            try
            {
                _service.Reorder(from, to);
            }
            finally
            {
                _markSync.EndReorder();
            }
            SyncMarks();

            // The recreated desktops are new GUIDs, so whatever the inventory holds per
            // desktop is keyed to ones that no longer exist.
            _inventory.MarkChanged();
            RefreshOccupancy();
        }

        Desktop Find(Guid id)
        {
            IList<Desktop> desktops = _service.Desktops;
            for (int i = 0; i < desktops.Count; i++)
                if (desktops[i].Id == id) return desktops[i];
            return null;
        }

        /// <summary>
        /// The desktop's name, editable in place - Task View's rename, in the menu.
        ///
        /// A row rather than a dialog: showing the menu already took the foreground, so the
        /// box can simply take the caret, and there is no second window to place, to dismiss,
        /// or to keep out of ForegroundTracker's way. Enter commits, Escape closes the menu
        /// and changes nothing, which is what a drop-down does anyway.
        ///
        /// Adds nothing at all when the shell cannot rename, leaving the menu to open on
        /// Switch as it did before.
        /// </summary>
        void AddRenameBox(ContextMenuStrip menu, Desktop desktop)
        {
            if (!_service.CanRename) return;

            Guid id = desktop.Id;

            // What the desktop is called right now, including the "Desktop 3" a nameless
            // one falls back to: an empty box says nothing about what is being renamed, and
            // it is selected on open, so typing replaces it either way. Task View shows the
            // name the same way and for the same reason.
            //
            // Whether that text is a real name matters on the way back out, so it is
            // settled here rather than guessed at from the string later.
            bool named = !string.IsNullOrEmpty(desktop.Name);
            string shown = desktop.DisplayName;

            var box = new ToolStripTextBox();
            box.Text = shown;
            box.ToolTipText = "Click to rename - Enter to save";

            // The hosted control, not the item wrapper: focus and the caret belong to the
            // real TextBox, and these are the events that actually report them.
            TextBox field = box.TextBox;

            // Selecting from the focus event does not survive the click that caused it -
            // the caret is placed on mouse-up, after focus has already moved - so the
            // select is deferred to that mouse-up.
            bool selectPending = false;

            field.Enter += delegate
            {
                selectPending = true;
                if (_menuTheme != null) _menuTheme.ApplyEditing(box);
            };

            field.MouseUp += delegate
            {
                if (!selectPending) return;
                selectPending = false;
                box.SelectAll();
            };

            field.Leave += delegate
            {
                selectPending = false;

                // Clicking away is not a save. Enter is the only thing that commits, so
                // anything half-typed goes back to the name the desktop still has.
                box.Text = shown;
                if (_menuTheme != null) _menuTheme.ApplyIdle(box);
            };

            box.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode != Keys.Enter) return;

                // A menu has nowhere for Enter to go, and unswallowed it just beeps.
                e.SuppressKeyPress = true;
                e.Handled = true;

                // Handing the fallback straight back means "still no name", not a desktop
                // literally called "Desktop 3" - which would look identical today and then
                // stay behind when a removal renumbers everything after it. Only ever
                // applied to a desktop that had no name to begin with, or opening the menu
                // on "Comms" and pressing Enter would erase it.
                string typed = box.Text.Trim();
                if (!named && typed == shown) typed = "";

                _service.Rename(id, typed);

                // Closed is what hands the button back to the strip, so this is also what
                // puts the highlight out.
                menu.Close();
            };

            menu.Items.Add(box);
            menu.Items.Add(new ToolStripSeparator());

            // Deliberately not focused when the menu opens. A caret sitting in a row nobody
            // asked to edit reads as the menu having put you somewhere, and it takes Enter
            // and the arrow keys away from the rows below, which are what the menu is for.
            // Clicking the name is what starts a rename - the same as Task View.
        }

        /// <summary>Longest window description a row will carry before it is cut short.</summary>
        const int MenuTextMax = 40;

        string SendCaption(IntPtr target)
        {
            if (target == IntPtr.Zero) return "Send window here";

            string what = Describe(WindowInventory.Describe(target));
            return "Send \"" + Ellipsize(what, MenuTextMax) + "\" here";
        }

        string RemoveCaption(Desktop desktop)
        {
            int count = _inventory.WindowsOn(desktop.Id).Count;

            string what;
            if (count == 0) what = "empty";
            else if (count == 1) what = "1 window";
            else what = count + " windows";

            return "Remove " + Ellipsize(desktop.DisplayName, MenuTextMax) + " (" + what + ")";
        }

        /// <summary>
        /// True when the window is already where the menu would send it. An unanswerable
        /// question - the shell mid-restart - leaves the item live: offering a move that
        /// turns out to be a no-op is a smaller failure than greying out one that would work.
        /// </summary>
        bool IsOn(IntPtr hwnd, Guid desktop)
        {
            try
            {
                Guid actual;
                if (!_api.TryGetWindowDesktop(hwnd, out actual)) return false;
                return actual == desktop;
            }
            catch (Exception ex)
            {
                Log.Write("controller: desktop of window unknown - " + ex.Message);
                return false;
            }
        }

        static string Ellipsize(string text, int max)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= max) return text;
            // The cut can land on an ellipsis the text already had - VS Code shortens its
            // own titles to "App stress testing and b\u2026" - and "..." after it read as
            // "b......" in the menu. So whatever trails is trimmed first.
            return text.Substring(0, max - 1).TrimEnd(' ', '.', '-', '\u2026') + "\u2026";
        }

        static void AddItem(ContextMenuStrip menu, string text, bool enabled, EventHandler click)
        {
            // Ampersands in the caption come out of window titles, where they are literal;
            // doubled, or the menu reads one as a mnemonic and swallows it.
            var item = new ToolStripMenuItem(text.Replace("&", "&&"));
            item.Enabled = enabled;

            // A disabled item cannot be clicked, and leaving the handler off means an action
            // that became impossible between building and clicking cannot fire either.
            if (enabled) item.Click += click;

            menu.Items.Add(item);
        }

        /// <summary>
        /// Closes and forgets the open menu, telling the strip either way - a menu that
        /// vanishes without a word leaves its button lit for good.
        /// </summary>
        void CloseButtonMenu()
        {
            ContextMenuStrip menu = _buttonMenu;
            _buttonMenu = null;

            if (menu != null)
            {
                menu.Close();
                menu.Dispose();
            }

            if (_strip != null) _strip.MenuClosed();
        }

        // --- marks ------------------------------------------------------------

        /// <summary>
        /// A dialog is opening over the strip: the hover panel stays away until the last one
        /// closes. Counted, because the strip keeps taking clicks under a modal dialog and a
        /// second can open over the first - a Remove confirmation over the mark picker - and a
        /// plain flag set back to false by the inner one let the panel open over the outer.
        /// </summary>
        void BeginAsking()
        {
            _asking++;
            if (_strip != null) _strip.Asking = true;
        }

        void EndAsking()
        {
            if (_asking > 0) _asking--;

            // The strip can have been rebuilt while the dialog was up - a desktop added or
            // removed underneath it - so the field is read again.
            if (_strip != null) _strip.Asking = _asking > 0;
        }

        /// <summary>What the strip draws for a desktop: the picker's preview while it is open, else the saved mark.</summary>
        DesktopMark MarkFor(Guid id)
        {
            if (_previewing && id == _pickerId) return _preview;
            return _marks.Get(id);
        }

        int MarkedCount()
        {
            if (_service == null) return 0;
            int n = 0;
            foreach (Desktop d in _service.Desktops)
                if (MarkFor(d.Id) != null) n++;
            return n;
        }

        /// <summary>
        /// The marks brought into line with the desktops there are - see MarkSync.Sync. True
        /// when a live desktop's mark changed and the strip wants telling.
        /// </summary>
        bool SyncMarks()
        {
            if (_service == null) return false;
            return _markSync.Sync(_service.Desktops);
        }

        /// <summary>A reorder recreated a desktop: its mark, and an open picker, follow it to the new GUID.</summary>
        void OnRecreated(Guid oldId, Guid newId)
        {
            _markSync.Recreated(oldId, newId);
            if (_pickerId == oldId) _pickerId = newId;
        }

        /// <summary>The marks changed: the strip re-reads them, and is re-placed if that changed its width.</summary>
        void ApplyMarks()
        {
            if (_strip == null) return;
            if (_strip.RefreshMarks()) PlaceStrip();
        }

        /// <summary>
        /// The menu's "Set mark...": the picker over the desktop's button, showing each change
        /// on the strip as it is made, and keeping it only on OK.
        ///
        /// The desktop is followed by GUID through _pickerId, which OnRecreated moves along if
        /// a drag reorders the row while the picker is up - the strip is a child of the taskbar,
        /// so a modal dialog on this form does not stop it taking a drag.
        /// </summary>
        void RequestMark(Guid id)
        {
            // One picker at a time. The strip still takes clicks while one is up - it is a
            // child of the taskbar, not of the form the dialog is modal over - and a second
            // "Set mark..." used to open a second picker over the first. Both previewed
            // through the one _pickerId, which the second overwrote, so OK in the first then
            // saved its mark onto the second desktop. The menu greys the item while a picker
            // is open; this is the guard behind it.
            if (_picker != null)
            {
                _picker.Activate();
                return;
            }

            Desktop desktop = Find(id);
            if (desktop == null || _strip == null) return;

            Rectangle anchor = _strip.AnchorFor(desktop.Index);
            _pickerId = id;
            _preview = _marks.Get(id);

            DialogResult result;
            DesktopMark chosen;
            using (var picker = new MarkPicker(desktop.DisplayName, _preview, _marks.Recent,
                                               _background, _config.HighlightColor, _host.DpiScale, anchor))
            {
                _picker = picker;
                picker.Previewed += delegate(DesktopMark mark)
                {
                    _preview = mark;
                    _previewing = true;
                    ApplyMarks();
                };

                // The hover panel would otherwise open over the picker; see SwitcherStrip.Asking.
                BeginAsking();
                try
                {
                    result = picker.ShowDialog(this);
                }
                finally
                {
                    _previewing = false;
                    _picker = null;
                    EndAsking();
                }
                chosen = picker.Mark;
            }

            if (result == DialogResult.OK)
            {
                desktop = Find(_pickerId);
                if (desktop != null && _markSync.Set(_pickerId, chosen, desktop.Index))
                    Log.Write(delegate { return "controller: desktop " + desktop.Number + " mark " + (chosen == null ? "cleared" : chosen.Format()); });
            }

            // Either way: OK shows the saved mark, Cancel takes the preview back off.
            ApplyMarks();
        }

        // --- tooltips ---------------------------------------------------------

        /// <summary>
        /// Builds what a button's tooltip should say. Assembled here because this is the
        /// only type holding the desktop model and the window inventory at once; the
        /// strip contributes geometry and nothing else.
        /// </summary>
        TooltipContent BuildTooltip(int index)
        {
            if (!_config.Tooltips || _service == null) return null;

            IList<Desktop> desktops = _service.Desktops;

            // Before '+', whose test would otherwise claim it. Only the hint: the figures
            // are shown by a click, never by a hover.
            if (_strip != null && index == _strip.StatsIndex)
            {
                // The copy gesture's confirmation, when the panel is closed: the click hid the
                // hint and the hover brings it back a moment later, saying this instead.
                if (_config.StatsPublicIp && RecentlyCopied())
                    return new TooltipContent("Copied " + _publicIp.Address, new List<string>
                    {
                        "Your public IP is on the clipboard",
                    });

                // The gesture says itself, or nobody would find it: nothing else on the tile
                // hints that a modified click does anything.
                TooltipContent hint = StatsHint(_config.StatsStyle);
                string gesture = _config.StatsPublicIp ? GestureName(_config.StatsCopyGesture) : null;
                if (gesture != null) hint.Lines.Add(gesture + " to copy your public IP");
                string keepGesture = GestureName(_config.StatsKeepGesture);
                if (keepGesture != null) hint.Lines.Add(keepGesture + " to keep it open");
                string taskGesture = GestureName(_config.StatsTaskManagerGesture);
                if (taskGesture != null) hint.Lines.Add(taskGesture + " for Task Manager");
                return hint;
            }

            if (index >= desktops.Count)
                return new TooltipContent("New desktop", new List<string> { "Win+Ctrl+D" });

            if (index < 0) return null;
            Desktop desktop = desktops[index];

            string title = desktop.DisplayName;
            if (desktop.IsCurrent) title += "   (current)";

            IList<WindowEntry> entries;
            int total;
            var content = new TooltipContent(title, WindowLines(desktop, out entries, out total));
            content.Windows = entries;
            content.Note = WindowCount(total);
            return content;
        }

        /// <summary>
        /// The modern panel's "5 windows", or null for none - the "- empty -" row already
        /// says so. Public for the --strip harness, so its panels say what the real ones do.
        /// </summary>
        public static string WindowCount(int total)
        {
            if (total == 0) return null;
            return total == 1 ? "1 window" : total + " windows";
        }

        /// <summary>
        /// Bullet prefix for window rows, indenting them under the desktop name.
        ///
        /// Written as an escape so the source stays ASCII: the in-box compiler reads
        /// BOM-less files in the system codepage, and a literal bullet byte would come
        /// through as mojibake.
        /// </summary>
        const string Bullet = "\u2022  ";

        /// <summary>
        /// Between the app and what it is showing. An em dash rather than a hyphen because
        /// window titles are full of hyphens already; escaped for the same reason as Bullet.
        /// </summary>
        const string Sep = "  \u2014  ";

        /// <summary>
        /// A desktop's rows as text, with the window behind each one beside it in
        /// <paramref name="entries"/> - null for a row that is not a window - and every
        /// window on the desktop counted in <paramref name="total"/>, listed or not.
        /// </summary>
        IList<string> WindowLines(Desktop desktop, out IList<WindowEntry> entries, out int total)
        {
            var lines = new List<string>();
            var behind = new List<WindowEntry>();
            entries = behind;
            IList<WindowEntry> windows = _inventory.WindowsOn(desktop.Id);
            total = windows.Count;

            // An empty desktop is worth saying out loud: it is the one you can remove
            // without losing anything, and otherwise you would have to visit it to find out.
            if (windows.Count == 0)
            {
                lines.Add("- empty -");
                behind.Add(null);
                return lines;
            }

            // Only real windows get a bullet. The overflow line is a count, not an entry.
            int max = _config.TooltipMaxWindows;
            for (int i = 0; i < windows.Count && i < max; i++)
            {
                lines.Add(WindowLine(windows[i]));
                behind.Add(windows[i]);
            }

            if (windows.Count > max)
            {
                lines.Add("+" + (windows.Count - max) + " more");
                behind.Add(null);
            }

            return lines;
        }

        /// <summary>
        /// One window's row, bullet and all. Public so the --strip harness renders exactly
        /// what the real tooltip renders instead of drifting from it.
        /// </summary>
        public static string WindowLine(WindowEntry window)
        {
            return Bullet + Describe(window);
        }

        /// <summary>
        /// App first, then the title. That order is the whole point: scanning a desktop is
        /// asking which apps are over there, and it is also the end the ellipsis never eats.
        /// </summary>
        static string Describe(WindowEntry window)
        {
            if (window.App.Length == 0) return window.Title;
            if (window.Title.Length == 0 || window.Title == window.App) return window.App;

            return window.App + Sep + window.Title;
        }

        // --- stats panel ------------------------------------------------------

        /// <summary>
        /// The stats tile's panel. Takes a fresh reading first - SystemStats skips it when
        /// the last was too recent to measure a rate over - so each once-a-second refresh
        /// shows the second that just went by. Not gated on the tooltips setting: the tile
        /// has no purpose but this panel, and turning the tile off is its own setting.
        /// </summary>
        TooltipContent BuildStats(bool kept)
        {
            _stats.Sample();
            PushStatsLevels();

            // Once a second while the panel is open, and a no-op unless the address is
            // stale; the answer shows on the refresh after it lands.
            if (_config.StatsPublicIp) _publicIp.Refresh(false);
            return StatsContent(_stats, kept, _config.StatsPublicIp ? IpCorner() : null);
        }

        /// <summary>A tile gesture as the hint and the menu spell it, or null for none.</summary>
        static string GestureName(TileGesture gesture)
        {
            switch (gesture)
            {
                case TileGesture.CtrlClick:   return "Ctrl+click";
                case TileGesture.ShiftClick:  return "Shift+click";
                case TileGesture.AltClick:    return "Alt+click";
                case TileGesture.MiddleClick: return "Middle click";
                case TileGesture.ShiftRightClick: return "Shift+right-click";
                default:                      return null;
            }
        }

        /// <summary>How long "Copied" stands in for the label after a copy.</summary>
        const int CopiedShowMs = 2000;

        /// <summary>
        /// The address onto the clipboard, from the menu item or the tile's copy gesture.
        /// With nothing known yet there is nothing to copy; the gesture asks for one instead,
        /// so a second try a moment later works. Either way the panel or the tile's hint says
        /// what happened - a gesture that copies with no sign of it reads as one that did
        /// nothing.
        /// </summary>
        void CopyPublicIp()
        {
            string address = _publicIp.Address;
            if (address == null)
            {
                _publicIp.Refresh(false);
                return;
            }

            CopyText(address);
            _ipCopied = true;
            _ipCopiedAt = Environment.TickCount;
        }

        bool RecentlyCopied()
        {
            if (!_ipCopied) return false;
            int age = unchecked(Environment.TickCount - _ipCopiedAt);
            return age >= 0 && age < CopiedShowMs;
        }

        /// <summary>The panel's top-right: the address, or "copied" for a moment after a copy.</summary>
        string IpCorner()
        {
            return (RecentlyCopied() ? "Copied   " : "Public IP   ") + _publicIp.Display;
        }

        /// <summary>
        /// Puts text on the clipboard. Another process can be holding the clipboard open -
        /// clipboard managers and remote desktop do, briefly - and SetText throws for it;
        /// WinForms already retries a few times, so a failure here is logged and dropped.
        /// </summary>
        static void CopyText(string text)
        {
            try
            {
                Clipboard.SetText(text);
            }
            catch (Exception ex)
            {
                Log.Write("controller: clipboard busy - " + ex.Message);
            }
        }

        /// <summary>
        /// The stats tile's hover tooltip: what the two meters are and what clicking does, in
        /// the same shape as '+' gives "New desktop" and its shortcut. The meters' letters are
        /// one character each, so this spells them out - and says what amber and red mean,
        /// which nothing on the strip can. The other styles have no letters, so for them
        /// the first line says what drives the mark instead. Public for the --strip
        /// harness.
        /// </summary>
        public static TooltipContent StatsHint(StatsTileStyle style)
        {
            string first;
            switch (style)
            {
                case StatsTileStyle.Gauge: first = "The needle is CPU - amber and red mean heavy load"; break;
                case StatsTileStyle.Rings: first = "Outer ring is CPU, inner is memory - amber and red mean heavy load"; break;
                case StatsTileStyle.Chip:
                case StatsTileStyle.Monitor:
                case StatsTileStyle.Fan:   first = "Amber and red mean heavy CPU or memory load"; break;
                default:                   first = "C is CPU, M is memory - amber and red mean heavy load"; break;
            }

            return new TooltipContent("System", new List<string>
            {
                first,
                "Click for CPU, memory and network",
            });
        }

        /// <summary>
        /// The panel: three tiles, CPU, memory and network, each with a chart of the last
        /// minute along the bottom - what a single figure refreshed once a second cannot
        /// show is the spike that has just passed. CPU and memory are percentages and get a
        /// ring gauge; network has no top, so it gets its two figures in the ring's place.
        ///
        /// It used to be three text rows in tab-stop columns, then the same rows with a
        /// boxed strip of graph beside each. The strips read as progress bars rather than
        /// graphs, and were too short to show a shape, so the figures and their history
        /// went into tiles where the chart can run the tile's full width.
        ///
        /// No lines, only tiles. Public and static so --stats and the --strip harness print
        /// and draw exactly what the real panel does.
        /// </summary>
        public static TooltipContent StatsContent(SystemStats stats, bool kept)
        {
            return StatsContent(stats, kept, null);
        }

        /// <summary>
        /// The same, with corner - the public IP - at the title's right. Null when
        /// statsPublicIp is off, and from the harnesses, which ask nobody.
        /// </summary>
        public static TooltipContent StatsContent(SystemStats stats, bool kept, string corner)
        {
            int now = Environment.TickCount;
            IList<StatsPoint> history = stats.History;

            var cpu = new StatsTile();
            cpu.Label = "CPU";
            cpu.HasGauge = true;
            cpu.Gauge = stats.CpuPercent < 0 ? -1f : stats.CpuPercent / 100f;
            cpu.Figure = Percent(stats.CpuPercent);
            cpu.Detail = "peak " + Percent(Peak(history, CpuOf));
            cpu.Primary = Series(history, now, CpuOf, 100.0);

            // The figure is the load because that is what the ring shows; the gigabytes are
            // the detail, which is the answer to "how much is that".
            var memory = new StatsTile();
            memory.Label = "Memory";
            memory.HasGauge = true;
            memory.Gauge = stats.HasMemory ? stats.MemoryLoad / 100f : -1f;
            memory.Figure = stats.HasMemory ? stats.MemoryLoad + "%" : Unknown;
            memory.Detail = stats.HasMemory
                ? Gigabytes(stats.MemoryUsed) + " / " + Gigabytes(stats.MemoryTotal) + " GB"
                : Unknown;
            memory.Primary = Series(history, now, MemoryOf, 100.0);

            // Download is the filled shape, upload the line over it: down is the one people
            // usually mean, and is usually the larger.
            double scale = NetworkScale(history);
            var network = new StatsTile();
            network.Label = "Network";
            network.Figure = Down + Rate(stats.DownBytesPerSecond);
            network.Figure2 = Up + Rate(stats.UpBytesPerSecond);

            // The widest Rate can produce short of 10 GB/s: four digits and a unit - up to
            // 1023 KB/s, and from 1000 MB/s. Digits in Segoe UI are all one width, so 8s
            // stand for any of them.
            network.FigureTemplate = "8888 KB/s";
            // Both peaks, each with its arrow: a bare "peak 4.5 MB/s" under two figures left
            // it a guess which direction it was.
            network.Detail = "peak " + RatePair(Peak(history, DownOf), Peak(history, UpOf));
            network.Primary = Series(history, now, DownOf, scale);
            network.Secondary = Series(history, now, UpOf, scale);

            // Says so the same way a desktop's panel says "(current)". Only when kept: a
            // popup behaves the way a popup is expected to, but a panel that sits through
            // clicks elsewhere would otherwise look stuck.
            var content = new TooltipContent(kept ? "System   (kept open)" : "System", null,
                                             new List<StatsTile> { cpu, memory, network });
            content.Corner = corner;
            return content;
        }

        /// <summary>The highest known value in the history, -1 when there is none.</summary>
        static float Peak(IList<StatsPoint> history, Func<StatsPoint, double> value)
        {
            double peak = -1;
            foreach (StatsPoint p in history)
                peak = Math.Max(peak, value(p));
            return (float)peak;
        }

        /// <summary>
        /// The floor under the network chart's scale, in bytes a second. The scale is
        /// otherwise the busiest moment in the window, down or up, so a burst fills the
        /// graph whatever the link speed - a fixed scale would sit flat on a slow link or
        /// pinned on a fast one. Without the floor an idle machine's background trickle,
        /// a few KB/s, would be scaled up to fill it too and look like real traffic.
        /// </summary>
        public const double NetworkFloor = 64 * 1024;

        /// <summary>
        /// What the network chart's top stands for over the current window. Public so
        /// --stats can print it beside the figures it was computed from.
        /// </summary>
        public static double NetworkScale(IList<StatsPoint> history)
        {
            double top = NetworkFloor;
            foreach (StatsPoint p in history)
            {
                if (p.Down > top) top = p.Down;
                if (p.Up > top) top = p.Up;
            }
            return top;
        }

        static double CpuOf(StatsPoint p) { return p.Cpu; }
        static double MemoryOf(StatsPoint p) { return p.Memory; }
        static double DownOf(StatsPoint p) { return p.Down; }
        static double UpOf(StatsPoint p) { return p.Up; }

        /// <summary>
        /// One series of the history, normalised for a StatsTile chart: x by age across
        /// HistoryMs, so readings two seconds apart and one second apart land where they
        /// were actually taken, and y as a share of <paramref name="top"/>. Unknown values
        /// - negative - are left out rather than drawn as zero.
        /// </summary>
        static List<PointF> Series(IList<StatsPoint> history, int now,
                                   Func<StatsPoint, double> value, double top)
        {
            var points = new List<PointF>();
            foreach (StatsPoint p in history)
            {
                double v = value(p);
                if (v < 0 || top <= 0) continue;

                int age = unchecked(now - p.At);
                float x = 1f - (float)age / SystemStats.HistoryMs;
                points.Add(new PointF(x, (float)(v / top)));
            }
            return points;
        }

        /// <summary>
        /// A figure that has no second reading to measure from yet. An ellipsis rather than
        /// 0, which would be a claim: "no traffic" and "not measured" are different answers.
        /// Escaped to keep the source ASCII, like Bullet.
        /// </summary>
        const string Unknown = "\u2026";

        // Arrows rather than "down"/"up": they fit a column the width of a short number and
        // read at a glance. Escaped for the same reason as Bullet.
        const string Down = "\u2193 ";
        const string Up = "\u2191 ";

        static string Percent(float value)
        {
            if (value < 0) return Unknown;
            return ((int)Math.Round(value)) + "%";
        }

        static string Gigabytes(ulong bytes)
        {
            return (bytes / (1024.0 * 1024 * 1024)).ToString("0.0");
        }

        /// <summary>
        /// Bytes a second, not bits: what a browser shows a download at, which is the
        /// number this is most often compared to. Task Manager uses bits, so its figure is
        /// eight times this one. One decimal only below 10 MB/s, where it is a real digit.
        /// </summary>
        static string Rate(double bytesPerSecond)
        {
            if (bytesPerSecond < 0) return Unknown;

            const double KB = 1024, MB = 1024 * 1024;
            if (bytesPerSecond < MB) return (bytesPerSecond / KB).ToString("0") + " KB/s";
            if (bytesPerSecond < 10 * MB) return (bytesPerSecond / MB).ToString("0.0") + " MB/s";
            return (bytesPerSecond / MB).ToString("0") + " MB/s";
        }

        /// <summary>
        /// Download and upload rates in one unit, "↓ 2.8 ↑ 4.5 MB/s", for a line too
        /// narrow for two Rate()s side by side. The unit is the larger figure's, so the
        /// smaller can read as a fraction - "↓ 0.3 ↑ 4.5 MB/s" - which is still the
        /// right number, and one unit is what makes the pair fit at all.
        ///
        /// Never more than three digits a figure. Rate() goes to four - "1023 KB/s" - and
        /// two of those with "peak" measured 125px against the 117 a tile's detail line has
        /// at the default width, so the pair moves to MB from 1000 KB/s rather than 1024.
        /// </summary>
        static string RatePair(double down, double up)
        {
            if (down < 0 && up < 0) return Unknown;

            const double KB = 1024, MB = 1024 * 1024;
            double top = Math.Max(down, up);

            // 999.5 so a figure that would round up to 1000 still changes unit.
            if (top < 999.5 * KB)
                return Down + Figure(down, KB, "0") + " " + Up + Figure(up, KB, "0") + " KB/s";

            string format = top < 9.95 * MB ? "0.0" : "0";
            return Down + Figure(down, MB, format) + " " + Up + Figure(up, MB, format) + " MB/s";
        }

        static string Figure(double bytesPerSecond, double unit, string format)
        {
            return bytesPerSecond < 0 ? Unknown : (bytesPerSecond / unit).ToString(format);
        }

        static void OpenTaskManager()
        {
            // Through the shell, which honours taskmgr's auto-elevate manifest. The one
            // failure worth expecting is the user declining a UAC prompt.
            try
            {
                System.Diagnostics.Process.Start("taskmgr.exe");
            }
            catch (Exception ex)
            {
                Log.Write("controller: could not open Task Manager - " + ex.Message);
            }
        }

        // --- service events ---------------------------------------------------

        void OnDesktopsChanged(object sender, EventArgs e)
        {
            // Desktops came or went, so the cached sweep is describing a stale set.
            _inventory.Invalidate();

            // And so is anything the menu is offering: a removal renumbers every desktop
            // after it, so "Remove Desktop 3" is now pointing at what used to be 4. The
            // items hold Guids and would fail harmlessly, but only after saying otherwise.
            CloseButtonMenu();

            if (_strip == null)
            {
                RebuildStrip();
                return;
            }

            // Before the strip lays itself out: a removed desktop's mark is let go now, and a
            // reorder's were carried already, so the row it reads is the row it draws.
            SyncMarks();

            _strip.SetDesktops(_service.Desktops);
            PlaceStrip();
            RefreshOccupancy();

            QueueSave(_service.Count);
        }

        void OnCurrentChanged(object sender, EventArgs e)
        {
            if (_strip != null) _strip.SetDesktops(_service.Desktops);
        }

        /// <summary>
        /// A desktop was renamed. The hover panel and the menu captions read the service
        /// model directly and are built on demand, so both are already right by the time
        /// this runs; what it is for is the strip's own copy of the list, which nothing
        /// else would correct until the set or the current desktop next moved.
        ///
        /// Safe while the menu is open - which is exactly when a rename commits - because
        /// SetDesktops keeps the pinned button lit when the count has not changed.
        /// </summary>
        void OnNamesChanged(object sender, EventArgs e)
        {
            if (_strip != null) _strip.SetDesktops(_service.Desktops);
        }

        // --- watchdog ---------------------------------------------------------

        /// <summary>
        /// Catches what notifications cannot: the taskbar being replaced, the strip
        /// being destroyed with it, and task buttons drifting over the strip.
        /// </summary>
        void Watchdog()
        {
            if (!_host.IsHealthy())
            {
                Log.Write("controller: taskbar handle stale - Explorer restarted");
                HandleExplorerRestart();
                return;
            }

            if (_strip == null || !_host.IsAttached(_strip.Handle))
            {
                if (!_host.HasAnchor) return;   // taskbar still settling
                Log.Write("controller: strip missing - rebuilding");
                RebuildStrip();
                return;
            }

            // Cheap, and keeps the strip above sibling task buttons.
            PlaceStrip();

            // The hooks are the fast path and this is the slow one behind them: it remakes
            // them after an Explorer restart or a tile arriving under a new process, and picks
            // up a focus change the hook dropped - a window that took the foreground before
            // it had a caption fails the alt-tab test then and passes it now.
            WatchTray();
            WatchTile();
            _foreground.Sample();
        }

        void HandleExplorerRestart()
        {
            DisposeStrip();

            if (!_host.Locate() || !_host.HasAnchor)
            {
                // Explorer creates Shell_TrayWnd before TrayNotifyWnd. Rebuilding now
                // would anchor the strip to the taskbar edge and park it right of the
                // clock; the watchdog retries in a second.
                Log.Write("controller: taskbar not fully back yet, will retry");
                return;
            }

            _api.Drop();
            _service.InvalidateNotifications();
            _service.Tick();

            // Which window is on which desktop is Explorer's state, and this is a new Explorer.
            // The occupancy answer is otherwise reused until a window event says it moved, and
            // a restart says nothing of the kind - so it is swept again on the rebuild below.
            _inventory.Invalidate();

            RebuildStrip();   // re-scales and re-samples on its own, after the strip is gone

            Log.Write("controller: recovered from Explorer restart");
        }

        protected override void WndProc(ref Message m)
        {
            // Another copy (the installer, or --quit) asking this one to exit the way the tray's
            // Exit does. Not gated on _started: being asked to quit during startup is still
            // being asked to quit.
            if (_quitMessage != 0 && (uint)m.Msg == _quitMessage)
            {
                Log.Write("controller: asked to quit");
                Close();
                return;
            }

            if (_started && _taskbarCreatedMessage != 0
                && (uint)m.Msg == _taskbarCreatedMessage)
            {
                Log.Write("controller: TaskbarCreated received");
                HandleExplorerRestart();
            }
            else if (_started && m.Msg == Native.WM_HOTKEY)
            {
                OnHotkey((int)m.WParam);
                return;
            }
            else if (_started && m.Msg == 0x007E) // WM_DISPLAYCHANGE
            {
                Log.Write("controller: display changed");
                if (_host.Locate()) QueueSettledRebuild("display changed");
            }
            else if (_started && m.Msg == WM_SETTINGCHANGE && IsImmersiveColorSet(ref m))
            {
                QueueSettledRebuild("colour set changed");
            }
            else if (_started && m.Msg == WM_POWERBROADCAST
                     && (int)m.WParam == PBT_APMRESUMEAUTOMATIC)
            {
                QueueSettledRebuild("resumed from sleep");
            }
            else if (_started && m.Msg == WM_WTSSESSION_CHANGE)
            {
                int change = (int)m.WParam;
                if (change == WTS_SESSION_LOCK)
                {
                    Pause();
                }
                else if (change == WTS_SESSION_UNLOCK)
                {
                    Resume("session unlocked");
                    QueueSettledRebuild("session unlocked");
                }
                else if (change == WTS_CONSOLE_CONNECT || change == WTS_REMOTE_CONNECT
                         || change == WTS_SESSION_LOGON)
                {
                    // Not expected to be what ends a lock, but a model frozen because one
                    // unlock went unheard would stay frozen for the rest of the session. Any
                    // sign the session is in use again resumes; resuming twice is harmless.
                    Resume("session reconnected");
                }
            }

            base.WndProc(ref m);
        }

        const int WM_SETTINGCHANGE = 0x001A;

        // Waking is two events, not one, and the strip's colour depends on the second.
        //
        // The resume fires while the lock screen is still covering the taskbar, so the
        // rebuild it triggers cannot sample - TrySampleBackground now refuses, and the strip
        // keeps the colour it had, which is the right answer for a wallpaper that did not
        // change. The unlock is when the taskbar is genuinely back, and its rebuild is the
        // one that can read a real pixel. Handling only the resume was the whole bug;
        // handling only the unlock would leave a wake without a lock screen uncovered.
        //
        // Both are wanted anyway: either may be the one that notices a theme changed while
        // the machine was asleep, and QueueSettledRebuild collapses a burst of them into a
        // single rebuild, so the pair costs nothing.
        //
        // PBT_APMRESUMEAUTOMATIC and not PBT_APMRESUMESUSPEND: the automatic one is sent for
        // every resume, where the suspend one is sent only when the wake was user-initiated
        // and is skipped entirely when the machine woke on a timer or the lid.
        const int WM_POWERBROADCAST = 0x0218;
        const int PBT_APMRESUMEAUTOMATIC = 0x0012;

        const int WM_WTSSESSION_CHANGE = 0x02B1;
        const int WTS_CONSOLE_CONNECT = 0x1;
        const int WTS_REMOTE_CONNECT = 0x3;
        const int WTS_SESSION_LOGON = 0x5;
        const int WTS_SESSION_LOCK = 0x7;
        const int WTS_SESSION_UNLOCK = 0x8;

        /// <summary>
        /// Stops the reconcile and watchdog ticks while the session is locked.
        ///
        /// Nothing can change a desktop behind the lock screen and nobody can see the strip,
        /// yet the reconcile tick went on walking every top-level window and asking the shell
        /// about each one every two seconds, all night if the machine stayed locked. The
        /// shell's own notifications are untouched - those cost nothing while nothing happens -
        /// so the model still follows anything that does change, and TaskbarCreated still
        /// rebuilds after an Explorer restart; the watchdog would have been the backup for
        /// both, and Resume runs it at once.
        /// </summary>
        void Pause()
        {
            if (_paused) return;
            _paused = true;
            if (_reconcileTimer != null) _reconcileTimer.Stop();
            if (_watchdogTimer != null) _watchdogTimer.Stop();
            Log.Write("controller: session locked - ticks paused");
        }

        /// <summary>
        /// Restarts the ticks and runs each once now, rather than leaving the model and the
        /// strip's health to wait up to two seconds after the user is back. The stats sample is
        /// not forced: the next tick takes it, and its rate covers the lock either way.
        /// </summary>
        void Resume(string reason)
        {
            if (!_paused) return;
            _paused = false;
            Log.Write("controller: " + reason + " - ticks resumed");

            if (_reconcileTimer != null) _reconcileTimer.Start();
            if (_watchdogTimer != null) _watchdogTimer.Start();

            // Reconcile, not Tick: Tick waits out the 30 s backstop while the sink is live, and
            // a lock is exactly the stretch nobody was watching. One check on the way back costs
            // a few calls and settles it.
            _service.Reconcile();
            RefreshOccupancy();
            Watchdog();
        }

        /// <summary>
        /// Whether this WM_SETTINGCHANGE is the one that means the colours moved.
        ///
        /// The message is broadcast for a great many unrelated things - a policy refresh,
        /// an environment variable, a locale or a work-area change - and rebuilding the
        /// strip on each would thrash it for no reason. Windows sends "ImmersiveColorSet"
        /// for the light/dark switch and for an accent colour change, which are exactly the
        /// two things that move the pixel we sample.
        ///
        /// wParam is checked before lParam is read as a string, and that is not tidiness:
        /// for several other wParam values lParam is a RECT or a flag word, and running
        /// PtrToStringUni over one of those walks memory looking for a null it has no
        /// reason to find. The colour-set broadcast always carries wParam 0.
        /// </summary>
        static bool IsImmersiveColorSet(ref Message m)
        {
            if (m.WParam != IntPtr.Zero || m.LParam == IntPtr.Zero) return false;

            try
            {
                string area = System.Runtime.InteropServices.Marshal.PtrToStringUni(m.LParam);
                return area == "ImmersiveColorSet";
            }
            catch (Exception ex)
            {
                Log.Write("controller: unreadable WM_SETTINGCHANGE lParam - " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Rebuilds the strip a moment after the pixel it samples might have moved, and
        /// only once.
        ///
        /// Covers an accent or light-dark switch, a display change, a resume, and an unlock.
        /// Windows sends several of these in a burst as the change propagates, and the
        /// taskbar has not finished repainting when the first one arrives - sampling then
        /// reads the colour it is on its way out of. One timer, restarted by every message
        /// in the burst, so the work happens once and late enough for the pixel to have
        /// settled.
        ///
        /// The delay is for repaints in progress and nothing more. It used to be asked to
        /// carry the sleep case too, which it could not: the taskbar was not mid-repaint
        /// after a wake, it was underneath the lock screen, and no delay reaches past a
        /// sign-in that might not come for hours. That belongs to the probe's ownership
        /// check in TrySampleBackground, which is what makes firing this on a resume safe
        /// rather than actively harmful.
        /// </summary>
        void QueueSettledRebuild(string reason)
        {
            // The reason has to live outside the closure. The Tick handler is built once, on
            // the first call, so a captured parameter would pin whichever reason happened to
            // come first and every later log line would name it - the burst that actually
            // triggered the rebuild going unrecorded, in the one log written to diagnose it.
            _settleReason = reason;

            if (_settleTimer == null)
            {
                _settleTimer = new Timer();
                _settleTimer.Interval = 600;
                _settleTimer.Tick += delegate
                {
                    _settleTimer.Stop();
                    if (!_started) return;

                    Log.Write("controller: " + _settleReason + " - resampling");
                    RebuildStrip();
                };
            }

            _settleTimer.Stop();
            _settleTimer.Start();
        }

        // --- config persistence -----------------------------------------------

        /// <summary>
        /// Makes <paramref name="next"/> the live config and applies all of it: the things
        /// that live outside the strip here, then a strip rebuild, which is what picks up
        /// the sizes, the colours and whether the context menu is wired at all.
        ///
        /// The one way in for every source of settings - the tray's reload item, a hand edit
        /// ConfigWatcher noticed, and every change made in the Settings window - so none of
        /// them can apply a different subset from the others. It began as the reload item's
        /// alone: editing the file and having to restart is a strange thing to ask of an app
        /// whose whole pitch is that it installs nothing.
        ///
        /// lastCount is not a setting and never comes from <paramref name="next"/>. It is
        /// the desktop count, which only this process knows the current value of - a debounced
        /// save may still be pending, and a Settings window opened before a desktop was added
        /// holds an older count. Taking it from either would quietly roll the count back and
        /// write that out on the next change.
        ///
        /// <paramref name="save"/> is for the window, whose changes exist nowhere else yet.
        /// Something read from the file is only written back when the count had to be
        /// corrected, so a hand edit is not reformatted under the editor that made it.
        /// </summary>
        void ApplyConfig(Config next, string reason, bool save)
        {
            int written = next.LastCount;
            if (_pendingCount >= 1)
            {
                next.LastCount = _pendingCount;
                _pendingCount = -1;
                if (_saveTimer != null) _saveTimer.Stop();
            }
            else
            {
                next.LastCount = _config.LastCount;
            }

            bool keysChanged = next.JumpHotkeys != _config.JumpHotkeys
                               || next.StepHotkeys != _config.StepHotkeys;

            _config = next;
            if (save || _config.LastCount != written) _config.Save();

            // Diagnostics is the one setting that takes effect nowhere near the strip.
            Log.Init(Config.LogPath, _config.Diagnostics);

            if (_reconcileTimer != null) _reconcileTimer.Interval = _config.ReconcileMs;

            // Not a strip setting either: the sampler outlives every strip. A change shows
            // after two readings, the same wait the first figure after login has.
            _stats.UseUtility = _config.StatsCpuUtility;

            // The tray glyph is drawn from highlightColor, and it is the one visible thing
            // a rebuilt strip does not cover - left alone, a changed accent would show up
            // on the strip immediately and in the tray only at the next launch.
            Icon previous = _trayIcon;
            _trayIcon = CreateIcon(_config.HighlightColor);
            if (_tray != null) _tray.Icon = _trayIcon;
            if (previous != null) previous.Dispose();

            if (_started) RebuildStrip();

            // Only when the keys moved. Re-registering is cheap, but a combination another
            // app holds would fail again and bring its balloon back on every unrelated edit.
            // stepWrap needs nothing here - OnHotkey reads it at each press.
            if (_hotkeys != null && keysChanged) RegisterHotkeys();

            // From anywhere but the window itself, the window is now showing stale values.
            if (_settings != null && !save) _settings.ShowConfig(_config);

            Log.Write("controller: settings applied - " + reason);
        }

        // --- shortcuts ------------------------------------------------------------

        /// <summary>
        /// Claims the configured shortcuts, letting go of any held before, and says in a
        /// balloon which could not be had. Said rather than only logged: a key another app
        /// holds simply does nothing when pressed, and with diagnostics off there would be
        /// no trace anywhere of why.
        /// </summary>
        void RegisterHotkeys()
        {
            List<string> failed = _hotkeys.Register(_config.JumpHotkeys, _config.StepHotkeys);
            if (failed.Count == 0 || _tray == null) return;

            string keys = failed.Count <= 3
                ? string.Join(", ", failed.ToArray())
                : failed[0] + " and " + (failed.Count - 1) + " more";
            _tray.ShowBalloonTip(5000, "Shortcut unavailable",
                keys + (failed.Count == 1 ? " is" : " are") + " already used by another app. "
                + "Pick other keys under Settings > Shortcuts.", ToolTipIcon.Warning);
        }

        /// <summary>
        /// A shortcut was pressed: work out the desktop it means and switch there, through
        /// the same call a click on the strip makes, so it lands without the slide.
        ///
        /// The current desktop is asked of the shell, not read from the model. The model
        /// only moves when the switch notification arrives, posted back onto this thread
        /// after the switch; two quick presses of Ctrl+Alt+Right would read the same stale
        /// current twice, and the second would switch to where the first already went. The
        /// shell's answer is current the moment SwitchDesktop returns.
        /// </summary>
        void OnHotkey(int id)
        {
            int jumpIndex, step;
            if (_service == null || !Hotkeys.TryDecode(id, out jumpIndex, out step)) return;

            IList<Desktop> desktops = _service.Desktops;
            int count = desktops.Count;
            if (count == 0) return;

            Guid currentId;
            try
            {
                currentId = _api.GetCurrentId();
            }
            catch (Exception ex)
            {
                // The shell is between lives, an Explorer restart most likely. The model's
                // idea is the best there is, and the switch below will fail quietly too if
                // the shell is really gone.
                Log.Write("hotkeys: current desktop unreadable - " + ex.Message);
                Desktop known = _service.Current;
                if (known == null) return;
                currentId = known.Id;
            }

            int current = -1;
            for (int i = 0; i < count; i++)
                if (desktops[i].Id == currentId) { current = i; break; }

            int target;
            if (step == 0)
            {
                // A digit past the last desktop does nothing rather than making one: creating
                // desktops from a stray keypress is a surprise, and '+' is one click away.
                if (jumpIndex >= count) return;
                target = jumpIndex;
            }
            else
            {
                // The model can lag a desktop created or removed a moment ago, so the shell's
                // current may not be in it. Step from the model's own current then.
                if (current < 0)
                {
                    for (int i = 0; i < count; i++)
                        if (desktops[i].IsCurrent) { current = i; break; }
                    if (current < 0) return;
                }

                target = current + step;
                if (_config.StepWrap) target = (target + count) % count;
                else if (target < 0 || target >= count) return;
            }

            if (target == current) return;

            Log.Write(delegate { return "hotkeys: id " + id + " -> desktop " + (target + 1); });
            _service.SwitchTo(desktops[target].Id);
        }

        // --- settings window ----------------------------------------------------

        /// <summary>
        /// Opens the Settings window, or brings the open one forward. One at a time: two
        /// windows editing two copies would each apply its own, and whichever changed last
        /// would silently undo the other.
        /// </summary>
        void OpenSettings()
        {
            if (_settings != null)
            {
                if (_settings.WindowState == FormWindowState.Minimized)
                    _settings.WindowState = FormWindowState.Normal;
                _settings.Activate();
                return;
            }

            // Centred on the taskbar's monitor, at its scale - the one the strip being
            // configured is on. Before the taskbar has been found, the primary monitor.
            Screen screen = _host.TrayWindow != IntPtr.Zero ? Screen.FromHandle(_host.TrayWindow) : Screen.PrimaryScreen;
            double scale = _host.TrayWindow != IntPtr.Zero ? _host.DpiScale : 1.0;
            Color background = _background.IsEmpty ? Color.FromArgb(0x1F, 0x1F, 0x1F) : _background;

            _settings = new SettingsWindow(_config, background, scale, screen.WorkingArea);
            _settings.Changed += OnSettingsChanged;
            _settings.OpenFileRequested += delegate { OpenInShell(Config.FilePath, true); };
            _settings.FormClosed += delegate
            {
                // Whatever was changed in the last moment before closing still applies.
                FlushSettings();
                _settings.Dispose();
                _settings = null;
                if (_inventory != null) _inventory.MarkChanged();
            };

            _settings.Show();

            // An alt-tab window like any other, so it counts towards its desktop's dot - but
            // it is this process's, and the window hook skips this process, so nothing else
            // would say it came or went. Null only if Settings opens before startup finished.
            if (_inventory != null) _inventory.MarkChanged();
            _settings.Activate();
            Log.Write("controller: settings window opened");
        }

        /// <summary>
        /// The window changed something. Applied after a pause rather than at once: a slider
        /// drag is a change per pixel, and each apply rebuilds the strip - recreating its
        /// window and resampling the taskbar. 150ms is short enough that the strip still
        /// follows the drag, and long enough that it rebuilds a handful of times rather than
        /// a hundred.
        /// </summary>
        void OnSettingsChanged(Config next)
        {
            _settingsPending = next;

            if (_settingsTimer == null)
            {
                _settingsTimer = new Timer();
                _settingsTimer.Interval = 150;
                _settingsTimer.Tick += delegate { FlushSettings(); };
            }
            _settingsTimer.Stop();
            _settingsTimer.Start();
        }

        void FlushSettings()
        {
            if (_settingsTimer != null) _settingsTimer.Stop();
            if (_settingsPending == null) return;

            Config next = _settingsPending;
            _settingsPending = null;
            ApplyConfig(next, "changed in the settings window", true);
        }

        /// <summary>
        /// ConfigWatcher's callback: config.ini was saved by something other than this app.
        ///
        /// A save that straddled the watcher's quiet period is read twice, and the second
        /// read has nothing in it that is not already live; so does a save that changed only
        /// a comment. Applying either would rebuild the strip for nothing - visibly, since a
        /// rebuild resamples and repaints. The tray's reload item deliberately does not ask
        /// this question: it is also how a person tells the strip to look at the taskbar
        /// again.
        /// </summary>
        void OnConfigFileChanged(Config next)
        {
            next.LastCount = _pendingCount >= 1 ? _pendingCount : _config.LastCount;
            if (next.SameAs(_config)) return;

            // Named in the log, so "I saved and nothing happened" can be told apart from
            // "it applied, but not the value I meant".
            var changed = new System.Text.StringBuilder();
            foreach (Setting s in Config.All)
            {
                if (s.Format(next) == s.Format(_config)) continue;
                if (changed.Length > 0) changed.Append(", ");
                changed.Append(s.Key + " " + s.Format(_config) + " -> " + s.Format(next));
            }

            ApplyConfig(next, "config.ini edited: " + (changed.Length > 0 ? changed.ToString() : "unrecognised keys"), false);
        }

        void QueueSave(int count)
        {
            // Back to what is already saved: drop whatever is pending, not just decline to
            // queue more. Returning alone left an excursion's count waiting - three scratch
            // desktops from a selftest, 4 -> 7 -> 4 inside the debounce - so 7 was written,
            // and the next start "restored" three empty desktops nobody wanted.
            if (count == _config.LastCount)
            {
                _pendingCount = -1;
                if (_saveTimer != null) _saveTimer.Stop();
                return;
            }
            _pendingCount = count;

            if (_saveTimer == null) return;
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        void SavePending(object sender, EventArgs e)
        {
            _saveTimer.Stop();
            if (_pendingCount < 1) return;

            _config.LastCount = _pendingCount;
            _pendingCount = -1;
            _config.Save();
            Log.Write("controller: saved lastCount=" + _config.LastCount);
        }

        // --- tray icon --------------------------------------------------------

        void BuildTrayIcon()
        {
            _trayIcon = CreateIcon(_config.HighlightColor);

            var menu = new ContextMenuStrip();

            var autostart = new ToolStripMenuItem("Start with Windows");
            autostart.Checked = IsAutostartEnabled();
            autostart.CheckOnClick = true;
            autostart.CheckedChanged += delegate { SetAutostart(autostart.Checked); };
            menu.Items.Add(autostart);

            menu.Items.Add(new ToolStripSeparator());

            // First in its group and bold, as the tray's default item: it is also what a
            // double-click on the icon opens.
            var settings = new ToolStripMenuItem("Settings...");
            settings.Font = new Font(settings.Font, FontStyle.Bold);
            settings.Click += delegate { OpenSettings(); };
            menu.Items.Add(settings);

            var openConfig = new ToolStripMenuItem("Open config file");
            openConfig.Click += delegate { OpenInShell(Config.FilePath, true); };
            menu.Items.Add(openConfig);

            var openLog = new ToolStripMenuItem("Open log file");
            openLog.Click += delegate { OpenInShell(Config.LogPath, false); };
            menu.Items.Add(openLog);

            // Named for what it does now. It used to say "Reload strip", which was accurate
            // and useless: it rebuilt the strip from the values already in memory, so the
            // config file - the only settings surface this app has - still needed a restart
            // to take effect.
            var reload = new ToolStripMenuItem("Reload settings");
            reload.Click += delegate
            {
                if (!_started) return;
                ApplyConfig(Config.Load(), "reloaded from the tray", false);
            };
            menu.Items.Add(reload);

            menu.Items.Add(new ToolStripSeparator());

            // One or the other, depending on where this copy is running from. Install is
            // here for anyone who answered No at first launch.
            if (Installer.IsInstalledCopy)
            {
                var uninstall = new ToolStripMenuItem("Uninstall...");
                uninstall.Click += delegate { if (Installer.ConfirmUninstall()) Close(); };
                menu.Items.Add(uninstall);
            }
            else
            {
                var install = new ToolStripMenuItem("Install to this PC...");
                install.Click += delegate { if (Installer.Install(true)) Close(); };
                menu.Items.Add(install);
            }

            var exit = new ToolStripMenuItem("Exit");
            exit.Click += delegate { Close(); };
            menu.Items.Add(exit);

            _tray = new NotifyIcon();
            _tray.Icon = _trayIcon;
            _tray.Text = "DesktopSwitcher";
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += delegate { OpenSettings(); };
            _tray.Visible = true;
        }

        /// <summary>Four squares, the first in the accent colour. Drawn, not shipped.</summary>
        static Icon CreateIcon(Color accent)
        {
            using (var bmp = new Bitmap(16, 16))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    using (var on = new SolidBrush(accent))
                        g.FillRectangle(on, 1, 1, 6, 6);
                    using (var off = new SolidBrush(Color.FromArgb(150, 150, 150)))
                    {
                        g.FillRectangle(off, 9, 1, 6, 6);
                        g.FillRectangle(off, 1, 9, 6, 6);
                        g.FillRectangle(off, 9, 9, 6, 6);
                    }
                }

                IntPtr hicon = bmp.GetHicon();
                using (Icon temp = Icon.FromHandle(hicon))
                {
                    // Clone so the icon survives destroying the source handle.
                    Icon owned = (Icon)temp.Clone();
                    Native.DestroyIcon(hicon);
                    return owned;
                }
            }
        }

        static void OpenInShell(string path, bool createIfMissing)
        {
            try
            {
                if (!File.Exists(path))
                {
                    if (!createIfMissing) return;
                    string dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);
                    File.WriteAllText(path, "");
                }
                System.Diagnostics.Process.Start("notepad.exe", path);
            }
            catch (Exception ex)
            {
                Log.Write("controller: could not open " + path + " - " + ex.Message);
            }
        }

        // --- autostart --------------------------------------------------------

        internal static bool IsAutostartEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey))
                {
                    if (key == null) return false;
                    return key.GetValue(RunValue) != null;
                }
            }
            catch
            {
                return false;
            }
        }

        static void SetAutostart(bool enabled)
        {
            SetAutostart(enabled, Application.ExecutablePath);
        }

        /// <summary>The Run entry names a path, so the installer, which runs from somewhere
        /// else, has to say which exe it means.</summary>
        internal static void SetAutostart(bool enabled, string exe)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (key == null) return;

                    if (enabled)
                    {
                        key.SetValue(RunValue, "\"" + exe + "\"");
                        Log.Write("controller: autostart enabled");
                    }
                    else
                    {
                        key.DeleteValue(RunValue, false);
                        Log.Write("controller: autostart disabled");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("controller: autostart change failed - " + ex.Message);
            }
        }

        // --- shutdown ---------------------------------------------------------

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            Log.Write("controller: shutting down");

            // Before the handle goes, while there is still something to unregister.
            if (_sessionNotifications)
            {
                Native.WTSUnRegisterSessionNotification(Handle);
                _sessionNotifications = false;
            }

            StopTimer(ref _startupTimer);
            StopTimer(ref _reconcileTimer);
            StopTimer(ref _watchdogTimer);
            StopTimer(ref _glideTimer);
            if (_tileWatch != null) { _tileWatch.Dispose(); _tileWatch = null; }
            if (_trayWatch != null) { _trayWatch.Dispose(); _trayWatch = null; }
            if (_windowWatch != null) { _windowWatch.Dispose(); _windowWatch = null; }
            if (_foregroundWatch != null) { _foregroundWatch.Dispose(); _foregroundWatch = null; }
            StopTimer(ref _settleTimer);
            StopTimer(ref _settingsTimer);

            // A change still waiting out the settings debounce is saved, not applied: the
            // strip is about to be torn down, and rebuilding it first would be wasted work.
            if (_settingsPending != null)
            {
                _settingsPending.LastCount = _config.LastCount;
                _config = _settingsPending;
                _settingsPending = null;
                _config.Save();
            }
            if (_settings != null) { _settings.Close(); _settings = null; }

            // Stops watching; windows already asked to close carry on regardless.
            if (_closeRun != null)
            {
                CloseAllRun run = _closeRun;
                _closeRun = null;   // first, so its Ended handler knows not to refresh
                run.Dispose();
            }

            if (_configWatcher != null) { _configWatcher.Dispose(); _configWatcher = null; }
            if (_hotkeys != null) { _hotkeys.Dispose(); _hotkeys = null; }

            if (_saveTimer != null)
            {
                _saveTimer.Stop();
                if (_pendingCount >= 1)
                {
                    _config.LastCount = _pendingCount;
                    _config.Save();
                }
                _saveTimer.Dispose();
                _saveTimer = null;
            }
            else if (_service != null && _service.Count >= 1)
            {
                _config.LastCount = _service.Count;
                _config.Save();
            }

            DisposeStrip();

            if (_buttonMenu != null) { _buttonMenu.Dispose(); _buttonMenu = null; }

            if (_service != null) { _service.Dispose(); _service = null; }

            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
                _tray = null;
            }
            if (_trayIcon != null) { _trayIcon.Dispose(); _trayIcon = null; }

            base.OnFormClosing(e);
        }

        static void StopTimer(ref Timer timer)
        {
            if (timer == null) return;
            timer.Stop();
            timer.Dispose();
            timer = null;
        }
    }
}

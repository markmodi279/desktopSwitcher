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

        DesktopService _service;
        WindowInventory _inventory;
        SwitcherStrip _strip;
        NotifyIcon _tray;
        Icon _trayIcon;

        ContextMenuStrip _buttonMenu;   // the open one, rebuilt per click - contents follow the model
        MenuTheme _menuTheme;

        Timer _startupTimer;     // waits for the taskbar to exist at login
        Timer _reconcileTimer;   // model safety net
        Timer _watchdogTimer;    // strip / taskbar health
        Timer _focusTimer;       // remembers the last real foreground window
        Timer _saveTimer;        // debounced config write
        Timer _settleTimer;      // debounced rebuild after a theme, accent, or display change

        uint _taskbarCreatedMessage;
        int _buttonWidth, _plusWidth, _margin, _barHeight;
        Color _background;
        bool _started;
        int _pendingCount = -1;

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
            _service.Start();

            // Windows forgets the desktop count across a reboot; put it back.
            if (_config.LastCount > _service.Count)
                _service.EnsureCount(_config.LastCount);

            // Subscribed only after the initial model settles, so the first
            // DesktopsChanged does not build a strip that RebuildStrip immediately
            // replaces.
            _service.DesktopsChanged += OnDesktopsChanged;
            _service.CurrentChanged += OnCurrentChanged;
            _service.NamesChanged += OnNamesChanged;

            RebuildStrip();

            _reconcileTimer = StartTimer(_config.ReconcileMs, delegate { _service.Tick(); RefreshOccupancy(); });
            _watchdogTimer = StartTimer(1000, delegate { Watchdog(); });
            _focusTimer = StartTimer(300, delegate { _foreground.Sample(); });

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
            else if (_config.Incomplete)
            {
                // Same reasoning one upgrade later: a file written before a setting existed
                // leaves it on its default with no line to edit, which is the very state
                // the first-run write exists to prevent. Every value already in the file
                // survives, having just been loaded from it.
                _config.Save();
                Log.Write("controller: config rewritten with settings added since it was written");
            }

            _started = true;
            Log.Write("controller: started");
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
            _plusWidth = _host.Scale(_config.PlusWidth);
            _margin = _host.Scale(_config.Margin);
            _barHeight = _host.Scale(3);

            if (!_config.BackgroundColor.IsEmpty)
            {
                _background = _config.BackgroundColor;
                return;
            }

            Color sampled;
            int probe = SwitcherStrip.MeasureWidth(desktopCount, _buttonWidth, _plusWidth);

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

            int width = SwitcherStrip.MeasureWidth(count, _buttonWidth, _plusWidth);

            Rectangle bounds;
            if (!_host.TryComputeBounds(width, _margin, out bounds))
            {
                Log.Write("controller: could not compute strip bounds");
                return;
            }

            _strip = new SwitcherStrip(_host.TrayWindow, bounds,
                                       _buttonWidth, _plusWidth, _barHeight,
                                       _background, _config.HighlightColor,
                                       (uint)_config.TooltipDelayMs, _config.TooltipWidth,
                                       _config.AnimationMs, _host.DpiScale);

            _strip.SwitchRequested += delegate(Guid id) { _service.SwitchTo(id); };
            _strip.CreateRequested += delegate { _service.Create(); };
            _strip.RemoveRequested += delegate(Guid id) { _service.Remove(id); };
            _strip.MoveWindowRequested += OnMoveWindowRequested;
            _strip.TooltipProvider = BuildTooltip;

            // Left unsubscribed when the menu is off, which is what puts right click back to
            // sending the active window: the strip falls through when nothing is listening.
            if (_config.ContextMenu)
            {
                _menuTheme = new MenuTheme(_background, _host.DpiScale);
                _strip.ContextMenuRequested += ShowButtonMenu;
            }

            _foreground.Ignore(_strip.Handle);

            _strip.SetDesktops(_service.Desktops);
            _host.Reassert(_strip.Handle, _strip.Width, _margin);
            RefreshOccupancy();

            Log.Write("controller: strip rebuilt");
        }

        /// <summary>
        /// Which desktops have windows, pushed to the strip's occupancy dots. Called from
        /// the reconcile tick, which is what "refreshed on the existing 2s tick" means, and
        /// also right after every relayout - a rebuild or a desktop-set change - so the
        /// strip does not sit undotted for up to 2s after either.
        ///
        /// Skipped below two desktops, where the dot would only ever answer a question
        /// nobody is asking: with one desktop there is nowhere else for a window to be.
        /// </summary>
        void RefreshOccupancy()
        {
            if (_strip == null || _service == null || _inventory == null) return;
            if (!_config.OccupancyDots) return;
            if (_service.Count < 2) return;

            _strip.SetOccupancy(_inventory.OccupiedDesktops(_service.Count));
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

            menu.Items.Add(new ToolStripSeparator());

            // Removing is the one item worth hesitating over, so it says what is at stake.
            // Windows moves the windows to another desktop rather than closing them, but
            // finding them again is still a nuisance.
            AddItem(menu, RemoveCaption(desktop), desktops.Count > 1,
                    delegate { _service.Remove(id); });

            return menu;
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
            return text.Substring(0, max - 3).TrimEnd() + "...";
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

            if (index >= desktops.Count)
                return new TooltipContent("New desktop", new List<string> { "Win+Ctrl+D" });

            if (index < 0) return null;
            Desktop desktop = desktops[index];

            string title = desktop.DisplayName;
            if (desktop.IsCurrent) title += "   (current)";

            return new TooltipContent(title, WindowLines(desktop));
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

        IList<string> WindowLines(Desktop desktop)
        {
            var lines = new List<string>();
            IList<WindowEntry> windows = _inventory.WindowsOn(desktop.Id);

            // An empty desktop is worth saying out loud: it is the one you can remove
            // without losing anything, and otherwise you would have to visit it to find out.
            if (windows.Count == 0)
            {
                lines.Add("- empty -");
                return lines;
            }

            // Only real windows get a bullet. The overflow line is a count, not an entry.
            int max = _config.TooltipMaxWindows;
            for (int i = 0; i < windows.Count && i < max; i++)
                lines.Add(WindowLine(windows[i]));

            if (windows.Count > max)
                lines.Add("+" + (windows.Count - max) + " more");

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

            _strip.SetDesktops(_service.Desktops);
            _host.Reassert(_strip.Handle, _strip.Width, _margin);
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
            _host.Reassert(_strip.Handle, _strip.Width, _margin);
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
            RebuildStrip();   // re-scales and re-samples on its own, after the strip is gone

            Log.Write("controller: recovered from Explorer restart");
        }

        protected override void WndProc(ref Message m)
        {
            if (_started && _taskbarCreatedMessage != 0
                && (uint)m.Msg == _taskbarCreatedMessage)
            {
                Log.Write("controller: TaskbarCreated received");
                HandleExplorerRestart();
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

            base.WndProc(ref m);
        }

        const int WM_SETTINGCHANGE = 0x001A;

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
        /// Covers both an accent/light-dark switch and a display change - the latter fires
        /// on waking from sleep as the monitor comes back, not only on an actual resolution
        /// change. Windows sends several of these in a burst as the change propagates, and
        /// the taskbar has not finished repainting when the first one arrives - sampling
        /// then reads the colour it is on its way out of (or, coming out of sleep, whatever
        /// transient frame DWM had not yet replaced), which is worse than not resampling at
        /// all and, unlike a failed read, does not fall back to the last good colour - it
        /// looks like a successful sample and gets cached until the next trigger. One timer,
        /// restarted by every message in the burst, so the work happens once and late enough
        /// for the pixel to have settled.
        /// </summary>
        void QueueSettledRebuild(string reason)
        {
            if (_settleTimer == null)
            {
                _settleTimer = new Timer();
                _settleTimer.Interval = 600;
                _settleTimer.Tick += delegate
                {
                    _settleTimer.Stop();
                    if (!_started) return;

                    Log.Write("controller: " + reason + " - resampling");
                    RebuildStrip();
                };
            }

            _settleTimer.Stop();
            _settleTimer.Start();
        }

        // --- config persistence -----------------------------------------------

        /// <summary>
        /// Re-reads config.ini and applies everything that is not the strip's own geometry;
        /// the caller rebuilds the strip, which is what picks up the sizes, the colours and
        /// whether the context menu is wired at all.
        ///
        /// Editing the file and having to restart is a strange thing to ask of an app whose
        /// whole pitch is that it installs nothing, and the file is deliberately written
        /// complete on first run so that every setting is there to be edited. This closes
        /// the loop those two decisions left open.
        /// </summary>
        void ReloadConfig()
        {
            // A debounced save still pending holds a desktop count newer than the file's.
            // Re-reading first would quietly roll it back to whatever was last written and
            // then write that back out on the next change.
            if (_pendingCount >= 1 && _saveTimer != null) SavePending(this, EventArgs.Empty);

            _config = Config.Load();

            // Diagnostics is the one setting that takes effect nowhere near the strip.
            Log.Init(Config.LogPath, _config.Diagnostics);

            if (_reconcileTimer != null) _reconcileTimer.Interval = _config.ReconcileMs;

            // The tray glyph is drawn from highlightColor, and it is the one visible thing
            // a rebuilt strip does not cover - left alone, a changed accent would show up
            // on the strip immediately and in the tray only at the next launch.
            Icon previous = _trayIcon;
            _trayIcon = CreateIcon(_config.HighlightColor);
            if (_tray != null) _tray.Icon = _trayIcon;
            if (previous != null) previous.Dispose();

            Log.Write("controller: settings reloaded from " + Config.FilePath);
        }

        void QueueSave(int count)
        {
            if (count == _config.LastCount) return;
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
                ReloadConfig();
                RebuildStrip();
            };
            menu.Items.Add(reload);

            menu.Items.Add(new ToolStripSeparator());

            var exit = new ToolStripMenuItem("Exit");
            exit.Click += delegate { Close(); };
            menu.Items.Add(exit);

            _tray = new NotifyIcon();
            _tray.Icon = _trayIcon;
            _tray.Text = "DesktopSwitcher";
            _tray.ContextMenuStrip = menu;
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

        static bool IsAutostartEnabled()
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
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (key == null) return;

                    if (enabled)
                    {
                        string exe = Application.ExecutablePath;
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

            StopTimer(ref _startupTimer);
            StopTimer(ref _reconcileTimer);
            StopTimer(ref _watchdogTimer);
            StopTimer(ref _focusTimer);
            StopTimer(ref _settleTimer);

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

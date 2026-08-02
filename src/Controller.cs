using System;
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
        readonly Config _config;
        readonly VirtualDesktopApi _api = new VirtualDesktopApi();
        readonly TaskbarHost _host = new TaskbarHost();
        readonly ForegroundTracker _foreground = new ForegroundTracker();

        DesktopService _service;
        SwitcherStrip _strip;
        NotifyIcon _tray;
        Icon _trayIcon;

        Timer _startupTimer;     // waits for the taskbar to exist at login
        Timer _reconcileTimer;   // model safety net
        Timer _watchdogTimer;    // strip / taskbar health
        Timer _focusTimer;       // remembers the last real foreground window
        Timer _saveTimer;        // debounced config write

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

            ComputeMetrics();

            _service = new DesktopService(_api, this);
            _service.Start();

            // Windows forgets the desktop count across a reboot; put it back.
            if (_config.LastCount > _service.Count)
                _service.EnsureCount(_config.LastCount);

            // Subscribed only after the initial model settles, so the first
            // DesktopsChanged does not build a strip that RebuildStrip immediately
            // replaces.
            _service.DesktopsChanged += OnDesktopsChanged;
            _service.CurrentChanged += OnCurrentChanged;

            RebuildStrip();

            _reconcileTimer = StartTimer(_config.ReconcileMs, delegate { _service.Tick(); });
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

        void ComputeMetrics()
        {
            _buttonWidth = _host.Scale(_config.ButtonWidth);
            _plusWidth = _host.Scale(_config.PlusWidth);
            _margin = _host.Scale(_config.Margin);
            _barHeight = _host.Scale(3);

            _background = _config.BackgroundColor;
            if (_background.IsEmpty)
            {
                Color sampled;
                int probe = SwitcherStrip.MeasureWidth(2, _buttonWidth, _plusWidth);
                _background = _host.TrySampleBackground(probe, _margin, out sampled)
                    ? sampled
                    : Color.FromArgb(0x1F, 0x1F, 0x1F);
            }
        }

        // --- the strip --------------------------------------------------------

        void RebuildStrip()
        {
            DisposeStrip();

            int count = _service.Count > 0 ? _service.Count : 1;
            int width = SwitcherStrip.MeasureWidth(count, _buttonWidth, _plusWidth);

            Rectangle bounds;
            if (!_host.TryComputeBounds(width, _margin, out bounds))
            {
                Log.Write("controller: could not compute strip bounds");
                return;
            }

            _strip = new SwitcherStrip(_host.TrayWindow, bounds,
                                       _buttonWidth, _plusWidth, _barHeight,
                                       _background, _config.HighlightColor);

            _strip.SwitchRequested += delegate(Guid id) { _service.SwitchTo(id); };
            _strip.CreateRequested += delegate { _service.Create(); };
            _strip.RemoveRequested += delegate(Guid id) { _service.Remove(id); };
            _strip.MoveWindowRequested += OnMoveWindowRequested;

            _foreground.Ignore(_strip.Handle);

            _strip.SetDesktops(_service.Desktops);
            _host.Reassert(_strip.Handle, _strip.Width, _margin);

            Log.Write("controller: strip rebuilt");
        }

        void DisposeStrip()
        {
            if (_strip == null) return;
            _strip.Dispose();
            _strip = null;
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

        // --- service events ---------------------------------------------------

        void OnDesktopsChanged(object sender, EventArgs e)
        {
            if (_strip == null)
            {
                RebuildStrip();
                return;
            }

            _strip.SetDesktops(_service.Desktops);
            _host.Reassert(_strip.Handle, _strip.Width, _margin);

            QueueSave(_service.Count);
        }

        void OnCurrentChanged(object sender, EventArgs e)
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

            ComputeMetrics();
            _api.Drop();
            _service.InvalidateNotifications();
            _service.Tick();
            RebuildStrip();

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
                if (_host.Locate())
                {
                    ComputeMetrics();
                    RebuildStrip();
                }
            }

            base.WndProc(ref m);
        }

        // --- config persistence -----------------------------------------------

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

            var reload = new ToolStripMenuItem("Reload strip");
            reload.Click += delegate
            {
                if (!_started) return;
                ComputeMetrics();
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

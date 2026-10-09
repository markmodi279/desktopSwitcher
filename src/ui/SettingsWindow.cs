using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DesktopSwitcher
{
    /// <summary>
    /// Every setting in config.ini, on tabs, applied as it is changed.
    ///
    /// The window never touches the live config. It edits a copy of its own, and after
    /// every change hands a fresh copy of that to whoever is listening on Changed - the
    /// Controller, which debounces it and applies it through the same ApplyConfig a hand
    /// edit or the tray's reload goes through. So there is one way settings take effect,
    /// and this window is just one more source of them.
    ///
    /// Two copies are kept: the one being edited, and the one the window opened with.
    /// Revert puts the second back. There is no OK and no Cancel because nothing is pending:
    /// what is on screen is what is applied and saved, and Close only closes. Defaults is
    /// as undoable as any other change, by Revert.
    ///
    /// Rows are built from Config.All. A setting added to that table appears here, on the
    /// tab its section names, without this file changing.
    ///
    /// A real top-level window with a normal frame, unlike everything else in the app:
    /// it is opened on purpose, worked in for a while and put away, so it belongs in the
    /// taskbar and Alt+Tab like any other window. The frame is the system's - with its title
    /// bar turned dark over a dark taskbar - and the body is drawn by the app's own palette.
    ///
    /// DPI is this window's own business. The process is per-monitor aware and WinForms
    /// in 4.8 does not rescale a form for it, so every size goes through the theme's S()
    /// and a WM_DPICHANGED - dragged to a monitor at another scale - rebuilds the contents
    /// at the new one.
    /// </summary>
    sealed class SettingsWindow : Form
    {
        readonly Config _snapshot;
        Config _working;
        SettingsTheme _theme;
        double _scale;

        readonly string[] _sections;
        TabRow _tabs;
        Panel _page;
        Panel _footer;
        Button _openFile, _defaults, _revert, _close;
        readonly List<SettingRow> _rows = new List<SettingRow>();
        readonly List<Action> _sync = new List<Action>();

        bool _syncing;     // pushing values into controls: their change events are not edits
        bool _arranging;

        /// <summary>
        /// The ColorDialog's custom colours, kept for as long as the app runs, so a colour
        /// mixed for the highlight is still there to pick for the background.
        /// </summary>
        static int[] _customColors;

        const int WM_DPICHANGED = 0x02E0;

        /// <summary>A copy of the settings after every change. Each call's copy is the receiver's to keep.</summary>
        public event Action<Config> Changed;

        public event EventHandler OpenFileRequested;

        public SettingsWindow(Config live, Color background, double scale, Rectangle workArea)
        {
            _snapshot = live.Clone();
            _working = live.Clone();
            _scale = scale;

            var sections = new List<string>();
            foreach (Setting s in Config.All)
                if (!s.Hidden && !sections.Contains(s.Section)) sections.Add(s.Section);
            _sections = sections.ToArray();

            Text = "DesktopSwitcher Settings";
            AutoScaleMode = AutoScaleMode.None;
            StartPosition = FormStartPosition.Manual;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = false;
            ShowInTaskbar = true;
            KeyPreview = true;

            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch (Exception) { /* the default icon is no reason not to open */ }

            Build(background);

            Size size = new Size(Math.Min(_theme.S(640), workArea.Width), Math.Min(_theme.S(660), workArea.Height));
            Bounds = new Rectangle(workArea.Left + (workArea.Width - size.Width) / 2,
                                   workArea.Top + (workArea.Height - size.Height) / 2,
                                   size.Width, size.Height);
        }

        // --- building ---------------------------------------------------------------

        void Build(Color background)
        {
            SuspendLayout();

            _theme = new SettingsTheme(_scale, background, _working.HighlightColor);
            BackColor = _theme.Surface;
            MinimumSize = new Size(_theme.S(480), _theme.S(380));

            _page = new Panel();
            _page.Dock = DockStyle.Fill;
            _page.AutoScroll = true;
            _page.BackColor = _theme.Surface;
            _page.ClientSizeChanged += delegate { Arrange(); };
            _page.HandleCreated += delegate { Native.SetDarkScrollBars(_page.Handle, _theme.Dark); };

            _tabs = new TabRow(_theme, _sections);
            _tabs.Dock = DockStyle.Top;
            _tabs.SelectedIndexChanged += delegate { ShowTab(true); };

            _footer = BuildFooter();

            // Docking takes the last added first: the footer and the tabs claim their edges,
            // then the page fills what is left.
            Controls.Add(_page);
            Controls.Add(_tabs);
            Controls.Add(_footer);

            _rows.Clear();
            _sync.Clear();
            foreach (Setting s in Config.All)
            {
                if (s.Hidden) continue;
                var row = new SettingRow(_theme, s, BuildEditor(s));
                _rows.Add(row);
                _page.Controls.Add(row);
            }

            SyncAll();
            UpdateEnabled();
            UpdateButtons();
            ShowTab(false);

            ResumeLayout(true);
            ActiveControl = _tabs;
        }

        Panel BuildFooter()
        {
            var footer = new Panel();
            footer.Dock = DockStyle.Bottom;
            footer.Height = _theme.S(60);
            footer.BackColor = _theme.Surface;
            footer.Paint += delegate(object sender, PaintEventArgs e)
            {
                using (var brush = new SolidBrush(_theme.Border))
                    e.Graphics.FillRectangle(brush, 0, 0, footer.Width, 1);
            };

            int w = _theme.S(92), h = _theme.S(30), gap = _theme.S(8), pad = _theme.S(16);
            int top = (footer.Height - h) / 2;

            _close = MakeButton("Close", w, h);
            _close.Click += delegate { Close(); };
            _revert = MakeButton("Revert", w, h);
            _revert.Click += delegate { Replace(_snapshot.Clone()); };
            _defaults = MakeButton("Defaults", w, h);
            _defaults.Click += delegate { Replace(_working.WithDefaults()); };

            _openFile = MakeButton("Open config.ini", _theme.S(130), h);
            _openFile.Click += delegate { if (OpenFileRequested != null) OpenFileRequested(this, EventArgs.Empty); };

            _openFile.Location = new Point(pad, top);
            _close.Anchor = _revert.Anchor = _defaults.Anchor = AnchorStyles.Top | AnchorStyles.Right;

            // Anchored right, so placed against the footer's width as it will be once docked:
            // the form's client width, since nothing is beside the footer.
            int right = ClientSize.Width - pad;
            footer.Width = ClientSize.Width;
            _close.Location = new Point(right - w, top);
            _revert.Location = new Point(_close.Left - gap - w, top);
            _defaults.Location = new Point(_revert.Left - gap - w, top);

            footer.Controls.Add(_openFile);
            footer.Controls.Add(_defaults);
            footer.Controls.Add(_revert);
            footer.Controls.Add(_close);
            return footer;
        }

        Button MakeButton(string text, int width, int height)
        {
            var b = new Button();
            b.Text = text;
            b.Size = new Size(width, height);
            b.Font = _theme.BodyFont;
            ConfirmDialog.StyleButton(b, _theme.Background);
            return b;
        }

        /// <summary>
        /// The control for one setting, wired both ways: an edit writes the working copy
        /// through the setting's own accessor, and an entry in _sync puts the working copy's
        /// value back into the control - after a Revert, after Defaults, after the amber/red
        /// rule moved the other slider, after the file was edited by hand.
        /// </summary>
        Control BuildEditor(Setting s)
        {
            switch (s.Kind)
            {
                case SettingKind.Int:
                {
                    var slider = new Slider(_theme);
                    slider.SetRange(s.Min, s.Max);
                    var box = new NumberBox(_theme, s.Unit);
                    box.SetRange(s.Min, s.Max);

                    slider.ValueChanged += delegate
                    {
                        if (_syncing) return;
                        box.Value = slider.Value;
                        Edited(s, delegate { s.SetInt(_working, slider.Value); });
                    };
                    box.ValueCommitted += delegate
                    {
                        if (_syncing) return;
                        slider.Value = box.Value;
                        Edited(s, delegate { s.SetInt(_working, box.Value); });
                    };
                    _sync.Add(delegate
                    {
                        int v = s.GetInt(_working);
                        slider.Value = v;
                        box.Value = v;
                    });
                    return new EditorGroup(_theme, slider, box);
                }

                case SettingKind.Bool:
                {
                    var toggle = new Toggle(_theme);
                    toggle.CheckedChanged += delegate
                    {
                        if (_syncing) return;
                        Edited(s, delegate { s.SetBool(_working, toggle.Checked); });
                    };
                    _sync.Add(delegate { toggle.Checked = s.GetBool(_working); });
                    return toggle;
                }

                case SettingKind.Choice:
                {
                    var choice = new Segmented(_theme, s.Captions);
                    choice.SelectedIndexChanged += delegate
                    {
                        if (_syncing) return;
                        Edited(s, delegate { s.SetInt(_working, choice.SelectedIndex); });
                    };
                    _sync.Add(delegate { choice.SelectedIndex = s.GetInt(_working); });
                    return choice;
                }

                default:
                {
                    var swatch = new Swatch(_theme);
                    swatch.PickRequested += delegate { PickColour(s); };

                    if (!s.AllowAuto)
                    {
                        _sync.Add(delegate { swatch.Color = s.GetColor(_working); });
                        return swatch;
                    }

                    // Automatic or a fixed colour. Choosing Custom starts from the colour
                    // automatic is currently producing, so the switch changes nothing on
                    // screen until a colour is actually picked - rather than jumping to some
                    // arbitrary default the person then has to correct.
                    var mode = new Segmented(_theme, new[] { "Automatic", "Custom" });
                    mode.SelectedIndexChanged += delegate
                    {
                        if (_syncing) return;
                        Color sampled = Color.FromArgb(_theme.Background.R, _theme.Background.G, _theme.Background.B);
                        Edited(s, delegate { s.SetColor(_working, mode.SelectedIndex == 0 ? Color.Empty : sampled); });
                    };
                    _sync.Add(delegate
                    {
                        Color c = s.GetColor(_working);
                        mode.SelectedIndex = c.IsEmpty ? 0 : 1;
                        swatch.Color = c;
                        swatch.Enabled = !c.IsEmpty;
                    });
                    return new EditorGroup(_theme, mode, swatch);
                }
            }
        }

        void PickColour(Setting s)
        {
            Color current = s.GetColor(_working);
            if (current.IsEmpty) current = _theme.Background;

            using (var dialog = new ColorDialog())
            {
                dialog.FullOpen = true;
                dialog.AnyColor = true;
                dialog.Color = current;
                if (_customColors != null) dialog.CustomColors = _customColors;

                if (dialog.ShowDialog(this) != DialogResult.OK) return;

                _customColors = dialog.CustomColors;
                Color picked = Color.FromArgb(dialog.Color.R, dialog.Color.G, dialog.Color.B);
                Edited(s, delegate { s.SetColor(_working, picked); });
            }
        }

        // --- editing ----------------------------------------------------------------

        /// <summary>
        /// One edit from one control: write it, let the amber/red rule settle the other
        /// value, bring every control and every row's enabled state into line, and pass the
        /// result on.
        /// </summary>
        void Edited(Setting s, Action write)
        {
            write();
            _working.Normalize(s.Key);
            AfterChange();
        }

        /// <summary>Revert and Defaults: a whole new working copy, then the same as an edit.</summary>
        void Replace(Config next)
        {
            _working = next;
            AfterChange();
        }

        void AfterChange()
        {
            SyncAll();
            UpdateEnabled();
            UpdateButtons();
            FollowAccent();

            if (Changed != null) Changed(_working.Clone());
        }

        /// <summary>
        /// The settings changed somewhere else - config.ini was saved by hand - and this
        /// window should show them. Revert still goes back to what the window opened with.
        /// </summary>
        public void ShowConfig(Config live)
        {
            _working = live.Clone();
            SyncAll();
            UpdateEnabled();
            UpdateButtons();
            FollowAccent();
        }

        void SyncAll()
        {
            _syncing = true;
            try
            {
                foreach (Action push in _sync) push();
            }
            finally
            {
                _syncing = false;
            }
        }

        void UpdateEnabled()
        {
            foreach (SettingRow row in _rows)
                row.Enabled = row.Setting.AppliesTo(_working);
        }

        void UpdateButtons()
        {
            _revert.Enabled = !_working.SameAs(_snapshot);
            _defaults.Enabled = !_working.SameAs(_working.WithDefaults());
        }

        /// <summary>
        /// The tabs' underline, the toggles and the sliders are drawn in the highlight
        /// colour, so they follow it the moment it is picked rather than after the
        /// controller's round trip.
        /// </summary>
        void FollowAccent()
        {
            if (_theme.Accent == _working.HighlightColor) return;
            _theme.SetColors(_theme.Background, _working.HighlightColor);
            Invalidate(true);
        }

        // --- theme --------------------------------------------------------------

        /// <summary>
        /// The taskbar colour this window is derived from changed - Windows switched between
        /// light and dark, or a custom background was just applied. Recolours in place;
        /// see SettingsTheme for why nothing is rebuilt.
        /// </summary>
        public void Retheme(Color background)
        {
            if (_theme == null) return;
            if (_theme.Background == background && _theme.Accent == _working.HighlightColor) return;

            bool wasDark = _theme.Dark;
            _theme.SetColors(background, _working.HighlightColor);

            BackColor = _theme.Surface;
            _page.BackColor = _theme.Surface;
            _footer.BackColor = _theme.Surface;
            foreach (Button b in new[] { _openFile, _defaults, _revert, _close })
                ConfirmDialog.StyleButton(b, background);

            if (wasDark != _theme.Dark) ApplyChrome();
            Invalidate(true);
        }

        void ApplyChrome()
        {
            if (!IsHandleCreated) return;
            Native.SetDarkTitleBar(Handle, _theme.Dark);
            if (_page.IsHandleCreated) Native.SetDarkScrollBars(_page.Handle, _theme.Dark);

            // A title bar already on screen does not repaint for the attribute alone.
            // Asking for the frame to be recalculated is what gets it redrawn.
            Native.SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0,
                                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER |
                                Native.SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }

        const uint SWP_FRAMECHANGED = 0x0020;

        // --- layout -------------------------------------------------------------

        /// <summary>
        /// Whether a row belongs on the selected tab. Asked of the row's section rather than
        /// of row.Visible, because Visible reports whether the control is actually showing -
        /// which, before the window is first on screen, is nothing. Arrange used to skip every
        /// row on that basis, so the window opened with an empty page that only filled in
        /// once a tab change laid it out again.
        /// </summary>
        bool OnTab(SettingRow row)
        {
            return row.Setting.Section == _sections[_tabs.SelectedIndex];
        }

        void ShowTab(bool resetScroll)
        {
            _page.SuspendLayout();
            foreach (SettingRow row in _rows)
                row.Visible = OnTab(row);
            if (resetScroll) _page.AutoScrollPosition = Point.Empty;
            _page.ResumeLayout(false);

            Arrange();
        }

        /// <summary>
        /// Stacks the current tab's rows down the page at its width. Re-entered when the
        /// vertical scroll bar comes or goes, since that changes the width; the guard makes
        /// that a second pass rather than a loop.
        /// </summary>
        void Arrange()
        {
            if (_arranging || _page == null || _tabs == null) return;
            _arranging = true;
            try
            {
                int width = _page.ClientSize.Width;
                int y = _theme.S(6);
                int scroll = _page.AutoScrollPosition.Y;

                foreach (SettingRow row in _rows)
                {
                    if (!OnTab(row)) continue;
                    int h = row.Arrange(width);
                    row.Location = new Point(0, y + scroll);
                    y += h;
                }

                _page.AutoScrollMinSize = new Size(0, y + _theme.S(6));
            }
            finally
            {
                _arranging = false;
            }

            // The scroll bar may have just appeared or gone, changing the width the rows
            // were laid out at. One more pass, at the width that is now true.
            if (_page.ClientSize.Width != RowWidth()) Arrange();
        }

        int RowWidth()
        {
            foreach (SettingRow row in _rows)
                if (OnTab(row)) return row.Width;
            return _page.ClientSize.Width;
        }

        // --- window ---------------------------------------------------------------

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);

            // The scale handed in is the taskbar's monitor. If the window has landed on
            // another, build again at the scale it is actually on.
            double actual = Native.GetDpiScale(Handle);
            if (Math.Abs(actual - _scale) > 0.01) Rescale(actual, Rectangle.Empty);

            ApplyChrome();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Control | Keys.Tab:
                case Keys.Control | Keys.PageDown:
                    _tabs.Cycle(1);
                    return true;
                case Keys.Control | Keys.Shift | Keys.Tab:
                case Keys.Control | Keys.PageUp:
                    _tabs.Cycle(-1);
                    return true;
                case Keys.Escape:
                    Close();
                    return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_DPICHANGED)
            {
                int dpi = (int)(((long)m.WParam >> 16) & 0xFFFF);
                var suggested = (Native.RECT)Marshal.PtrToStructure(m.LParam, typeof(Native.RECT));
                Rescale(dpi / 96.0, new Rectangle(suggested.Left, suggested.Top, suggested.Width, suggested.Height));
                m.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref m);
        }

        /// <summary>
        /// Everything again at a new scale: a new theme with fonts at the new size and new
        /// controls built from it. The tab survives; the scroll position goes back to the top,
        /// since every row's height has changed under it anyway.
        /// </summary>
        void Rescale(double scale, Rectangle bounds)
        {
            int tab = _tabs.SelectedIndex;
            Color background = _theme.Background;
            SettingsTheme old = _theme;

            SuspendLayout();
            var children = new List<Control>();
            foreach (Control c in Controls) children.Add(c);
            Controls.Clear();
            foreach (Control c in children) c.Dispose();

            _scale = scale;
            Build(background);
            old.Dispose();

            _tabs.SelectedIndex = tab;
            ShowTab(true);
            if (!bounds.IsEmpty) Bounds = bounds;
            ResumeLayout(true);

            if (IsHandleCreated) ApplyChrome();
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && _theme != null) { _theme.Dispose(); _theme = null; }
        }
    }
}

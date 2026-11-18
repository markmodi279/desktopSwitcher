using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace DesktopSwitcher
{
    /// <summary>What the confirmation was answered with.</summary>
    enum CloseChoice
    {
        Cancel,
        CloseSelected,
        CloseAndRemove
    }

    /// <summary>
    /// "Close all and remove" and "Close all windows", from the question to the answer, in
    /// one window.
    ///
    /// This used to be two: a modal checklist (CloseSelectDialog, or ConfirmDialog for a
    /// single window) that closed when answered, and a progress panel (CloseProgressWindow)
    /// that opened in its place. The list the user had just been reading vanished and a
    /// different one appeared, laid out differently, with a status line trying to sum up
    /// every window at once and three buttons that acted on all of them. Now the checklist
    /// is the progress list: the ticks turn into each window's state in the same rows, the
    /// rows the user unticked stay on as "kept", and whatever is holding things up carries
    /// its own Show and Force.
    ///
    ///   Confirm   the title and sentence the controller wrote, the list, and the buttons
    ///             CloseSelectDialog had, under the same rules - see UpdateConfirm.
    ///   Running   "4 of 6 closed - 2 need you", a bar, the list, Force all and Cancel.
    ///   Done      a tick and what happened, held a moment, then faded out. Nothing to
    ///             dismiss: the strip is already showing the outcome.
    ///
    /// Modeless throughout, because the run needs the user free to answer save prompts,
    /// and so the confirmation is too - one window cannot be modal for half its life. The
    /// controller holds BeginAsking while it confirms, which is what keeping the hover
    /// panel away needed the modal loop for. TopMost so a prompt does not bury it, and a
    /// tool window, which gives it no desktop of its own: it shows on every one, so a Show
    /// that switches desktops does not leave it behind - which is what the progress panel
    /// found out the hard way, and why it already was one.
    ///
    /// Laid out over the strip as ConfirmDialog is, and re-placed whenever its height
    /// changes, bottom edge fixed, so a list folding away shrinks it toward the strip.
    /// </summary>
    sealed class CloseAllWindow : Form
    {
        enum Phase { Confirm, Running, Done }

        const int FadeMs = 200;
        const int BarMs = 300;

        const string HintRemove = "Unticked windows stay, so the desktop can't be removed.";

        readonly Color _background;
        readonly Rectangle _anchor;
        readonly double _scale;
        readonly bool _remove;
        readonly bool _animate;
        readonly ClosePacing _pacing;

        /// <summary>The run's outcome, waiting for the list to finish telling it.</summary>
        string _doneTitle, _doneLine;
        readonly int _closeCount;
        readonly Font _titleFont;
        readonly Font _bodyFont;
        readonly CloseList _list;
        readonly Button _close;
        readonly Button _removeButton;
        readonly Button _forceAll;
        readonly Button _cancel;
        readonly int _pad, _inner;

        Phase _phase = Phase.Confirm;
        string _title;
        string _body;
        int _bodyHeight;
        string _hint = "";
        int _closed, _total, _needYou;
        float _bar, _barTarget;
        bool _forceArmed;
        int _forceArmedAt;
        string _forceLabel = "Force all";

        Timer _frames;
        int _lastFrame;
        int _sinceArrange;
        int _frameCount;
        Timer _hold;
        bool _fading;

        /// <summary>Set once the window is closing by any route, so nothing raises twice.</summary>
        bool _finishing;

        /// <summary>The confirmation was answered: CloseSelected or CloseAndRemove. Cancel
        /// arrives as Cancelled instead, by the button, Escape or Alt+F4.</summary>
        public event Action<CloseChoice> Answered;
        public event EventHandler Cancelled;

        /// <summary>A row's Show, Force or Ask again; for Force, whether it was the second click.</summary>
        public event Action<IntPtr, RowButton, bool> RowPressed;

        /// <summary>Force all; true when it was the second click of an armed one.</summary>
        public event Action<bool> ForceAllPressed;

        /// <summary>Cancel while running - by the button, Escape or Alt+F4.</summary>
        public event EventHandler StopRequested;

        /// <param name="windows">The closeable windows, each a ticked row.</param>
        /// <param name="pinned">
        /// Windows pinned to every desktop. Shown greyed after the rest and never ticked:
        /// close-all leaves them alone, and without a row for them a window the user can see
        /// on this desktop would be missing from the list with nothing to say why.
        /// </param>
        /// <param name="pacing">The Close all tab's settings, and whether anything animates.</param>
        public CloseAllWindow(string title, string body, IList<WindowEntry> windows, IList<WindowEntry> pinned,
                              bool remove, Color background, Color accent, double dpiScale,
                              Rectangle anchor, ClosePacing pacing)
        {
            _title = title;
            _body = body;
            _remove = remove;
            _background = background;
            _anchor = anchor;
            _scale = dpiScale;
            _pacing = pacing ?? new ClosePacing();
            _animate = _pacing.Animate;
            _closeCount = windows.Count;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            DoubleBuffered = true;
            KeyPreview = false;
            BackColor = Palette.Lift(background, 0.06f);
            Text = title;

            _pad = S(16);
            _titleFont = new Font("Segoe UI", (float)(12 * dpiScale), FontStyle.Bold, GraphicsUnit.Pixel);
            _bodyFont = new Font("Segoe UI", (float)(12 * dpiScale), FontStyle.Regular, GraphicsUnit.Pixel);

            // Fixed, not sized to the text. The rows are as wide as the window, and one that
            // resized to its longest title would be as wide as a browser tab's page title -
            // or, clamped, would wrap the sentence differently from one desktop to the next.
            _inner = S(420);
            ClientSize = new Size(_inner + 2 * _pad, S(200));

            // One window gets no checkbox: a checklist of one box is a second way to say
            // Cancel. It keeps the box's column, so its row does not shift when the box's
            // place becomes the run's status mark.
            _list = new CloseList(windows, pinned, windows.Count == 1, background, accent, dpiScale, _bodyFont, _pacing);
            _list.Width = _inner;
            _list.TabIndex = 0;
            _list.Changed += delegate { UpdateConfirm(); };
            _list.HeightChanging += delegate { Kick(); };
            _list.ButtonPressed += delegate(IntPtr h, RowButton b, bool armed)
            {
                Action<IntPtr, RowButton, bool> handler = RowPressed;
                if (handler != null) handler(h, b, armed);
            };
            _list.MouseDown += delegate { if (_phase == Phase.Done) Finish(); };
            Controls.Add(_list);

            _close = MakeButton("Close all");
            _removeButton = remove ? MakeButton("Close and remove") : null;
            _forceAll = MakeButton("Force all");
            SetShown(_forceAll, false);
            _cancel = MakeButton("Cancel");

            // The close button is as wide as the widest label it can carry, so it never
            // changes width under the pointer as rows are ticked and unticked.
            _close.Width = Math.Max(_close.Width, MeasureButton("Close " + _closeCount));

            _close.Click += delegate { Answer(CloseChoice.CloseSelected); };
            if (_removeButton != null) _removeButton.Click += delegate { Answer(CloseChoice.CloseAndRemove); };
            _forceAll.Click += delegate
            {
                bool armed = _forceArmed;
                DisarmForceAll();
                Action<bool> handler = ForceAllPressed;
                if (handler != null) handler(armed);
            };
            _forceAll.MouseLeave += delegate { DisarmForceAll(); };
            _cancel.Click += delegate { CancelPressed(); };

            _close.TabIndex = 1;
            if (_removeButton != null) _removeButton.TabIndex = 2;
            _forceAll.TabIndex = 3;
            _cancel.TabIndex = 4;

            // Cancel has the focus, as in ConfirmDialog: a focused Button fires on Enter, so
            // a stray Enter and Escape both reach the safe choice. Closing is a click, or a
            // Tab and a key, never one keystroke from the moment the window appears.
            CancelButton = _cancel;
            ActiveControl = _cancel;

            MeasureBody();
            UpdateConfirm();
            Arrange();
        }

        int S(int value)
        {
            return (int)Math.Round(value * _scale);
        }

        Button MakeButton(string text)
        {
            var button = new Button();
            button.Text = text;
            button.Font = _bodyFont;
            button.Size = new Size(MeasureButton(text), S(28));
            ConfirmDialog.StyleButton(button, _background);
            Controls.Add(button);
            return button;
        }

        int MeasureButton(string text)
        {
            // A scratch surface, not CreateGraphics: that would create the window's handle
            // from inside the constructor, before it has a size or a place.
            using (var scratch = new Bitmap(1, 1))
            using (var g = Graphics.FromImage(scratch))
                return Math.Max(S(88), TextRenderer.MeasureText(g, text, _bodyFont).Width + S(24));
        }

        void MeasureBody()
        {
            using (var scratch = new Bitmap(1, 1))
            using (var g = Graphics.FromImage(scratch))
            {
                Size size = TextRenderer.MeasureText(g, _body, _bodyFont, new Size(_inner, int.MaxValue),
                                                     TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                _bodyHeight = size.Height;
            }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                // WS_EX_TOOLWINDOW: no taskbar button, no Alt+Tab entry, and - the reason it
                // is here - no virtual desktop of its own, so it shows on every one.
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_TOOLWINDOW;

                // Composited, so the window and its buttons and list paint as one. Without it
                // a screen recording caught the in-between frames: on Close, "Close and remove"
                // half erased behind a Force all already drawn over it; mid-resize, Cancel drawn
                // twice and the title cut off - children moved and painted before the window
                // around them had. Dropped again for the fade, which needs WS_EX_LAYERED.
                cp.ExStyle |= Native.WS_EX_COMPOSITED;
                return cp;
            }
        }

        // --- confirm ----------------------------------------------------------------

        public HashSet<IntPtr> Ticked { get { return _list.TickedHandles(); } }

        public bool Confirming { get { return _phase == Phase.Confirm; } }

        public bool IsDone { get { return _phase == Phase.Done; } }

        /// <summary>
        /// The buttons and the hint, from how many rows are ticked. Run on every toggle.
        ///
        /// Remove is only offered with every row ticked. Removing a desktop makes the shell
        /// carry whatever is left on it to a neighbour, so "keep these" plus "remove" would
        /// really mean "move these to whichever desktop the shell picks" - which the window
        /// would then have to say, for something nobody asked for. With a row unticked the
        /// remove button greys out, the hint says why, and "Close N" appears beside it,
        /// which closes the ticked ones and keeps the desktop.
        /// </summary>
        void UpdateConfirm()
        {
            if (_phase != Phase.Confirm) return;

            int ticked = _list.TickedCount;
            bool all = ticked == _closeCount;

            _close.Text = all ? "Close all" : "Close " + ticked;
            SetEnabled(_close, ticked > 0);

            if (_remove)
            {
                // All ticked, remove mode is what it always was: Close and remove, Cancel.
                // "Close all" beside it would be the menu's other item, offered again here.
                SetShown(_close, !all);
                SetEnabled(_removeButton, all);
                _hint = all ? "" : HintRemove;
            }
            Invalidate();
        }

        void Answer(CloseChoice choice)
        {
            if (_phase != Phase.Confirm) return;
            Action<CloseChoice> handler = Answered;
            if (handler != null) handler(choice);
        }

        void CancelPressed()
        {
            if (_phase == Phase.Confirm)
            {
                EventHandler handler = Cancelled;
                if (handler != null) handler(this, EventArgs.Empty);
                Finish();
            }
            else if (_phase == Phase.Running && _doneTitle == null)
            {
                EventHandler handler = StopRequested;
                if (handler != null) handler(this, EventArgs.Empty);
            }
            else Finish();
        }

        /// <summary>
        /// A flat button disabled keeps the border it was styled with, so it still looked
        /// pressable beside an enabled one; the border fades with the text.
        /// </summary>
        void SetEnabled(Button button, bool enabled)
        {
            if (button.Enabled == enabled) return;

            // Disabling the focused button would leave the keyboard nowhere; give it to the
            // list, which is what was being used to change the ticks.
            if (!enabled && button.Focused) _list.Focus();

            button.Enabled = enabled;
            button.FlatAppearance.BorderColor = enabled
                ? Palette.Lift(_background, 0.24f)
                : Palette.Lift(_background, 0.14f);
        }

        // --- running ----------------------------------------------------------------

        /// <summary>
        /// The run has the window now. The checklist's buttons go, Force all takes their
        /// place, and Cancel - rightmost in both - stays exactly where it was, so the one
        /// button that is the same thing in both phases does not move.
        /// </summary>
        public void BeginRunning(string desktopName, ICollection<IntPtr> asked)
        {
            _phase = Phase.Running;
            _title = "Closing \"" + desktopName + "\"";
            Text = _title;
            _hint = "";

            SetShown(_close, false);
            if (_removeButton != null) SetShown(_removeButton, false);
            SetShown(_forceAll, true);
            SetEnabled(_forceAll, false);

            _list.BeginRunning(asked);
            ActiveControl = _cancel;
            Arrange();
            Kick();
        }

        /// <summary>The run's tick: every row, and the counts above them.</summary>
        /// <param name="forceable">Rows a Force all would still try - its count and whether it is live.</param>
        /// <param name="hint">The line under the list, the run's to word: it knows what Force ran into.</param>
        public void Update(IList<CloseRow> rows, int closed, int total, int needYou, int forceable, string hint)
        {
            if (_phase != Phase.Running) return;

            // The subtitle and the bar count what the list has shown closed, not what the run
            // has seen close: with the cascade they run behind it, by design - see ClosePacing.
            bool changed = total != _total || needYou != _needYou || hint != _hint;
            _total = total;
            _needYou = needYou;
            _hint = hint;

            int open = forceable;
            if (_forceArmed && unchecked(Environment.TickCount - _forceArmedAt) > CloseList.ArmMs) DisarmForceAll();
            if (!_forceArmed)
            {
                string label = open > 0 ? "Force all (" + open + ")" : "Force all";
                if (label != _forceLabel) { _forceLabel = label; SetButtonText(_forceAll, label); }
            }
            SetEnabled(_forceAll, open > 0);

            _list.Update(rows);
            changed |= SyncCounts();

            // Only the header and the hint are this window's own drawing; the list repaints
            // itself. Nothing changed, nothing to draw - see CloseList.Update.
            if (changed) Invalidate();
            Kick();
        }

        /// <summary>A row's Force needs its second click.</summary>
        public void ArmRow(IntPtr handle, string label)
        {
            _list.Arm(handle, label);
        }

        /// <summary>Force all needs its second click: the button says what it will also end.</summary>
        public void ArmForceAll(string label)
        {
            _forceArmed = true;
            _forceArmedAt = Environment.TickCount;
            SetButtonText(_forceAll, label);
            _forceAll.ForeColor = CloseList.Bad;
            _forceAll.FlatAppearance.BorderColor = CloseList.Bad;
        }

        void DisarmForceAll()
        {
            if (!_forceArmed) return;
            _forceArmed = false;
            SetButtonText(_forceAll, _forceLabel);
            _forceAll.ForeColor = Palette.BodyText(_background);
            _forceAll.FlatAppearance.BorderColor = Palette.Lift(_background, _forceAll.Enabled ? 0.24f : 0.14f);
        }

        void SetButtonText(Button button, string text)
        {
            if (button.Text == text) return;
            button.Text = text;
            int width = MeasureButton(text);
            if (button.Width != width)
            {
                button.Width = width;
                ArrangeButtons(button.Top);
            }
        }

        // --- done -------------------------------------------------------------------

        /// <summary>
        /// The run is over and it went the way it was meant to: say so, briefly, and go.
        /// Held while the pointer is over it, so it can be read; a click dismisses it.
        /// </summary>
        public void ShowDone(string title, string line)
        {
            if (_phase == Phase.Done || IsDisposed) return;

            // The run is over, but the list may still be telling it - rows waiting their turn
            // in the cascade, or folding. The message waits for the last of them, so it never
            // claims "5 closed" over rows still spinning. Nothing is left to force or cancel.
            _doneTitle = title;
            _doneLine = line;
            SetEnabled(_forceAll, false);
            DisarmForceAll();
            _hint = "";
            if (_list.Finished) EnterDone();
            else Kick();
        }

        /// <summary>The run's outcome is waiting and the list has caught up with it.</summary>
        public bool DonePending { get { return _doneTitle != null && _phase != Phase.Done; } }

        /// <summary>Counts and bar from the list. True when they changed.</summary>
        bool SyncCounts()
        {
            int shown = _list.ShownClosed;
            if (shown == _closed) return false;
            _closed = shown;
            _barTarget = _total == 0 ? 1f : Math.Min(1f, (float)shown / _total);
            return true;
        }

        void EnterDone()
        {
            if (_phase == Phase.Done || IsDisposed) return;
            string title = _doneTitle, line = _doneLine;
            _phase = Phase.Done;
            _title = title;

            // Nothing to type into from here on, and it must not take activation by
            // accident. Hiding the focused Cancel below makes WinForms move focus to the
            // next control, and SetFocus inside a window that is not active activates it -
            // taking the keyboard from the dialog the user has just answered, or from
            // whatever Windows handed it to once the last window closed. So the focus is let
            // go first, and the window is made one that cannot be activated at all; a click
            // still reaches it, which is all dismissing it needs.
            if (Form.ActiveForm != this) ActiveControl = null;
            if (IsHandleCreated)
            {
                int ex = Native.GetWindowLong(Handle, Native.GWL_EXSTYLE);
                Native.SetWindowLong(Handle, Native.GWL_EXSTYLE, ex | Native.WS_EX_NOACTIVATE);
            }

            // Only the window knows what was unticked: the run never tracked those.
            int kept = _list.KeptCount;
            _body = line + (kept > 0 ? ". " + kept + " kept" : "") + ".";
            _hint = "";
            _barTarget = 1f;
            SetShown(_forceAll, false);
            SetShown(_cancel, false);

            Arrange();
            Invalidate();
            Kick();

            if (_pacing.DoneHoldMs <= 0) { Finish(); return; }

            _hold = new Timer();
            _hold.Interval = _pacing.DoneHoldMs;
            _hold.Tick += delegate
            {
                if (!IsDisposed && Bounds.Contains(Cursor.Position))
                {
                    _hold.Interval = 400;   // read it, then go
                    return;
                }
                _hold.Stop();
                if (!_animate) { Finish(); return; }
                if (IsHandleCreated)
                {
                    int ex = Native.GetWindowLong(Handle, Native.GWL_EXSTYLE);
                    Native.SetWindowLong(Handle, Native.GWL_EXSTYLE, ex & ~Native.WS_EX_COMPOSITED);
                }
                _fading = true;
                Kick();
            };
            _hold.Start();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (_phase == Phase.Done) Finish();
        }

        /// <summary>Closes the window on the run's or the controller's behalf, raising nothing.</summary>
        public void Finish()
        {
            if (_finishing || IsDisposed) return;
            _finishing = true;
            Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Alt+F4 means what Cancel means.
            if (!_finishing)
            {
                _finishing = true;
                if (_phase == Phase.Confirm)
                {
                    EventHandler handler = Cancelled;
                    if (handler != null) handler(this, EventArgs.Empty);
                }
                else if (_phase == Phase.Running)
                {
                    EventHandler handler = StopRequested;
                    if (handler != null) handler(this, EventArgs.Empty);
                }
            }
            StopTimers();
            base.OnFormClosing(e);
        }

        /// <summary>Frames drawn, the list's and the window's, for the perf log.</summary>
        public int Frames { get { return _frameCount + _list.Frames; } }

        /// <summary>Row images rendered, for the perf log.</summary>
        public int Renders { get { return _list.Renders; } }

        /// <summary>Where a window's frames went, in ms, for the perf log.</summary>
        public string Timings
        {
            get
            {
                double f = System.Diagnostics.Stopwatch.Frequency / 1000.0;
                return string.Format("frames {0:0} ms, arrange {1:0} ms x{2}, window paint {3:0} ms, list paint {4:0} ms",
                                     FrameTicks / f, ArrangeTicks / f, Arranges, PaintTicks / f, _list.PaintTicks / f);
            }
        }

        // --- frames -----------------------------------------------------------------

        /// <summary>
        /// Starts the frame clock if anything wants it. It runs only while something moves -
        /// rows easing, the bar, a spinner, the fade - and stops itself once all of it is
        /// still: a stuck row waiting on the user animates nothing, and the run's own tick
        /// repaints it when its state changes.
        /// </summary>
        void Kick()
        {
            if (IsDisposed) return;
            if (_frames == null)
            {
                _frames = new Timer();
                _frames.Interval = Motion.FrameMs;
                _frames.Tick += OnFrame;
            }
            if (_frames.Enabled) return;
            _lastFrame = Environment.TickCount;
            _frames.Start();
        }

        void OnFrame(object sender, EventArgs e)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try { Frame(); }
            finally { FrameTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0; }
        }

        /// <summary>Time spent in frames, in re-layout, and painting itself - for the perf log.</summary>
        public long FrameTicks, ArrangeTicks, PaintTicks;
        public int Arranges;

        void Frame()
        {
            int now = Environment.TickCount;
            int elapsed = unchecked(now - _lastFrame);
            if (elapsed <= 0) return;
            _lastFrame = now;

            bool spinning;
            bool moved = _list.Step(elapsed, out spinning);

            if (_phase == Phase.Running && SyncCounts()) Invalidate(new Rectangle(0, 0, ClientSize.Width, BarRect.Top));
            if (DonePending && _list.Finished) EnterDone();
            if (DonePending) spinning = true;   // keep the clock until the list has caught up

            float rate = _animate ? Motion.Rate(elapsed, BarMs) : 1f;
            bool bar = Motion.Ease(ref _bar, _barTarget, rate, 0.002f);
            if (bar) { _frameCount++; Invalidate(BarRect); }

            bool chrome = false;
            if (_animate && _top >= 0)
            {
                float r = Motion.Rate(elapsed, ChromeMs);
                chrome |= Motion.Ease(ref _top, ListTop, r, Motion.PixelEpsilon);
                chrome |= Motion.Ease(ref _below, BelowTarget, r, Motion.PixelEpsilon);
            }
            if (chrome || _list.DesiredHeight != _list.Height) _layoutDirty = true;

            // The window follows the list's eased height and its own, but not every frame: a
            // top-level resize costs several ms however it is done (see ArrangeNow), and a
            // window edge moving at 20 fps reads as smooth as at 60 while the rows inside it
            // keep the full rate. The last step is never skipped, so it always lands exactly.
            if (_layoutDirty)
            {
                _sinceArrange++;
                if (_sinceArrange >= 3 || (!moved && !chrome)) { _sinceArrange = 0; Arrange(); }
            }

            if (_fading)
            {
                double opacity = Opacity - (double)elapsed / FadeMs;
                if (opacity <= 0.02) { Finish(); return; }
                Opacity = opacity;
                _frameCount++;
                return;
            }

            if (!moved && !bar && !spinning && !chrome && !_layoutDirty) _frames.Stop();
        }

        void StopTimers()
        {
            if (_frames != null) { _frames.Stop(); _frames.Dispose(); _frames = null; }
            if (_hold != null) { _hold.Stop(); _hold.Dispose(); _hold = null; }
        }

        // --- layout and paint -------------------------------------------------------

        int TitleTop { get { return S(14); } }
        int SubTop { get { return TitleTop + S(22); } }

        int SubHeight
        {
            get { return _phase == Phase.Confirm ? _bodyHeight : S(18); }
        }

        int BarHeight { get { return Math.Max(2, S(2)); } }

        /// <summary>Just above the list, wherever its eased top is.</summary>
        Rectangle BarRect
        {
            get { return new Rectangle(_pad, (int)Math.Round(Top0) - S(10) - BarHeight, _inner, BarHeight); }
        }

        /// <summary>Where the list's top belongs in this phase.</summary>
        int ListTop
        {
            get
            {
                return _phase == Phase.Confirm
                    ? SubTop + SubHeight + S(12)
                    : SubTop + SubHeight + S(8) + BarHeight + S(10);
            }
        }

        /// <summary>Everything under the list: the hint's line, then the buttons, or a margin when done.</summary>
        int BelowTarget
        {
            get
            {
                int hint = HintLine ? S(8) + S(16) : 0;
                return _phase == Phase.Done ? hint + _pad - S(2) : hint + S(14) + S(28) + S(14);
            }
        }

        // The list's top and the space under it, eased like the list's own height, so a phase
        // change moves the window's edge rather than jumping it: the sentence giving way to
        // the count when Close is pressed, the buttons' row going when it is done. Negative
        // until the first layout, which lands them where they belong.
        float _top = -1f, _below = -1f;
        bool _layoutDirty;
        int _buttonsTop;

        float Top0 { get { return _top < 0 ? ListTop : _top; } }

        const int ChromeMs = 220;

        /// <summary>Whether the hint's line is kept: in remove mode while confirming, always while running.</summary>
        bool HintLine
        {
            get { return (_phase == Phase.Confirm && _remove) || _phase == Phase.Running; }
        }

        /// <summary>Title, the line under it, the bar, the list, the hint, the buttons - top to bottom.</summary>
        void Arrange()
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            Arranges++;
            try { ArrangeNow(); }
            finally { ArrangeTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0; }
        }

        void ArrangeNow()
        {
            // Cheap enough to run every frame a row grows or folds, which is what it does:
            // children moved with layout suspended, one SetWindowPos for the window's size
            // and place together, and only the part below the list repainted - the title and
            // the bar above it have not moved. The first version set ClientSize and then
            // Location, two top-level SetWindowPos calls, and repainted everything: about 9
            // ms a call, which was most of a close-all's CPU under --closeall.
            bool resized = false;
            int topBefore = _list.Top;
            SuspendLayout();
            try { resized = Lay(); }
            finally { ResumeLayout(false); }

            if (resized && IsHandleCreated && _list.Top == topBefore)
                Invalidate(new Rectangle(0, Math.Max(0, _list.Bottom), ClientSize.Width, ClientSize.Height));
            else
                Invalidate();
        }

        /// <summary>Positions everything. True when only the window's height changed.</summary>
        bool Lay()
        {
            if (_top < 0 || !_animate) { _top = ListTop; _below = BelowTarget; }
            else if (_top != ListTop || _below != BelowTarget) Kick();
            _layoutDirty = false;

            _list.Location = new Point(_pad, (int)Math.Round(_top));
            _list.Height = _list.DesiredHeight;

            // The buttons ride the bottom edge, which is the edge that stays put over the
            // strip, so a growing hint line or a shrinking sentence never moves them on screen.
            int height = _list.Bottom + (int)Math.Round(_below);
            _buttonsTop = height - S(14) - S(28);
            ArrangeButtons(_buttonsTop);

            var size = new Size(_inner + 2 * _pad, height);
            if (ClientSize == size) return false;

            // Borderless, so the client is the whole window.
            if (IsHandleCreated) Bounds = new Rectangle(PlaceFor(size), size);
            else ClientSize = size;
            return true;
        }

        /// <summary>
        /// Which buttons are meant to be showing, kept here rather than read back from
        /// Visible. Before the window is shown, WinForms answers Visible false for every
        /// child whatever it was set to, and the first layout runs in the constructor: it
        /// skipped every button, left them all at 0,0, and the confirmation opened with
        /// "Close all" drawn over its own title and nothing along the bottom. --closeall
        /// never saw it, since its windows are shown before they are laid out.
        /// </summary>
        readonly HashSet<Button> _hidden = new HashSet<Button>();

        void SetShown(Button button, bool shown)
        {
            if (shown)
            {
                // Placed before it is shown. A button never laid out sits at 0,0, and Force
                // all, shown on Close, flashed there over the title for a frame before the
                // layout that followed moved it - caught in a screen recording.
                _hidden.Remove(button);
                ArrangeButtons(_buttonsTop);
            }
            else _hidden.Add(button);
            button.Visible = shown;
        }

        /// <summary>Rows still shown as open, for --closeall's check that done means done.</summary>
        public int OpenRows { get { return _list.OpenCount; } }

        /// <summary>Where Cancel is, for --closeall's check that the layout ran.</summary>
        public Rectangle CancelBounds { get { return _cancel.Bounds; } }

        /// <summary>From the right: Cancel, then whichever of the others are showing.</summary>
        void ArrangeButtons(int top)
        {
            int right = _pad + _inner;
            Button[] order = { _cancel, _removeButton, _forceAll, _close };
            foreach (Button b in order)
            {
                if (b == null || _hidden.Contains(b)) continue;
                b.Location = new Point(right - b.Width, top);
                right = b.Left - S(8);
            }
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            Place();
        }

        /// <summary>ConfirmDialog's placement: by the strip, inside the work area.</summary>
        void Place()
        {
            Location = PlaceFor(Size);
        }

        Rectangle _work;

        Point PlaceFor(Size size)
        {
            Rectangle anchor = _anchor.IsEmpty
                ? new Rectangle(Cursor.Position, Size.Empty)
                : _anchor;

            // The work area once: the anchor does not move for the window's life, and
            // asking for the monitor every frame of a resize was part of what it cost.
            if (_work.IsEmpty) _work = Screen.FromRectangle(anchor).WorkingArea;
            bool accentAtTop;
            return TooltipWindow.Place(size, anchor, _work, S(4), out accentAtTop);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try { PaintNow(e); }
            finally { PaintTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0; }
        }

        void PaintNow(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;

            // The bar easing invalidates only itself, a few times a second for a second or
            // two; laying the title and the sentence out again for each of those is waste.
            if (_phase != Phase.Confirm && BarRect.Contains(e.ClipRectangle))
            {
                PaintBar(g);
                return;
            }

            using (var pen = new Pen(Palette.Lift(_background, 0.24f)))
                g.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);

            const TextFormatFlags Line = TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis
                                         | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine;

            bool light = Palette.IsLight(BackColor);
            Color good = light ? Palette.Blend(CloseList.Good, Color.Black, 0.32f) : CloseList.Good;
            Color warn = light ? Palette.Blend(CloseList.Waiting, Color.Black, 0.32f) : CloseList.Waiting;

            // Title, with the tick in front once it is done.
            int x = _pad;
            if (_phase == Phase.Done)
            {
                const string tick = "\u2713";
                int w = TextRenderer.MeasureText(g, tick, _titleFont, Size.Empty, TextFormatFlags.NoPadding).Width;
                TextRenderer.DrawText(g, tick, _titleFont, new Rectangle(x, TitleTop, w + S(2), S(20)), good, Line | TextFormatFlags.NoPadding);
                x += w + S(6);
            }
            TextRenderer.DrawText(g, _title, _titleFont,
                new Rectangle(x, TitleTop, _pad + _inner - x, S(20)), Palette.PrimaryText(_background), Line);

            // The line under it.
            Color body = Palette.BodyText(_background);
            if (_phase == Phase.Confirm)
            {
                TextRenderer.DrawText(g, _body, _bodyFont, new Rectangle(_pad, SubTop, _inner, _bodyHeight), body,
                                      TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            }
            else if (_phase == Phase.Running)
            {
                string counts = _closed + " of " + _total + " closed";
                TextRenderer.DrawText(g, counts, _bodyFont, new Rectangle(_pad, SubTop, _inner, S(18)), body, Line);
                if (_needYou > 0)
                {
                    int w = TextRenderer.MeasureText(g, counts, _bodyFont, Size.Empty, TextFormatFlags.NoPadding).Width;
                    string need = "\u00B7  " + _needYou + (_needYou == 1 ? " needs you" : " need you");
                    TextRenderer.DrawText(g, need, _bodyFont,
                        new Rectangle(_pad + w + S(10), SubTop, _inner - w - S(10), S(18)), warn, Line);
                }
            }
            else
            {
                TextRenderer.DrawText(g, _body, _bodyFont, new Rectangle(_pad, SubTop, _inner, S(18)), body, Line);
            }

            // The bar, once there is progress to show.
            if (_phase != Phase.Confirm) PaintBar(g);

            if (HintLine && _hint.Length > 0)
            {
                TextRenderer.DrawText(g, _hint, _bodyFont,
                    new Rectangle(_pad, _list.Bottom + S(8), _inner, S(16)), Palette.MutedText(_background), Line);
            }
        }

        void PaintBar(Graphics g)
        {
            Rectangle bar = BarRect;
            using (var track = new SolidBrush(Palette.Lift(_background, 0.18f)))
                g.FillRectangle(track, bar);
            int filled = (int)Math.Round(bar.Width * Math.Max(0f, Math.Min(1f, _bar)));
            if (filled <= 0) return;

            bool light = Palette.IsLight(BackColor);
            Color good = light ? Palette.Blend(CloseList.Good, Color.Black, 0.32f) : CloseList.Good;
            Color ink = _phase == Phase.Done ? good : _list.Accent;
            using (var fill = new SolidBrush(ink))
                g.FillRectangle(fill, new Rectangle(bar.Left, bar.Top, filled, bar.Height));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                StopTimers();
                _titleFont.Dispose();
                _bodyFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}

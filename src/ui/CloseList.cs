using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace DesktopSwitcher
{
    /// <summary>A button drawn on a close-all row.</summary>
    enum RowButton
    {
        None,
        Show,
        Force,
        AskAgain
    }

    /// <summary>
    /// One window's state in a running close-all, as CloseAllRun hands it to the window on
    /// every tick. Plain data: the list decides how each looks.
    /// </summary>
    sealed class CloseRow
    {
        public IntPtr Handle;
        public WindowEntry Entry;
        public CloseState State;

        /// <summary>Not pumping messages - IsHungAppWindow. Not a CloseState, so the
        /// states --closeall expects of a hung window are what they always were.</summary>
        public bool Hung;

        /// <summary>The prompt's own text, read by DialogText, or null.</summary>
        public string Question;

        public bool Forced;
        public bool CanForce;

        /// <summary>True when Show has something to bring up - false for a refused window,
        /// which is usually elevated and cannot be focused from here either.</summary>
        public bool CanShow;

        /// <summary>
        /// The order the run first saw it stuck in, 0 while it never has. The list puts
        /// stuck rows in this order, oldest first, so a row does not hop above another
        /// just because it changed from one kind of stuck to another.
        /// </summary>
        public int StuckSeq;
    }

    /// <summary>
    /// The list in CloseAllWindow: a checklist while the user decides, then each window's
    /// progress, in the same rows. Grew out of CloseSelectDialog's CheckList, and keeps its
    /// rules - the All row's tick logic, pinned rows greyed and unclickable, the focus ring
    /// only after the keyboard has been used, row-granular wheel scrolling past eight rows,
    /// and owner drawing throughout, for the reason given there: the stock checked lists
    /// paint their own light chrome over a dark taskbar's tones.
    ///
    /// What is new is that rows move. Once the run starts:
    ///
    ///   - the order is "what still needs you": stuck rows first, in the order they got
    ///     stuck, then the ones still closing, then the closed ones as they fold away, then
    ///     the windows left alone - kept by the user, or pinned. CloseProgressWindow kept
    ///     every row where it started, so the eye could follow one; but a desktop of twenty
    ///     with one save prompt then hid its only interesting row behind "+N more". Rows
    ///     slide into their new places rather than jumping, which keeps that row followable.
    ///   - a closed row shows its tick for a moment, then its height eases to nothing.
    ///   - a stuck row grows a second line - the question, or why it is held up - and the
    ///     buttons that deal with that one window, and flashes amber once as it arrives.
    ///
    /// Every row keeps a Y and a height that ease toward the targets the order gives, on
    /// the window's frame clock. The list's own height is eased the same way, and the
    /// window follows it, so a run that empties the list shrinks the window smoothly.
    /// </summary>
    sealed class CloseList : Control
    {
        sealed class Row
        {
            public WindowEntry Entry;
            public int Index;              // order in the list as first built - the tie-break
            public bool Pinned;
            public bool Ticked = true;

            /// <summary>Unticked when the run started: left alone, shown muted at the end.</summary>
            public bool Kept;

            /// <summary>The run's latest word on it. Null before the run, and for kept and pinned rows.</summary>
            public CloseRow Live;

            public float Y, YTarget, H, HTarget;
            public float Flash;            // 1 when it has just got stuck, easing to 0
            public bool WasStuck;
            public int ClosedAt;           // TickCount when it closed, for the tick's hold
            public bool Closed;

            public string ArmedLabel;      // Force asked once, waiting for the second click
            public int ArmedAt;

            // Where the buttons were last painted, for hit testing: in the row's own
            // coordinates as rendered, and in the list's as last drawn.
            public RowButton[] Buttons = new RowButton[0];
            public Rectangle[] LocalRects = new Rectangle[0];
            public Rectangle[] ButtonRects = new Rectangle[0];

            /// <summary>The row as last rendered, and what it was rendered from - see DrawRow.</summary>
            public Bitmap Image;
            public string ImageKey;
        }

        /// <summary>How long a closed row keeps its tick before it folds away.</summary>
        const int ClosedHoldMs = 260;

        /// <summary>How long an armed Force waits for its second click.</summary>
        public const int ArmMs = 4000;

        const int MaxVisibleRows = 8;
        const int LayoutMs = 220;
        const int FlashMs = 650;

        // The panel's only colours not derived from the taskbar: state, which has to read the
        // same over a light or a dark bar. Moved here from CloseProgressWindow.
        public static readonly Color Good = Color.FromArgb(0x5F, 0xC2, 0x6B);
        public static readonly Color Waiting = Color.FromArgb(0xE8, 0xA8, 0x2E);
        public static readonly Color Bad = Color.FromArgb(0xE0, 0x5A, 0x5A);

        const string MarkClosed = "\u2713";     // check mark
        const string MarkAsking = "!";
        const string MarkBad = "\u2715";        // multiplication x
        const string Dot = "  \u00B7  ";         // middle dot, spaced

        readonly List<Row> _rows = new List<Row>();
        readonly int _closeable;
        readonly bool _single;
        readonly Color _background;
        readonly Color _surface;
        readonly Color _accent;
        readonly Color _onAccent;
        readonly double _scale;
        readonly bool _animate;
        readonly Font _font;
        readonly Font _smallFont;
        readonly Font _markFont;
        readonly int _rowH;
        readonly int _tallH;

        bool _running;
        List<Row> _order;                  // the display order; rebuilt when it can change
        float _header;                     // the All row's height: eases to 0 when the run starts
        float _viewport, _viewportTarget;
        float _scroll;
        int _focus;                        // HeaderIndex or a row in _order
        int _focusButton;
        int _hot = NoRow;
        int _hotButton = -1;
        int _pressRow = NoRow, _pressButton = -1;
        bool _keyboard;
        bool _dragging;
        int _dragOffset;
        int _frames;
        int _renders;
        bool _spinPhase;

        const int HeaderIndex = -1;
        const int NoRow = -2;

        /// <summary>The ticks changed. Confirm phase only.</summary>
        public event EventHandler Changed;

        /// <summary>A row's button was pressed: Show, Force (with whether it was armed), Ask again.</summary>
        public event Action<IntPtr, RowButton, bool> ButtonPressed;

        /// <summary>Its target height changed; the window lays itself out again.</summary>
        public event EventHandler HeightChanging;

        public CloseList(IList<WindowEntry> windows, IList<WindowEntry> pinned, bool single,
                         Color background, Color accent, double scale, Font font, bool animate)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                   | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
                   | ControlStyles.Selectable, true);
            TabStop = true;

            _single = single;
            _closeable = windows.Count;
            for (int i = 0; i < windows.Count; i++)
                _rows.Add(new Row { Entry = windows[i], Index = i });
            if (pinned != null)
                for (int i = 0; i < pinned.Count; i++)
                    _rows.Add(new Row { Entry = pinned[i], Index = _closeable + i, Pinned = true, Ticked = false });

            _background = background;
            _surface = Palette.Lift(background, 0.06f);
            _accent = accent;
            _onAccent = Palette.IsLight(accent) ? Color.FromArgb(20, 20, 20) : Color.White;
            _scale = scale;
            _animate = animate;
            _font = font;
            _smallFont = new Font("Segoe UI", (float)(11 * scale), FontStyle.Regular, GraphicsUnit.Pixel);
            _markFont = new Font("Segoe UI", (float)(13 * scale), FontStyle.Bold, GraphicsUnit.Pixel);
            _rowH = S(26);
            _tallH = S(42);
            _header = single ? 0 : _rowH + 1;
            _focus = single ? NoRow : HeaderIndex;

            BackColor = _surface;
            Reorder();
            Snap();
            Height = DesiredHeight;
        }

        int S(int value)
        {
            return (int)Math.Round(value * _scale);
        }

        // --- what the window reads ------------------------------------------------

        public int TickedCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < _closeable; i++) if (_rows[i].Ticked) n++;
                return n;
            }
        }

        public HashSet<IntPtr> TickedHandles()
        {
            var set = new HashSet<IntPtr>();
            for (int i = 0; i < _closeable; i++)
                if (_rows[i].Ticked) set.Add(_rows[i].Entry.Handle);
            return set;
        }

        /// <summary>Rules above and below, the header, and the rows in view - as eased.</summary>
        public int DesiredHeight
        {
            get { return 1 + (int)Math.Round(_header) + (int)Math.Round(_viewport) + 1; }
        }

        /// <summary>Rows of the run not yet shown closed - for --closeall, zero once it is done.</summary>
        public int OpenCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < _rows.Count; i++)
                    if (_rows[i].Live != null && !_rows[i].Closed) n++;
                return n;
            }
        }

        /// <summary>Rows left alone because they were unticked when the run started.</summary>
        public int KeptCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < _rows.Count; i++) if (_rows[i].Kept) n++;
                return n;
            }
        }

        /// <summary>The highlight colour the boxes and spinners use; the window's bar uses it too.</summary>
        public Color Accent { get { return _accent; } }

        /// <summary>Frames drawn, for the perf log.</summary>
        public int Frames { get { return _frames; } }

        /// <summary>Row images rendered, for the perf log: what the cache did not save.</summary>
        public int Renders { get { return _renders; } }

        // --- the run ------------------------------------------------------------------

        /// <summary>
        /// The run has started: ticked rows become live, unticked ones are kept. The rows
        /// stay where they are; Update, on the run's first tick, sorts them.
        /// </summary>
        public void BeginRunning(ICollection<IntPtr> asked)
        {
            _running = true;
            int now = Environment.TickCount;
            for (int i = 0; i < _rows.Count; i++)
            {
                Row r = _rows[i];
                if (r.Pinned || asked == null || asked.Contains(r.Entry.Handle)) continue;

                // Unticked: kept, and shown so. Ticked but not asked: it closed or left the
                // desktop while the question was up (CloseSelection takes the list again),
                // so there is nothing to keep - it folds away like any closed row, without
                // a tick it did not earn here.
                if (!r.Ticked) r.Kept = true;
                else
                {
                    r.Closed = true;
                    r.ClosedAt = unchecked(now - ClosedHoldMs);
                }
            }
            _focus = NoRow;
            _focusButton = 0;
            Reorder();
            Invalidate();
        }

        /// <summary>
        /// The run's rows, every tick. A handle the list has not seen - a window that opened
        /// mid-run, which the run's sweep picked up - gets a row that grows in from nothing.
        /// </summary>
        public void Update(IList<CloseRow> live)
        {
            var byHandle = new Dictionary<IntPtr, Row>();
            for (int i = 0; i < _rows.Count; i++)
                if (!_rows[i].Pinned) byHandle[_rows[i].Entry.Handle] = _rows[i];

            int now = Environment.TickCount;
            bool changed = false;
            for (int i = 0; i < live.Count; i++)
            {
                CloseRow c = live[i];
                Row r;
                if (!byHandle.TryGetValue(c.Handle, out r))
                {
                    r = new Row { Entry = c.Entry, Index = _rows.Count };
                    _rows.Add(r);
                    r.Y = r.YTarget = ContentTarget();
                    r.H = 0;
                }
                if (r.Kept || !Same(r.Live, c)) changed = true;
                r.Kept = false;
                r.Live = c;

                bool stuck = IsStuck(c);
                if (stuck && !r.WasStuck) r.Flash = _animate ? 1f : 0f;
                r.WasStuck = stuck;

                if (c.State == CloseState.Closed && !r.Closed)
                {
                    r.Closed = true;
                    r.ClosedAt = now;
                    r.ArmedLabel = null;
                }

                // An armed Force lapses: the window is no longer stuck, the time is up.
                if (r.ArmedLabel != null && (!stuck || unchecked(now - r.ArmedAt) > ArmMs))
                {
                    r.ArmedLabel = null;
                    changed = true;
                }
            }

            // The run ticks every 150 ms whether or not anything happened, and a list with a
            // window sitting on its save prompt would otherwise be repainted - every row's
            // text laid out again - six times a second for as long as the user takes.
            if (!changed) return;
            Reorder();
            Invalidate();
        }

        /// <summary>Whether a tick's row says anything the last one did not.</summary>
        static bool Same(CloseRow a, CloseRow b)
        {
            if (a == null || b == null) return a == b;
            return a.State == b.State && a.Hung == b.Hung && a.Question == b.Question
                && a.Forced == b.Forced && a.CanForce == b.CanForce && a.CanShow == b.CanShow
                && a.StuckSeq == b.StuckSeq;
        }

        /// <summary>The run decided a Force needs a second click: the row's Force says so.</summary>
        public void Arm(IntPtr handle, string label)
        {
            for (int i = 0; i < _rows.Count; i++)
            {
                if (_rows[i].Entry.Handle != handle) continue;
                _rows[i].ArmedLabel = label;
                _rows[i].ArmedAt = Environment.TickCount;
                Invalidate();
                return;
            }
        }

        static bool IsStuck(CloseRow c)
        {
            if (c == null || c.State == CloseState.Closed) return false;
            return c.Hung || c.State == CloseState.Prompting || c.State == CloseState.StillOpen
                || c.State == CloseState.KeptOpen || c.State == CloseState.Refused;
        }

        /// <summary>
        /// Where a row sorts while running. Lower first: what needs the user, then what is
        /// still going, then what is done, then what was never part of it.
        /// </summary>
        static int Rank(Row r)
        {
            if (r.Pinned) return 5;
            if (r.Kept) return 4;
            if (r.Closed) return 3;
            if (r.Live == null) return 1;
            if (IsStuck(r.Live)) return 0;
            return 1;
        }

        void Reorder()
        {
            var order = new List<Row>(_rows);
            if (_running)
            {
                order.Sort(delegate(Row a, Row b)
                {
                    int d = Rank(a) - Rank(b);
                    if (d != 0) return d;
                    if (Rank(a) == 0)
                    {
                        int sa = a.Live.StuckSeq, sb = b.Live.StuckSeq;
                        if (sa != sb) return sa - sb;
                    }
                    return a.Index - b.Index;
                });
            }
            _order = order;

            int now = Environment.TickCount;
            float y = 0;
            for (int i = 0; i < order.Count; i++)
            {
                Row r = order[i];
                r.HTarget = TargetHeight(r, now);
                r.YTarget = y;
                y += r.HTarget;
            }

            float before = _viewportTarget;
            _viewportTarget = Math.Min(y, MaxVisibleRows * _rowH);
            ClampScroll(y);
            if (before != _viewportTarget && HeightChanging != null) HeightChanging(this, EventArgs.Empty);
        }

        float TargetHeight(Row r, int now)
        {
            if (!_running) return _rowH;
            if (r.Closed) return unchecked(now - r.ClosedAt) >= ClosedHoldMs ? 0 : _rowH;
            if (r.Live == null) return _rowH;
            return IsStuck(r.Live) ? _tallH : _rowH;
        }

        float ContentTarget()
        {
            float y = 0;
            for (int i = 0; i < _rows.Count; i++) y += _rows[i].HTarget;
            return y;
        }

        void ClampScroll(float content)
        {
            float max = Math.Max(0, content - _viewportTarget);
            if (_scroll > max) _scroll = max;
            if (_scroll < 0) _scroll = 0;
        }

        void Snap()
        {
            for (int i = 0; i < _rows.Count; i++)
            {
                _rows[i].Y = _rows[i].YTarget;
                _rows[i].H = _rows[i].HTarget;
            }
            _viewport = _viewportTarget;
        }

        // --- animation --------------------------------------------------------------

        /// <summary>
        /// One frame. True while anything other than a spinner is moving - the window's cue
        /// to keep its timer and to lay itself out again. Spinners are reported apart:
        /// a list with only a spinner turning repaints only the spinner.
        /// </summary>
        public bool Step(int elapsedMs, out bool spinning)
        {
            spinning = false;
            int now = Environment.TickCount;
            bool moved = false;

            // A closed row's hold runs out by the clock, not by an Update, so look here too.
            bool reorder = false;
            for (int i = 0; i < _rows.Count; i++)
            {
                Row r = _rows[i];
                if (r.Closed && r.HTarget > 0 && unchecked(now - r.ClosedAt) >= ClosedHoldMs) reorder = true;
                if (r.Live != null && !r.Closed && !IsStuck(r.Live)) spinning = true;
            }
            if (reorder) Reorder();

            if (!_animate)
            {
                bool any = _viewport != _viewportTarget || (_running && _header != 0);
                for (int i = 0; i < _rows.Count && !any; i++)
                    any = _rows[i].Y != _rows[i].YTarget || _rows[i].H != _rows[i].HTarget || _rows[i].Flash != 0;
                if (_running) _header = 0;
                for (int i = 0; i < _rows.Count; i++) _rows[i].Flash = 0;
                Snap();
                if (any) Invalidate();
                return any;
            }

            float rate = Motion.Rate(elapsedMs, LayoutMs);
            float flash = Motion.Rate(elapsedMs, FlashMs);
            for (int i = 0; i < _rows.Count; i++)
            {
                Row r = _rows[i];
                moved |= Motion.Ease(ref r.Y, r.YTarget, rate, Motion.PixelEpsilon);
                moved |= Motion.Ease(ref r.H, r.HTarget, rate, Motion.PixelEpsilon);
                moved |= Motion.Ease(ref r.Flash, 0f, flash, Motion.ToneEpsilon);

                // Folded away for good: its image will not be drawn again.
                if (r.Closed && r.H == 0 && r.HTarget == 0 && r.Image != null)
                {
                    r.Image.Dispose();
                    r.Image = null;
                }
            }
            moved |= Motion.Ease(ref _viewport, _viewportTarget, rate, Motion.PixelEpsilon);
            if (_running) moved |= Motion.Ease(ref _header, 0f, rate, Motion.PixelEpsilon);

            if (moved) { _frames++; Invalidate(); }
            else if (spinning && (_spinPhase = !_spinPhase))
            {
                // Every other frame: a turning arc at 30 fps reads exactly as smoothly, and
                // only the arcs are repainted - see OnPaint's skip.
                _frames++;
                for (int i = 0; i < _rows.Count; i++)
                {
                    Row r = _rows[i];
                    if (r.Live != null && !r.Closed && !IsStuck(r.Live)) Invalidate(LeadRect(RowRect(r)));
                }
            }
            return moved;
        }

        // --- geometry ---------------------------------------------------------------

        int RowsTop { get { return 1 + (int)Math.Round(_header); } }

        int ViewportHeight { get { return (int)Math.Round(_viewport); } }

        bool Scrolls { get { return ContentTarget() > _viewportTarget + 0.5f; } }

        Rectangle HeaderRect { get { return new Rectangle(0, 1, Width, (int)Math.Round(_header) - 1); } }

        Rectangle RowRect(Row r)
        {
            int top = RowsTop + (int)Math.Round(r.Y - _scroll);
            return new Rectangle(0, top, Width - (Scrolls ? S(10) : 0), Math.Max(0, (int)Math.Round(r.H)));
        }

        Rectangle LeadRect(Rectangle row)
        {
            int size = S(14);
            return new Rectangle(row.Left + S(6), row.Top + (Math.Min(row.Height, _rowH) - size) / 2, size, size);
        }

        Rectangle TrackRect
        {
            get { return new Rectangle(Width - S(8), RowsTop + S(2), S(4), Math.Max(0, ViewportHeight - S(4))); }
        }

        Rectangle ThumbRect
        {
            get
            {
                Rectangle track = TrackRect;
                float content = Math.Max(1f, ContentTarget());
                int h = Math.Max(S(16), (int)(track.Height * _viewportTarget / content));
                float range = content - _viewportTarget;
                int y = track.Top + (range <= 0 ? 0 : (int)((track.Height - h) * _scroll / range));
                return new Rectangle(track.Left, y, track.Width, h);
            }
        }

        /// <summary>A row index into _order, HeaderIndex, or NoRow.</summary>
        int HitTest(Point p)
        {
            if (!_running && !_single && HeaderRect.Contains(p)) return HeaderIndex;
            if (p.Y < RowsTop || p.Y >= RowsTop + ViewportHeight) return NoRow;
            if (Scrolls && p.X >= Width - S(10)) return NoRow;
            for (int i = 0; i < _order.Count; i++)
            {
                Rectangle r = RowRect(_order[i]);
                if (r.Height > 0 && p.Y >= r.Top && p.Y < r.Bottom) return i;
            }
            return NoRow;
        }

        int ButtonAt(int row, Point p)
        {
            if (row < 0) return -1;
            Row r = _order[row];
            for (int i = 0; i < r.ButtonRects.Length; i++)
                if (r.ButtonRects[i].Contains(p)) return i;
            return -1;
        }

        bool Toggleable(int index)
        {
            if (_running || _single) return false;
            if (index == HeaderIndex) return true;
            return index >= 0 && !_order[index].Pinned;
        }

        void Toggle(int index)
        {
            if (index == HeaderIndex)
            {
                // Anything unticked ticks everything; only a full list clears. The same rule
                // Explorer's own select-all box follows, and the one that makes a stray click
                // on a partial list the harmless direction.
                bool all = TickedCount == _closeable;
                for (int i = 0; i < _closeable; i++) _rows[i].Ticked = !all;
            }
            else if (Toggleable(index))
            {
                _order[index].Ticked = !_order[index].Ticked;
            }
            else return;

            Invalidate();
            if (Changed != null) Changed(this, EventArgs.Empty);
        }

        void ScrollBy(float delta)
        {
            float before = _scroll;
            _scroll += delta;
            ClampScroll(ContentTarget());
            if (_scroll != before) Invalidate();
        }

        void EnsureVisible(int index)
        {
            if (index < 0) return;
            Row r = _order[index];
            if (r.YTarget < _scroll) ScrollBy(r.YTarget - _scroll);
            else if (r.YTarget + r.HTarget > _scroll + _viewportTarget)
                ScrollBy(r.YTarget + r.HTarget - _scroll - _viewportTarget);
        }

        /// <summary>Rows the keyboard stops at: tickable ones in confirm, ones with buttons running.</summary>
        bool Focusable(int index)
        {
            if (index == HeaderIndex) return !_running && !_single;
            if (index < 0 || index >= _order.Count) return false;
            Row r = _order[index];
            if (!_running) return !r.Pinned && !_single;
            return Buttons(r).Length > 0;
        }

        // --- input ----------------------------------------------------------------

        protected override bool IsInputKey(Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Up:
                case Keys.Down:
                case Keys.Left:
                case Keys.Right:
                case Keys.Home:
                case Keys.End:
                    return true;
            }
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            switch (e.KeyCode)
            {
                case Keys.Space:
                case Keys.Enter:
                    if (e.KeyCode == Keys.Enter && !_running) { base.OnKeyDown(e); return; }
                    _keyboard = true;
                    if (_running) PressFocused();
                    else Toggle(_focus);
                    e.Handled = true;
                    return;
                case Keys.Up:   MoveFocus(-1); break;
                case Keys.Down: MoveFocus(1); break;
                case Keys.Home: _focus = _running || _single ? NoRow : HeaderIndex; MoveFocus(_focus == NoRow ? 1 : 0); break;
                case Keys.End:  _focus = _order.Count; MoveFocus(-1); break;
                case Keys.Left:
                case Keys.Right:
                    if (!_running || _focus < 0) { base.OnKeyDown(e); return; }
                    int count = _order[_focus].ButtonRects.Length;
                    if (count > 0)
                        _focusButton = Math.Max(0, Math.Min(count - 1, _focusButton + (e.KeyCode == Keys.Left ? -1 : 1)));
                    break;
                default:
                    base.OnKeyDown(e);
                    return;
            }
            _keyboard = true;
            Invalidate();
            e.Handled = true;
        }

        void MoveFocus(int step)
        {
            int i = _focus;
            int low = _running || _single ? 0 : HeaderIndex;
            for (int n = 0; n <= _order.Count + 1; n++)
            {
                i += step == 0 ? 0 : step;
                if (i < low || i >= _order.Count) return;
                if (Focusable(i)) { _focus = i; _focusButton = 0; EnsureVisible(i); return; }
                if (step == 0) return;
            }
        }

        void PressFocused()
        {
            if (_focus < 0 || _focus >= _order.Count) return;
            Row r = _order[_focus];
            RowButton[] buttons = Buttons(r);
            if (buttons.Length == 0) return;
            Press(r, buttons[Math.Min(_focusButton, buttons.Length - 1)]);
        }

        void Press(Row r, RowButton button)
        {
            bool armed = button == RowButton.Force && r.ArmedLabel != null;
            if (armed) r.ArmedLabel = null;
            Action<IntPtr, RowButton, bool> handler = ButtonPressed;
            if (handler != null) handler(r.Entry.Handle, button, armed);
            Invalidate();
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            if (_running && !Focusable(_focus)) { _focus = -1; MoveFocus(1); }
            Invalidate();
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            _keyboard = false;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            Focus();
            _keyboard = false;

            if (Scrolls && e.X >= Width - S(10))
            {
                Rectangle thumb = ThumbRect;
                if (e.Y >= thumb.Top && e.Y < thumb.Bottom)
                {
                    _dragging = true;
                    _dragOffset = e.Y - thumb.Top;
                }
                else
                {
                    // A click on the track pages toward it, as a system scrollbar does.
                    ScrollBy(e.Y < thumb.Top ? -_viewportTarget : _viewportTarget);
                }
                return;
            }

            int hit = HitTest(e.Location);
            if (_running)
            {
                int b = ButtonAt(hit, e.Location);
                _pressRow = b >= 0 ? hit : NoRow;
                _pressButton = b;
                if (b >= 0) { _focus = hit; _focusButton = b; Invalidate(); }
                return;
            }

            if (Toggleable(hit))
            {
                _focus = hit;
                Toggle(hit);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            if (_dragging)
            {
                Rectangle track = TrackRect;
                Rectangle thumb = ThumbRect;
                int room = track.Height - thumb.Height;
                float range = ContentTarget() - _viewportTarget;
                if (room > 0)
                {
                    float y = e.Y - _dragOffset - track.Top;
                    ScrollBy(y * range / room - _scroll);
                }
                return;
            }

            int hit = HitTest(e.Location);
            int button = _running ? ButtonAt(hit, e.Location) : -1;
            if (!_running && !Toggleable(hit)) hit = NoRow;   // pinned rows do not light up: nothing to click
            if (_running && button < 0) hit = NoRow;

            if (hit != _hot || button != _hotButton)
            {
                // Leaving a row lets its armed Force go: the second click has to be a
                // deliberate one on the same button, not one that wandered back later.
                if (_hot >= 0 && _hot < _order.Count && _hot != hit) Disarm(_order[_hot]);
                _hot = hit;
                _hotButton = button;
                Invalidate();
            }
        }

        void Disarm(Row r)
        {
            if (r.ArmedLabel == null) return;
            // Not the moment the pointer steps off the button onto its own row's text -
            // only once it leaves the row altogether, which is what the hot row tracks.
            r.ArmedLabel = null;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _dragging = false;
            if (!_running || _pressRow < 0) return;

            int hit = HitTest(e.Location);
            int b = ButtonAt(hit, e.Location);
            int row = _pressRow, pressed = _pressButton;
            _pressRow = NoRow;
            _pressButton = -1;
            if (hit == row && b == pressed && row < _order.Count)
            {
                Row r = _order[row];
                if (b < r.Buttons.Length) Press(r, r.Buttons[b]);
            }
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hot >= 0 && _hot < _order.Count) Disarm(_order[_hot]);
            if (_hot != NoRow || _hotButton >= 0)
            {
                _hot = NoRow;
                _hotButton = -1;
                Invalidate();
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (!Scrolls) return;
            ScrollBy(-e.Delta / 120f * 3 * _rowH);
            OnMouseMove(new MouseEventArgs(MouseButtons.None, 0, e.X, e.Y, 0));
        }

        // --- what each row offers ----------------------------------------------------

        static RowButton[] Buttons(Row r)
        {
            CloseRow c = r.Live;
            if (c == null || !IsStuck(c)) return new RowButton[0];

            var list = new List<RowButton>(3);
            bool forceable = c.CanForce && !(c.State == CloseState.Refused && c.Forced);

            // A hung window cannot be shown usefully - it would come up white and frozen -
            // and a refused one is usually elevated, which Show cannot focus either.
            if (!c.Hung && c.CanShow && c.State != CloseState.Refused) list.Add(RowButton.Show);
            if (!c.Hung && c.State == CloseState.KeptOpen) list.Add(RowButton.AskAgain);
            if (forceable) list.Add(RowButton.Force);
            return list.ToArray();
        }

        static string Caption(Row r, RowButton b)
        {
            switch (b)
            {
                case RowButton.Show: return "Show";
                case RowButton.AskAgain: return "Ask again";
                case RowButton.Force: return r.ArmedLabel ?? "Force";
            }
            return "";
        }

        /// <summary>The second line of a stuck row, and the colour it is in.</summary>
        string Note(CloseRow c, out Color ink)
        {
            bool light = Palette.IsLight(_surface);
            Color warn = light ? Palette.Blend(Waiting, Color.Black, 0.32f) : Waiting;
            Color bad = light ? Palette.Blend(Bad, Color.Black, 0.25f) : Bad;

            ink = Palette.SecondaryText(_background);
            string tail = c.CanForce ? "" : " It can't be forced from here.";

            if (c.Hung) { ink = bad; return "Not responding." + tail; }

            switch (c.State)
            {
                case CloseState.Prompting:
                    ink = warn;
                    return (c.Question != null ? "Asks: " + c.Question : "Has a dialog open.") + tail;
                case CloseState.StillOpen:
                    return "Still open. Nothing to read, so it may be asking inside the window." + tail;
                case CloseState.KeptOpen:
                    return "Kept open - its question was answered Cancel." + tail;
                case CloseState.Refused:
                    ink = bad;
                    return c.Forced
                        ? "Survived Force - probably running as administrator."
                        : "Refused to be asked - probably running as administrator." + tail;
            }
            return "";
        }

        // --- paint ----------------------------------------------------------------

        /// <summary>Time spent painting, for the perf log.</summary>
        public long PaintTicks;

        protected override void OnPaint(PaintEventArgs e)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try { PaintNow(e); }
            finally { PaintTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0; }
        }

        void PaintNow(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(_surface);

            Color rule = Palette.Lift(_background, 0.14f);
            using (var pen = new Pen(rule))
            {
                g.DrawLine(pen, 0, 0, Width - 1, 0);
                if (_header >= 1) g.DrawLine(pen, 0, RowsTop - 1, Width - 1, RowsTop - 1);
                g.DrawLine(pen, 0, Height - 1, Width - 1, Height - 1);
            }

            Rectangle dirty = e.ClipRectangle;

            if (_header >= 2 && dirty.IntersectsWith(HeaderRect))
            {
                Region outer = g.Clip;
                g.SetClip(HeaderRect, CombineMode.Intersect);
                PaintHeader(g);
                g.Clip = outer;
            }

            Region clip = g.Clip;
            var view = new Rectangle(0, RowsTop, Width, ViewportHeight);
            for (int i = 0; i < _order.Count; i++)
            {
                Row r = _order[i];
                Rectangle rect = RowRect(r);
                if (rect.Height <= 0 || rect.Bottom <= view.Top || rect.Top >= view.Bottom)
                {
                    r.ButtonRects = new Rectangle[0];
                    r.Buttons = new RowButton[0];
                    continue;
                }
                Rectangle visible = Rectangle.Intersect(rect, view);

                // A spinner frame invalidates a 14 px square per closing row. Clipping alone
                // still runs every row's text layout and icon draw before discarding the
                // pixels, and that was most of a running close-all's CPU. Rows outside the
                // dirty area keep the button rectangles they were last painted with, which
                // are still where they are - nothing moved, or they would be dirty.
                if (!dirty.IntersectsWith(visible)) continue;

                g.SetClip(visible);
                DrawRow(g, i, r, rect);
            }
            g.Clip = clip;

            if (Scrolls)
            {
                using (var brush = new SolidBrush(Palette.Lift(_background, _dragging ? 0.34f : 0.24f)))
                    g.FillRectangle(brush, ThumbRect);
            }
        }

        void PaintHeader(Graphics g)
        {
            Rectangle r = new Rectangle(0, 1, Width, _rowH);
            if (_hot == HeaderIndex) Fill(g, r, Palette.Lift(_background, 0.12f));

            int ticked = TickedCount;
            int pad = S(6);
            Rectangle box = LeadRect(r);
            if (ticked == _closeable) DrawTicked(g, box);
            else if (ticked == 0) DrawEmpty(g, box, false);
            else DrawMixed(g, box);

            int x = box.Right + S(8);
            string count = ticked + " of " + _closeable;
            int countWidth = (int)Math.Ceiling(g.MeasureString(count, _font).Width);

            Write(g, "All", _font, Palette.SecondaryText(_background),
                 new Rectangle(x, r.Top, r.Right - x - countWidth - pad, r.Height), false);
            Write(g, count, _font, Palette.MutedText(_background),
                 new Rectangle(r.Right - pad - countWidth, r.Top, countWidth, r.Height), false);

            if (_keyboard && Focused && _focus == HeaderIndex) Ring(g, r);
        }

        /// <summary>
        /// One row, from its cached image. Rendering a row - GDI+ text laid out and measured
        /// four or five times, an icon scaled, buttons sized - costs a few milliseconds, and
        /// while rows slide and fold every row is drawn every frame. The first build did all
        /// of it every frame and a close-all cost 16% of a core against the old panel's 7%,
        /// measured scenario by scenario under --closeall. So a row is rendered once into a
        /// bitmap, again only when something it shows changes - the key below - and a frame
        /// is a copy of each bitmap at its eased place.
        ///
        /// What changes every frame is drawn on top: the spinner, by the clock, and the amber
        /// flash of a row that has just got stuck, as a fading overlay. The image is always
        /// the row's full height; a row easing open or shut is the same image clipped.
        /// </summary>
        void DrawRow(Graphics g, int index, Row row, Rectangle rect)
        {
            int full = _running && IsStuck(row.Live) ? _tallH : _rowH;
            string key = ImageKey(index, row, rect.Width, full);
            if (row.Image == null || row.ImageKey != key)
            {
                if (row.Image == null || row.Image.Width != rect.Width || row.Image.Height != full)
                {
                    if (row.Image != null) row.Image.Dispose();
                    row.Image = new Bitmap(rect.Width, full, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                }
                using (Graphics rg = Graphics.FromImage(row.Image))
                {
                    // Opaque first, or ClearType has nothing to blend against.
                    rg.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                    rg.Clear(_surface);
                    PaintRow(rg, index, new Rectangle(0, 0, rect.Width, full));
                }
                row.ImageKey = key;
                _renders++;
            }

            g.DrawImageUnscaled(row.Image, rect.X, rect.Y);

            if (_running && row.Live != null && !row.Closed && !IsStuck(row.Live))
                DrawSpinner(g, LeadRect(rect));

            if (row.Flash > 0 && _running && IsStuck(row.Live))
            {
                Color edge = row.Live.Hung || row.Live.State == CloseState.Refused ? Bad : Waiting;
                using (var flash = new SolidBrush(Color.FromArgb((int)(255 * 0.30f * row.Flash), edge)))
                    g.FillRectangle(flash, rect);
            }

            var placed = new Rectangle[row.LocalRects.Length];
            for (int i = 0; i < placed.Length; i++)
            {
                Rectangle b = row.LocalRects[i];
                placed[i] = new Rectangle(b.X + rect.X, b.Y + rect.Y, b.Width, b.Height);
            }
            row.ButtonRects = placed;
        }

        /// <summary>Everything a row's image depends on. Equal keys, identical pixels.</summary>
        string ImageKey(int index, Row row, int width, int full)
        {
            CloseRow c = row.Live;
            return string.Concat(new object[]
            {
                width, "/", full, "/", _running, _single, row.Ticked, row.Kept, row.Closed, row.Pinned, "/",
                c == null ? "-" : c.State + "," + c.Hung + "," + c.Question + "," + c.Forced + "," + c.CanForce + "," + c.CanShow,
                "/", row.ArmedLabel,
                "/", _hot == index ? _hotButton : -9,
                "/", _pressRow == index ? _pressButton : -9,
                "/", _keyboard && Focused && _focus == index ? _focusButton : -9
            });
        }

        /// <summary>Renders, for the image cache. Everything but the spinner and the flash.</summary>
        void PaintRow(Graphics g, int index, Rectangle r)
        {
            Row row = _order[index];
            WindowEntry w = row.Entry;
            CloseRow c = row.Live;
            bool stuck = _running && IsStuck(c);
            bool bad = stuck && (c.Hung || c.State == CloseState.Refused);
            bool muted = row.Pinned || row.Kept || (!_running && !row.Ticked) || row.Closed;

            // The row's own band: the first line is always _rowH tall; a stuck row's note
            // sits under it.
            var line = new Rectangle(r.Left, r.Top, r.Width, _rowH);

            if (stuck)
            {
                Color edge = bad ? Bad : Waiting;
                Color tint = Palette.Blend(_surface, edge, Palette.IsLight(_surface) ? 0.10f : 0.07f);
                Fill(g, r, tint);
                Fill(g, new Rectangle(r.Left, r.Top, S(2), r.Height), edge);
            }
            else if (!_running && _hot == index) Fill(g, r, Palette.Lift(_background, 0.12f));

            // Lead: the box before the run, the state after.
            Rectangle lead = LeadRect(r);
            if (!_running)
            {
                if (_single) { }
                else if (row.Pinned) DrawEmpty(g, lead, true);
                else if (row.Ticked) DrawTicked(g, lead);
                else DrawEmpty(g, lead, false);
            }
            else if (c != null)
            {
                if (row.Closed) Mark(g, MarkClosed, Good, lead);
                else if (bad) Mark(g, MarkBad, Bad, lead);
                else if (stuck) Mark(g, MarkAsking, Waiting, lead);
                // Closing: the spinner is drawn over the image by DrawRow, by the clock.
            }

            int icon = S(16);
            var iconRect = new Rectangle(lead.Right + S(8), line.Top + (line.Height - icon) / 2, icon, icon);
            Bitmap bitmap = AppIcons.Get(w, icon);   // the cache's; never disposed here
            if (bitmap != null)
            {
                InterpolationMode mode = g.InterpolationMode;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                if (muted)
                {
                    using (var attributes = Faded(0.45f))
                        g.DrawImage(bitmap, iconRect, 0, 0, bitmap.Width, bitmap.Height, GraphicsUnit.Pixel, attributes);
                }
                else g.DrawImage(bitmap, iconRect);
                g.InterpolationMode = mode;
            }
            else DrawLetterTile(g, iconRect, w.App);

            int pad = S(6);
            int x = iconRect.Right + S(8);
            int right = r.Right - pad;

            // Buttons, right to left, vertically centred on the whole row.
            RowButton[] buttons = _running ? Buttons(row) : new RowButton[0];
            var rects = new Rectangle[buttons.Length];
            int bh = S(22);
            int by = r.Top + (r.Height - bh) / 2;
            for (int i = buttons.Length - 1; i >= 0; i--)
            {
                string caption = Caption(row, buttons[i]);
                int bw = (int)Math.Ceiling(g.MeasureString(caption, _smallFont).Width) + S(16);
                rects[i] = new Rectangle(right - bw, by, bw, bh);
                right -= bw + S(4);
            }
            if (buttons.Length > 0) right -= S(4);
            row.Buttons = buttons;
            row.LocalRects = rects;

            // A tag where there are no buttons: why a quiet row is quiet.
            string tag = null;
            if (row.Pinned) tag = _running ? "pinned, left alone" : "pinned, stays";
            else if (row.Kept) tag = "kept";
            else if (row.Closed && c != null && c.Forced) tag = "forced";
            if (tag != null)
            {
                int tagWidth = (int)Math.Ceiling(g.MeasureString(tag, _smallFont).Width);
                Write(g, tag, _smallFont, Palette.MutedText(_background),
                     new Rectangle(right - tagWidth, line.Top, tagWidth, line.Height), false);
                right -= tagWidth + S(8);
            }

            // Faded, not struck through: the box already says which way it is, and a line
            // through every kept title made the rows the user chose to keep the hardest ones
            // in the list to read.
            Color appInk = muted ? Palette.MutedText(_background) : Palette.BodyText(_background);
            Color titleInk = muted ? Palette.MutedText(_background) : Palette.SecondaryText(_background);

            string app = w.App ?? string.Empty;
            string title = w.Title ?? string.Empty;
            if (app.Length == 0) { app = title; title = string.Empty; }
            else if (title == app) title = string.Empty;

            // The app name gets what it needs up to half the room, the title the rest. Names
            // are short and say most; a title is often a long path whose tail matters least,
            // and the ellipsis takes that.
            int room = Math.Max(0, right - x);
            int appWidth = Math.Min(room / 2, (int)Math.Ceiling(g.MeasureString(app, _font).Width));
            if (title.Length == 0) appWidth = room;
            Write(g, app, _font, appInk, new Rectangle(x, line.Top, appWidth, line.Height), true);

            if (title.Length > 0)
            {
                int tx = x + appWidth + S(10);
                Write(g, title, _font, titleInk, new Rectangle(tx, line.Top, Math.Max(0, right - tx), line.Height), true);
            }

            if (stuck && r.Height > _rowH)
            {
                Color ink;
                string note = Note(c, out ink);
                Write(g, note, _smallFont, ink,
                      new Rectangle(x, line.Bottom - S(6), Math.Max(0, right - x), S(18)), true);
            }

            for (int i = 0; i < buttons.Length; i++)
                PaintButton(g, rects[i], Caption(row, buttons[i]),
                            buttons[i] == RowButton.Force && row.ArmedLabel != null,
                            _hot == index && _hotButton == i,
                            _pressRow == index && _pressButton == i,
                            _keyboard && Focused && _focus == index && _focusButton == i);

            if (!_running && _keyboard && Focused && _focus == index) Ring(g, r);
        }

        /// <summary>ConfirmDialog.StyleButton's look, drawn: the row's buttons are not controls.</summary>
        void PaintButton(Graphics g, Rectangle b, string caption, bool armed, bool hot, bool pressed, bool focused)
        {
            Color face = pressed ? Palette.Lift(_background, 0.20f)
                       : hot ? Palette.Lift(_background, 0.14f)
                       : Palette.Lift(_background, 0.06f);
            Fill(g, b, face);

            Color border = armed ? Bad : Palette.Lift(_background, 0.24f);
            using (var pen = new Pen(border))
                g.DrawRectangle(pen, b.Left, b.Top, b.Width - 1, b.Height - 1);

            Color ink = armed ? (Palette.IsLight(_surface) ? Palette.Blend(Bad, Color.Black, 0.25f) : Bad)
                              : Palette.BodyText(_background);
            using (var brush = new SolidBrush(ink))
            using (var format = new StringFormat(StringFormatFlags.NoWrap))
            {
                format.Alignment = StringAlignment.Center;
                format.LineAlignment = StringAlignment.Center;
                g.DrawString(caption, _smallFont, brush, b, format);
            }

            if (focused)
            {
                using (var pen = new Pen(Palette.SecondaryText(_background)))
                {
                    pen.DashStyle = DashStyle.Dot;
                    g.DrawRectangle(pen, b.Left + 2, b.Top + 2, b.Width - 5, b.Height - 5);
                }
            }
        }

        void Mark(Graphics g, string mark, Color ink, Rectangle lead)
        {
            using (var brush = new SolidBrush(ink))
            using (var format = new StringFormat(StringFormatFlags.NoWrap))
            {
                format.Alignment = StringAlignment.Center;
                format.LineAlignment = StringAlignment.Center;
                var r = new RectangleF(lead.Left - S(3), lead.Top - S(3), lead.Width + S(6), lead.Height + S(6));
                g.DrawString(mark, _markFont, brush, r, format);
            }
        }

        /// <summary>
        /// A quarter-arc turning on a faint ring, by the clock rather than by frame, so a
        /// late frame shows the arc where it should be rather than where it would have been.
        /// </summary>
        void DrawSpinner(Graphics g, Rectangle lead)
        {
            SmoothingMode old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float w = Math.Max(1.5f, (float)(2 * _scale));
            var r = new RectangleF(lead.Left + 1.5f, lead.Top + 1.5f, lead.Width - 3f, lead.Height - 3f);
            using (var track = new Pen(Palette.Lift(_background, 0.18f), w))
                g.DrawEllipse(track, r);
            float angle = (Environment.TickCount & 0x7FFFFFFF) % 800 * 360f / 800f;
            using (var arc = new Pen(_accent, w))
            {
                arc.StartCap = LineCap.Round;
                arc.EndCap = LineCap.Round;
                g.DrawArc(arc, r, angle, 100f);
            }
            g.SmoothingMode = old;
        }

        void DrawTicked(Graphics g, Rectangle box)
        {
            Fill(g, box, _accent);
            SmoothingMode old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(_onAccent, Math.Max(1.5f, (float)(1.8 * _scale))))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                float u = box.Width / 14f;
                g.DrawLines(pen, new[]
                {
                    new PointF(box.Left + 3.5f * u, box.Top + 7.2f * u),
                    new PointF(box.Left + 6.0f * u, box.Top + 9.6f * u),
                    new PointF(box.Left + 10.6f * u, box.Top + 4.6f * u)
                });
            }
            g.SmoothingMode = old;
        }

        void DrawMixed(Graphics g, Rectangle box)
        {
            Fill(g, box, _accent);
            int bar = Math.Max(2, S(2));
            int inset = S(3);
            Fill(g, new Rectangle(box.Left + inset, box.Top + (box.Height - bar) / 2, box.Width - 2 * inset, bar), _onAccent);
        }

        /// <summary>An empty box; dotted for a pinned row, which cannot be ticked at all.</summary>
        void DrawEmpty(Graphics g, Rectangle box, bool locked)
        {
            using (var pen = new Pen(Palette.Lift(_background, locked ? 0.20f : 0.32f)))
            {
                if (locked) pen.DashStyle = DashStyle.Dot;
                g.DrawRectangle(pen, box.Left, box.Top, box.Width - 1, box.Height - 1);
            }
        }

        /// <summary>
        /// TooltipWindow's stand-in for an icon that could not be had: the app's first
        /// letter on a soft square, so the column of icons stays a column.
        /// </summary>
        void DrawLetterTile(Graphics g, Rectangle r, string app)
        {
            Fill(g, r, Palette.Lift(_surface, 0.16f));
            string letter = string.IsNullOrEmpty(app) ? "?" : app.Substring(0, 1).ToUpperInvariant();
            using (var ink = new SolidBrush(Palette.SecondaryText(_surface)))
            using (var format = new StringFormat())
            {
                format.Alignment = StringAlignment.Center;
                format.LineAlignment = StringAlignment.Center;
                g.DrawString(letter, _smallFont, ink, r, format);
            }
        }

        static System.Drawing.Imaging.ImageAttributes Faded(float alpha)
        {
            var matrix = new System.Drawing.Imaging.ColorMatrix();
            matrix.Matrix33 = alpha;
            var attributes = new System.Drawing.Imaging.ImageAttributes();
            attributes.SetColorMatrix(matrix);
            return attributes;
        }

        void Ring(Graphics g, Rectangle r)
        {
            using (var pen = new Pen(Palette.SecondaryText(_background)))
                g.DrawRectangle(pen, r.Left, r.Top, r.Width - 1, r.Height - 1);
        }

        static void Fill(Graphics g, Rectangle r, Color c)
        {
            using (var brush = new SolidBrush(c)) g.FillRectangle(brush, r);
        }

        static void Write(Graphics g, string text, Font font, Color ink, Rectangle r, bool ellipsis)
        {
            if (r.Width <= 0 || string.IsNullOrEmpty(text)) return;
            using (var brush = new SolidBrush(ink))
            using (var format = new StringFormat(StringFormatFlags.NoWrap))
            {
                format.LineAlignment = StringAlignment.Center;
                format.Trimming = ellipsis ? StringTrimming.EllipsisCharacter : StringTrimming.None;
                g.DrawString(text, font, brush, r, format);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _smallFont.Dispose();
                _markFont.Dispose();
                for (int i = 0; i < _rows.Count; i++)
                    if (_rows[i].Image != null) { _rows[i].Image.Dispose(); _rows[i].Image = null; }
            }
            base.Dispose(disposing);
        }
    }
}

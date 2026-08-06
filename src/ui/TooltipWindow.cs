using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace DesktopSwitcher
{
    /// <summary>
    /// What a tooltip should say. Built by the controller, which is the only type holding
    /// the desktop model, the window inventory and the foreground tracker together; the
    /// strip supplies geometry and knows nothing about the text.
    /// </summary>
    sealed class TooltipContent
    {
        readonly string _title;
        readonly IList<string> _lines;

        public TooltipContent(string title, IList<string> lines)
        {
            _title = title;
            _lines = lines != null ? lines : new List<string>();
        }

        public string Title { get { return _title; } }

        /// <summary>Window titles, or a single "empty" marker.</summary>
        public IList<string> Lines { get { return _lines; } }
    }

    /// <summary>
    /// The hover panel.
    ///
    /// A top-level popup, NOT a child of the strip or the taskbar: a child is clipped to
    /// its parent, and the taskbar is exactly as tall as the strip, so a child tooltip
    /// would be invisible.
    ///
    /// It must never take activation. WS_EX_NOACTIVATE plus SWP_NOACTIVATE keep focus
    /// where it is - a tooltip that stole focus would break click handling and, worse,
    /// overwrite the window ForegroundTracker is holding, which is the whole basis of
    /// right-click-to-send. WS_EX_TRANSPARENT additionally makes it click-through, which
    /// is what lets the strip own a plain show/hide state machine with no hand-off.
    /// </summary>
    sealed class TooltipWindow : NativeWindow, IDisposable
    {
        /// <summary>One line of the panel. Measure and Render walk the same list, so they cannot disagree.</summary>
        struct Row
        {
            public string Text;
            public Font Font;
            public Color Ink;       // resolved when the row is built, not when it is drawn
        }

        readonly Color _background;
        readonly Color _highlight;
        readonly double _scale;

        readonly Font _titleFont;
        readonly Font _bodyFont;

        readonly int _padX, _padY, _accent, _width;

        Bitmap _buffer;
        List<Row> _rows = new List<Row>();
        bool _accentAtTop;      // panel sits below the anchor, so the accent flips
        bool _visible;
        bool _disposed;

        // GEOMETRY: edges in screen coordinates, not an origin and a size.
        //
        // The obvious model - ease x, y and height - makes the bottom edge hold still only
        // as an accident of arithmetic. For a bottom taskbar Place derives y from height, so
        // y + height is constant across buttons, and easing both at one rate preserves the
        // sum. True in exact arithmetic, and false in four places that all apply here:
        // rounding each value to an int independently can land the sum a pixel out, Ease
        // snaps each value onto its target independently once inside the epsilon so one can
        // arrive a frame before the other, Place's two work-area clamps do not preserve the
        // sum at all, and nothing in the code states the requirement - so easing the height
        // faster than the slide, which is a reasonable thing to want, would silently break
        // an edge nobody knew was load-bearing.
        //
        // Holding the edges instead makes it structural. For a bottom taskbar _bottomTarget
        // equals _bottom always, Ease reports it unmoved, and the bottom edge is identical
        // in device pixels because it was never a computed quantity in the first place.
        // Width is not here: it is fixed at _width and never animates.
        float _left, _top, _bottom;
        float _leftTarget, _topTarget, _bottomTarget;

        // The hovered button's centre, eased alongside the panel at the same rate.
        //
        // DrawAccent used to derive the stub from the anchor rect against a live
        // GetWindowRect, so on the frame the pointer moved, the stub jumped the full width
        // of a button while the panel had not moved at all, then drifted back as the panel
        // caught up - a rubber band rather than one object. Easing the centre too makes both
        // deltas equal, so the stub's offset within the panel is constant and the two move
        // as one body.
        //
        // It also earns its keep in the case the panel cannot move: against the right edge
        // of the work area consecutive buttons clamp to the same x, and then the panel is
        // still while the stub alone travels - motion pointing where the pointer went, where
        // there would otherwise be none.
        float _anchorCentre, _anchorCentreTarget;
        int _anchorWidth;

        public TooltipWindow(Color background, Color highlight, int width, double dpiScale)
        {
            _background = background;
            _highlight = highlight;
            _scale = dpiScale;

            _padX = Scale(12);
            _padY = Scale(9);
            _accent = Scale(3);
            // Every panel is this wide, whatever it holds. Sizing to content made the
            // width jump from button to button as the pointer moved along the strip, which
            // reads as the panel being redrawn rather than moved. Authored at 96 DPI, like
            // every other size in the config.
            _width = Scale(width);

            float body = (float)(12 * dpiScale);
            _titleFont = new Font("Segoe UI", body, FontStyle.Bold, GraphicsUnit.Pixel);
            _bodyFont = new Font("Segoe UI", body, FontStyle.Regular, GraphicsUnit.Pixel);

            var cp = new CreateParams();
            cp.Caption = "DesktopSwitcherTooltip";
            cp.X = 0;
            cp.Y = 0;
            cp.Width = 10;
            cp.Height = 10;
            cp.Parent = IntPtr.Zero;
            // No WS_VISIBLE: it is shown explicitly, already sized and positioned, so it
            // never flashes at the wrong place first.
            cp.Style = Native.WS_POPUP;
            cp.ExStyle = Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST
                       | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT;
            CreateHandle(cp);
        }

        public bool IsVisible { get { return _visible; } }

        int Scale(int value)
        {
            return (int)Math.Round(value * _scale);
        }

        // --- show / hide ------------------------------------------------------

        /// <summary>
        /// Gives the panel new text, heights it to fit - the width is fixed - and aims it at
        /// a button. Safe to call while already visible.
        ///
        /// This is the expensive half of the pair. MoveTo is the other, and the split is
        /// what lets the panel start travelling the moment the pointer does while the text
        /// underneath it changes only once, at the button the pointer stopped on.
        /// </summary>
        public void Show(TooltipContent content, Rectangle anchorScreenRect)
        {
            if (_disposed || content == null) return;

            // Built before anything is committed. The anchor used to be assigned first, so a
            // layout that threw left the stub pointing at the button the pointer had just
            // reached while the rows still described the desktop it came from.
            List<Row> rows;
            Size size;
            try
            {
                rows = BuildRows(content, out size);
            }
            catch (Exception ex)
            {
                Log.Write("tooltip: layout failed - " + ex.Message);
                return;
            }

            _rows = rows;
            Retarget(size, anchorScreenRect);

            // The first show is one SetWindowPos already at its final place: the window is
            // created without WS_VISIBLE precisely so it never appears somewhere else first,
            // and letting the frame path reveal it would put that flash straight back.
            if (!_visible)
            {
                _visible = true;
                Apply(Native.HWND_TOPMOST, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
                return;
            }

            // The text changed even where the geometry did not, so this repaints regardless
            // of whether anything is about to move.
            Apply(IntPtr.Zero, Native.SWP_NOACTIVATE | Native.SWP_NOZORDER
                             | Native.SWP_NOCOPYBITS);
        }

        /// <summary>
        /// Aims the panel at a different button without touching what it says, keeping the
        /// height it already has.
        ///
        /// The cheap half: no BuildRows, so no MeasureString per row and no Fit trim loop on
        /// the UI thread that is also driving the frame timer. That matters because this is
        /// called on every button the pointer crosses, where Show is called once.
        ///
        /// It only retargets. Nothing is applied here - the strip's frame loop owns the
        /// travelling, and if animation is switched off Settle applies the arrival instead.
        /// </summary>
        public void MoveTo(Rectangle anchorScreenRect)
        {
            if (_disposed || !_visible) return;

            Retarget(Measure(_rows), anchorScreenRect);
        }

        /// <summary>
        /// Points the geometry at a button, at a size the caller has already decided.
        /// </summary>
        void Retarget(Size size, Rectangle anchor)
        {
            _anchorWidth = anchor.Width;

            bool accentAtTop;
            Point origin = Place(size, anchor, Screen.FromRectangle(anchor).WorkingArea,
                                 Scale(4), out accentAtTop);

            // Which edge the panel grows from decides which edge holds still, so a change of
            // regime invalidates the geometry rather than retargeting it: the panel no
            // longer fits above the strip and flips below, which --slide measures at 927px
            // on a 1080 screen. That is the panel crossing the display, not a slide, so it
            // snaps. Same for the first show, which must simply be correct rather than
            // arriving from wherever the last one happened to end.
            bool relocated = accentAtTop != _accentAtTop || !_visible;
            _accentAtTop = accentAtTop;

            _leftTarget = origin.X;
            _topTarget = origin.Y;
            _bottomTarget = origin.Y + size.Height;
            _anchorCentreTarget = anchor.Left + anchor.Width / 2;

            if (relocated) Settle();
        }

        public void Hide()
        {
            if (_disposed || !_visible) return;

            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOACTIVATE | Native.SWP_NOMOVE | Native.SWP_NOSIZE
                | Native.SWP_HIDEWINDOW);

            _visible = false;

            // Arrive where it was heading before going dark. A panel left mid-flight would
            // otherwise keep a frame source alive stepping SetWindowPos against a window
            // nobody can see, and would slide the rest of the way the next time it opened.
            Settle();
        }

        // --- geometry ---------------------------------------------------------

        /// <summary>The panel's current edges, rounded once, so every caller agrees.</summary>
        Rectangle Bounds()
        {
            int x = (int)Math.Round(_left);
            int y = (int)Math.Round(_top);
            return new Rectangle(x, y, _width, (int)Math.Round(_bottom) - y);
        }

        /// <summary>What the panel will be once it arrives. What the back buffer is sized for.</summary>
        int TargetHeight()
        {
            return (int)Math.Round(_bottomTarget) - (int)Math.Round(_topTarget);
        }

        /// <summary>
        /// Pushes the current edges at the window. Guarded on the handle: a frame that lands
        /// after DestroyHandle would otherwise reach InvalidateRect with a null hwnd, and a
        /// null hwnd there does not mean "nothing" - it means invalidate every window on the
        /// desktop, sixty-six times a second.
        /// </summary>
        void Apply(IntPtr insertAfter, uint flags)
        {
            if (_disposed || Handle == IntPtr.Zero) return;

            Rectangle b = Bounds();
            Native.SetWindowPos(Handle, insertAfter, b.X, b.Y, b.Width, b.Height, flags);

            // Every pixel of this panel is position-dependent - the stub is placed against a
            // button in screen coordinates - so the whole client area is repainted whatever
            // moved. SWP_NOCOPYBITS on the frame path means the system does not first blit
            // the old pixels to the new place for us to immediately paint over.
            Native.InvalidateRect(Handle, IntPtr.Zero, false);
            Native.UpdateWindow(Handle);
        }

        /// <summary>
        /// Arrive now, without animating - a regime flip, a hide, or animation switched off
        /// entirely.
        ///
        /// Applies as well as assigns, because for the animation-off path this is the only
        /// thing that will: MoveTo deliberately does not touch the window, leaving that to
        /// the frame loop, and with animationMs at 0 there is no frame loop to leave it to.
        /// Guarded on _visible so Hide, which settles after going dark, does not push a
        /// hidden window around.
        /// </summary>
        public void Settle()
        {
            _left = _leftTarget;
            _top = _topTarget;
            _bottom = _bottomTarget;
            _anchorCentre = _anchorCentreTarget;

            if (_visible)
                Apply(IntPtr.Zero, Native.SWP_NOACTIVATE | Native.SWP_NOZORDER
                                 | Native.SWP_NOCOPYBITS);
        }

        public bool Settled
        {
            get
            {
                return _left == _leftTarget && _top == _topTarget
                    && _bottom == _bottomTarget && _anchorCentre == _anchorCentreTarget;
            }
        }

        /// <summary>
        /// One frame of travel, at a rate the strip has already computed. True when anything
        /// moved, which is the strip's cue to keep its timer running.
        ///
        /// The panel does not own a timer of its own. It could - but the strip already has
        /// one, already owns this object, and the two motions always start together, because
        /// the same WM_MOUSEMOVE that lifts a button's hover tone is what retargets the
        /// panel. A second timer would mean a second _lastFrame, a second copy of the
        /// TickCount wrap reasoning and a second stop condition, all to run in lockstep with
        /// the first.
        ///
        /// All four values are eased at the one rate, which is what keeps the bottom edge
        /// pinned and the accent stub riding at a fixed offset inside the panel rather than
        /// racing ahead of it.
        /// </summary>
        public bool Step(float rate)
        {
            if (_disposed || !_visible) return false;

            bool moved = false;
            moved |= Motion.Ease(ref _left, _leftTarget, rate, Motion.PixelEpsilon);
            moved |= Motion.Ease(ref _top, _topTarget, rate, Motion.PixelEpsilon);
            moved |= Motion.Ease(ref _bottom, _bottomTarget, rate, Motion.PixelEpsilon);
            moved |= Motion.Ease(ref _anchorCentre, _anchorCentreTarget, rate, Motion.PixelEpsilon);

            if (moved)
                Apply(IntPtr.Zero, Native.SWP_NOACTIVATE | Native.SWP_NOZORDER
                                 | Native.SWP_NOCOPYBITS);

            return moved;
        }

        /// <summary>The monitor and gap this panel's placement is resolved against.</summary>
        Point Place(Size size, Rectangle anchor)
        {
            return Place(size, anchor, Screen.FromRectangle(anchor).WorkingArea,
                         Scale(4), out _accentAtTop);
        }

        /// <summary>
        /// Above the anchor when there is room, otherwise below, clamped into the
        /// monitor's work area.
        ///
        /// That one rule covers every taskbar edge without special-casing: the work area
        /// excludes the taskbar, so "above" lands on-screen for a bottom taskbar, fails
        /// the fit test for a top one and falls through to "below", and for a side-docked
        /// taskbar the horizontal clamp pushes the panel clear of the bar.
        ///
        /// Static, pure, and taking the work area rather than asking Screen for it, so that
        /// --slide can drive the real placement against synthetic geometry instead of
        /// asserting an identity of its own construction. The instance overload above
        /// supplies the two things only a live panel knows.
        ///
        /// accentAtTop is an out parameter and not a field for the same reason: which edge
        /// the panel grew from used to be a side effect written from inside a method called
        /// Place, which is why the flip was invisible to every caller. The slide has to see
        /// it - crossing between the two regimes moves the panel the height of the screen
        /// and must snap rather than ease.
        /// </summary>
        public static Point Place(Size size, Rectangle anchor, Rectangle work, int gap,
                                  out bool accentAtTop)
        {
            int y = anchor.Top - size.Height - gap;
            accentAtTop = false;
            if (y < work.Top)
            {
                y = anchor.Bottom + gap;
                accentAtTop = true;
            }
            if (y + size.Height > work.Bottom) y = work.Bottom - size.Height;
            if (y < work.Top) y = work.Top;

            // Centred on the button, so the accent stub lines up under it.
            int x = anchor.Left + anchor.Width / 2 - size.Width / 2;
            if (x + size.Width > work.Right) x = work.Right - size.Width;
            if (x < work.Left) x = work.Left;

            return new Point(x, y);
        }

        // --- layout -----------------------------------------------------------

        List<Row> BuildRows(TooltipContent content, out Size size)
        {
            var rows = new List<Row>();

            using (var scratch = new Bitmap(1, 1))
            using (var g = Graphics.FromImage(scratch))
            {
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

                // The title carries the desktop's name and is what the panel is answering;
                // the window rows support it. Both tones come from the sampled background,
                // so a light taskbar gets dark text rather than white on white.
                Add(rows, g, content.Title, _titleFont, Palette.PrimaryText(_background));

                foreach (string line in content.Lines)
                    Add(rows, g, line, _bodyFont, Palette.SecondaryText(_background));

                size = Measure(rows);
            }

            return rows;
        }

        void Add(List<Row> rows, Graphics g, string text, Font font, Color ink)
        {
            var row = new Row();
            row.Text = Fit(g, text, font, _width - _padX * 2);
            row.Font = font;
            row.Ink = ink;
            rows.Add(row);
        }

        /// <summary>
        /// Width is fixed; only the height follows the content, growing a row at a time as
        /// a desktop holds more windows. Rows are already trimmed to fit by Fit, so nothing
        /// here needs to measure them across.
        /// </summary>
        Size Measure(List<Row> rows)
        {
            return new Size(_width, MeasureHeight(_padY, _accent, RowsHeight(rows)));
        }

        /// <summary>
        /// The panel's height for a given stack of rows. Static so --slide can build the
        /// same height Measure would, rather than a number of its own that happens to agree.
        /// </summary>
        public static int MeasureHeight(int padY, int accent, int rowsHeight)
        {
            return padY * 2 + accent + rowsHeight;
        }

        /// <summary>
        /// Where the row block starts, measured from whichever edge is holding still - the
        /// one the accent is not on.
        ///
        /// Static and shared with Render for the same reason as MeasureHeight: the property
        /// that matters is that this and MeasureHeight agree, and a test that recomputed
        /// both would prove only that arithmetic is arithmetic. Feed it a height that
        /// MeasureHeight produced and it must return exactly padY; change the anchoring in
        /// one place without the other and --slide says so.
        /// </summary>
        public static int RowOrigin(int height, int padY, int accent, int rowsHeight,
                                    bool accentAtTop)
        {
            if (accentAtTop) return padY + accent;
            return height - accent - padY - rowsHeight;
        }

        /// <summary>
        /// The stacked height of the rows alone. Shared with Render, which lays them out from
        /// the stationary edge and so has to know the same total Measure built the height
        /// from - the two agreeing is what makes the settled origin come out at exactly _padY.
        /// </summary>
        int RowsHeight() { return RowsHeight(_rows); }

        int RowsHeight(List<Row> rows)
        {
            int height = 0;

            for (int i = 0; i < rows.Count; i++)
                height += LineHeight(rows[i].Font);

            return height;
        }

        int LineHeight(Font font)
        {
            return (int)Math.Ceiling(font.GetHeight()) + Scale(3);
        }

        /// <summary>Trims a title to fit, with an ellipsis. Window titles run long.</summary>
        static string Fit(Graphics g, string text, Font font, int maxWidth)
        {
            if (string.IsNullOrEmpty(text)) return "";
            if (g.MeasureString(text, font).Width <= maxWidth) return text;

            string s = text;
            while (s.Length > 4 && g.MeasureString(s + "...", font).Width > maxWidth)
                s = s.Substring(0, s.Length - 4);

            return s + "...";
        }

        // --- rendering --------------------------------------------------------

        void Paint()
        {
            Native.PAINTSTRUCT ps;
            IntPtr hdc = Native.BeginPaint(Handle, out ps);
            if (hdc == IntPtr.Zero) return;

            try
            {
                Native.RECT client;
                if (!Native.GetWindowRect(Handle, out client)) return;

                int w = client.Width, h = client.Height;
                if (w <= 0 || h <= 0) return;

                // Sized for where the panel is going, not where it currently is, and only
                // ever grown. A height ease climbs a few pixels a frame, so a buffer that
                // must match the client exactly is a fresh Bitmap on every single frame -
                // and "grow only" alone does not help, because the growth is monotonic and
                // fails the test every time. Taking the target height up front means one
                // allocation for the whole slide.
                int needH = Math.Max(h, TargetHeight());
                if (_buffer == null || _buffer.Width < w || _buffer.Height < needH)
                {
                    if (_buffer != null) _buffer.Dispose();
                    _buffer = new Bitmap(Math.Max(w, _width), needH);
                }

                using (var g = Graphics.FromImage(_buffer))
                {
                    Render(g, w, h);
                }

                using (var target = Graphics.FromHdc(hdc))
                {
                    // Clipped, because the buffer is now generally taller than the window
                    // and the surplus holds whatever the last larger frame left there.
                    target.DrawImageUnscaledAndClipped(_buffer, new Rectangle(0, 0, w, h));
                }
            }
            catch (Exception ex)
            {
                Log.Write("tooltip: paint failed - " + ex.Message);
            }
            finally
            {
                Native.EndPaint(Handle, ref ps);
            }
        }

        void Render(Graphics g, int width, int height)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            // Borderless, in the taskbar's own sampled colour, lifted just enough to
            // separate the panel from the bar it grows out of - away from that colour,
            // so a light taskbar gets a slightly darker panel rather than a whiter one
            // it would vanish into.
            using (var back = new SolidBrush(Palette.Lift(_background, 0.06f)))
                g.FillRectangle(back, 0, 0, width, height);

            DrawAccent(g, width, height);

            // Laid out from whichever edge is holding still, which is the edge the accent is
            // NOT on. Rows were always measured from the top, and that was invisible while
            // the panel only ever teleported; once the height eases, a top-anchored block
            // inside a panel whose bottom is pinned slides upward the whole way, so new text
            // appears in the old place and then wanders. Anchoring it to the stationary edge
            // makes it sit still while the panel unrolls behind it.
            //
            // At the settled height this is identically _padY - Measure builds the height out
            // of exactly these terms - so nothing moves at rest. --slide asserts that.
            int y = RowOrigin(height, _padY, _accent, RowsHeight(), _accentAtTop);

            for (int i = 0; i < _rows.Count; i++)
            {
                Row row = _rows[i];

                using (var brush = new SolidBrush(row.Ink))
                    g.DrawString(row.Text, row.Font, brush, _padX, y);

                y += LineHeight(row.Font);
            }
        }

        /// <summary>
        /// A short stub on the edge facing the strip, centred on the hovered button, so the
        /// panel reads as belonging to that button rather than floating loose.
        ///
        /// Deliberately not the mark it used to be. This was a full-button-width bar at
        /// Scale(3) tall - which is, attribute for attribute, the underline
        /// SwitcherStrip.DrawBar puts under the *current* desktop, sitting four pixels above
        /// it across the gap Place leaves. Hovering desktop 2 while desktop 1 was current
        /// therefore painted the current-desktop mark over button 2, and the two bars
        /// stacked read as one indicator drawn twice.
        ///
        /// Width and shape are what separate them now: a fraction of the button rather than
        /// all of it, rounded rather than square. The colour stays _highlight - it was tried
        /// in a neutral tone off the panel surface and looked worse, and two changed
        /// attributes out of three carry the distinction on their own.
        /// </summary>
        void DrawAccent(Graphics g, int width, int height)
        {
            int anchorWidth = _anchorWidth > 0 ? _anchorWidth : Scale(24);

            // A stub, not a bar. Two fifths of the button still points at it unambiguously
            // without reading as a copy of it. Floored at _accent because below its own
            // height there is no room for the two round caps and the pill collapses to a
            // dot - which only bites at very small button widths on a low-DPI display.
            int barWidth = Math.Max(_accent, anchorWidth * 2 / 5);

            // Against the panel's own eased left edge, not a live GetWindowRect. Two
            // reasons: it is one syscall per frame saved, and more importantly it is the
            // same number the window was positioned from, so the stub cannot disagree with
            // where the panel actually is on a frame where the two were read apart.
            int centre = (int)Math.Round(_anchorCentre - _left);

            int x = centre - barWidth / 2;
            if (x < 0) x = 0;
            if (x + barWidth > width) x = width - barWidth;

            int y = _accentAtTop ? 0 : height - _accent;

            using (var brush = new SolidBrush(_highlight))
            using (var pill = Pill(new Rectangle(x, y, barWidth, _accent)))
                g.FillPath(brush, pill);
        }

        /// <summary>
        /// A rectangle with semicircular ends, each cap a circle of the stub's own height.
        ///
        /// At Scale(3) tall the rounding is worth about a pixel a side, which is the whole
        /// intent: enough to take the hard corners off a mark that used to be square, not
        /// enough to make it a shape competing for attention.
        /// </summary>
        static GraphicsPath Pill(Rectangle r)
        {
            var path = new GraphicsPath();

            int d = r.Height;
            path.AddArc(r.X, r.Y, d, d, 90, 180);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 180);
            path.CloseFigure();

            return path;
        }

        // --- window procedure --------------------------------------------------

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case Native.WM_PAINT:
                    Paint();
                    m.Result = IntPtr.Zero;
                    return;

                case Native.WM_ERASEBKGND:
                    m.Result = new IntPtr(1);
                    return;
            }

            base.WndProc(ref m);
        }

        // --- lifetime ---------------------------------------------------------

        public void Dispose()
        {
            if (_disposed) return;

            // Hidden before _disposed is set, or Hide() would bail out on its own guard
            // and leave a frame of this on screen as an orphaned topmost window - the
            // worst failure mode this class has, and the one an Explorer restart hits.
            Hide();
            _disposed = true;

            if (Handle != IntPtr.Zero) DestroyHandle();

            if (_buffer != null) { _buffer.Dispose(); _buffer = null; }
            _titleFont.Dispose();
            _bodyFont.Dispose();
        }
    }
}

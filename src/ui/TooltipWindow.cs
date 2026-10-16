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
        readonly IList<StatsTile> _tiles;

        public TooltipContent(string title, IList<string> lines)
            : this(title, lines, null)
        {
        }

        public TooltipContent(string title, IList<string> lines, IList<StatsTile> tiles)
        {
            _title = title;
            _lines = lines != null ? lines : new List<string>();
            _tiles = tiles;
        }

        public string Title { get { return _title; } }

        /// <summary>Window titles, or a single "empty" marker.</summary>
        public IList<string> Lines { get { return _lines; } }

        /// <summary>
        /// Tiles drawn side by side under the lines, or null for none. Only the stats panel
        /// has any.
        /// </summary>
        public IList<StatsTile> Tiles { get { return _tiles; } }

        /// <summary>
        /// The window behind each line, for the modern panel's icon and two-tone row: as long
        /// as Lines, with null where a line is not a window - "+N more", "- empty -", a hint.
        /// Null altogether for a panel with no windows in it. The classic panel ignores it
        /// and draws Lines as they are, bullets and all.
        /// </summary>
        public IList<WindowEntry> Windows { get; set; }

        /// <summary>
        /// Small print at the right of the modern panel's title - "5 windows". Null for none,
        /// and unused by the classic panel.
        /// </summary>
        public string Note { get; set; }

        /// <summary>
        /// Small print at the right of the title in both styles - the stats panel's public
        /// IP. Separate from Note so the classic desktop panels, which have always ignored
        /// Note, still look exactly as they did. Modern shows Note in preference when both
        /// are set; nothing sets both.
        /// </summary>
        public string Corner { get; set; }
    }

    /// <summary>
    /// One tile of the stats panel: a label, a figure - in a ring gauge, or plain - a detail
    /// line, and a chart of the last minute along the bottom.
    ///
    /// Already normalised by whoever built it - the gauge 0..1, chart points with x 0..1
    /// from the oldest edge of the window to now and y 0..1 from the floor - so the panel
    /// only maps them onto rectangles and knows nothing about percent, bytes or seconds.
    /// The scale is the content's decision for the same reason the text is. A plain data
    /// bag, filled field by field by Controller.StatsContent.
    /// </summary>
    sealed class StatsTile
    {
        public string Label;

        /// <summary>The big figure: inside the ring when there is one, the first line when not.</summary>
        public string Figure;

        /// <summary>A second big line under Figure, for a tile with no ring. Null for none.</summary>
        public string Figure2;

        /// <summary>
        /// For a tile with no ring: the widest the part of a figure after its first space can
        /// get, e.g. "8888 KB/s". Set, the figures are drawn as one fixed-width block - the
        /// lead, up to the first space, pinned left and the rest right-aligned - so a rate
        /// changing width moves only its digits, not its arrow or its unit. Null centres each
        /// line on its own.
        /// </summary>
        public string FigureTemplate;

        /// <summary>Small print under the visual: a peak, a total.</summary>
        public string Detail;

        /// <summary>Whether this tile draws a ring gauge around Figure.</summary>
        public bool HasGauge;

        /// <summary>The ring's fill, 0..1. Negative draws the track alone: not known yet.</summary>
        public float Gauge = -1f;

        /// <summary>
        /// The chart: a filled shape for Primary and a plain line for Secondary, which may be
        /// null. Points outside x 0..1 are clipped, which is how the one point older than the
        /// window enters from its edge.
        /// </summary>
        public IList<PointF> Primary;
        public IList<PointF> Secondary;
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

            // The stats tiles, as one row: set, the row is the whole tile strip and Text is
            // empty. A row rather than a separate block so that RowsHeight, and with it
            // Measure, RowOrigin and the pinned-edge slide, account for the tiles without
            // a second height calculation that would have to be kept in step with the first.
            public IList<StatsTile> Tiles;

            // Modern only, but for Note, which a classic title row carries too when the
            // content has a Corner. A header row is the title with Note at its right; a window row is
            // an icon, App in semibold and Detail muted after it, with AppWidth measured once
            // here so a frame of the slide does not measure it again. Classic builds every
            // row as Plain, so none of this exists there.
            public RowKind Kind;
            public string Note;
            public string App;
            public string Detail;
            public float AppWidth;
            public Bitmap Icon;     // the cache's, never disposed by the row; null draws a letter tile
        }

        enum RowKind { Plain, Header, Window }

        // The modern panel's sizes at 96 DPI, beside the classic ones in Metrics. Public so
        // --slide builds its heights from the same numbers.
        public const int ModernRowHeight = 28;
        const int ModernHeaderHeight = 30;
        const int ModernIcon = 16;
        const int ModernRadius = 8;

        // The shadow's reach past the panel on every side, and how far it sits below centre.
        // The window is the panel plus this, so the shadow has pixels to fade out in; they are
        // click-through like the rest of it.
        const int ShadowSpread = 12;
        const int ShadowDrop = 3;

        // Tile geometry at 96 DPI. The gauge is the tallest thing in a tile and sets its
        // height; everything else stacks around it. See TilesHeight for the sum.
        const int TileGap = 8;          // between tiles, and above the row of them
        const int TilePad = 8;          // inside a tile, every edge
        const int GaugeSize = 64;       // the ring's box, and the height of the network figures' box
        const int GaugeStroke = 6;
        const int ChartHeight = 30;

        readonly Color _background;
        readonly Color _highlight;
        readonly double _scale;

        readonly Font _titleFont;
        readonly Font _bodyFont;

        // The stats tiles' type: the figure in a ring, the larger figures of a tile with no
        // ring, and the small print under both. Created with the window like the two above,
        // so a panel that never shows the stats pays three font handles and nothing else.
        readonly Font _gaugeFont;
        readonly Font _figureFont;
        readonly Font _smallFont;

        readonly int _padX, _padY, _accent, _gap, _width;

        // The style, and what it costs. Modern is a layered window - see Present - so it
        // carries a DIB section to draw into and a shadow to stretch; classic carries
        // neither and paints on WM_PAINT exactly as it always has. Everything that differs
        // between the two keys off this one flag, set from the Look the panel was made with.
        readonly bool _layered;
        readonly Font _headFont;     // modern title
        readonly Font _appFont;      // modern app name
        readonly int _rowHeight, _headHeight, _iconSize, _radius;
        readonly int _spread, _drop;   // shadow room around the panel, physical pixels
        Bitmap _shadow;                // the nine-slice source, built once
        int _shadowCorner;             // corner slice size in it

        IntPtr _memDC, _dib, _oldBitmap, _bits;
        int _dibWidth, _dibHeight;
        Bitmap _surface;               // GDI+ over _bits, premultiplied

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

        public TooltipWindow(Color background, Color highlight, int width, double dpiScale, Look look)
        {
            _background = background;
            _highlight = highlight;
            _scale = dpiScale;
            _layered = look == Look.Modern;

            int padY, accent, gap;
            Metrics(look, out padY, out accent, out gap);
            _padX = Scale(12);
            _padY = Scale(padY);
            _accent = Scale(accent);
            _gap = Scale(gap);
            // Every panel is this wide, whatever it holds. Sizing to content made the
            // width jump from button to button as the pointer moved along the strip, which
            // reads as the panel being redrawn rather than moved. Authored at 96 DPI, like
            // every other size in the config.
            _width = Scale(width);

            float body = (float)(12 * dpiScale);
            _titleFont = new Font("Segoe UI", body, FontStyle.Bold, GraphicsUnit.Pixel);
            _bodyFont = new Font("Segoe UI", body, FontStyle.Regular, GraphicsUnit.Pixel);
            _gaugeFont = new Font("Segoe UI", (float)(16 * dpiScale), FontStyle.Bold, GraphicsUnit.Pixel);
            _figureFont = new Font("Segoe UI", (float)(14 * dpiScale), FontStyle.Bold, GraphicsUnit.Pixel);
            _smallFont = new Font("Segoe UI", (float)(11 * dpiScale), FontStyle.Regular, GraphicsUnit.Pixel);

            if (_layered)
            {
                _headFont = new Font("Segoe UI Semibold", (float)(14 * dpiScale), FontStyle.Regular, GraphicsUnit.Pixel);
                _appFont = new Font("Segoe UI Semibold", body, FontStyle.Regular, GraphicsUnit.Pixel);
                _rowHeight = Scale(ModernRowHeight);
                _headHeight = Scale(ModernHeaderHeight);
                _iconSize = Scale(ModernIcon);
                _radius = Scale(ModernRadius);
                _spread = Scale(ShadowSpread);
                _drop = Scale(ShadowDrop);
                _shadow = BuildShadow(out _shadowCorner);
            }

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
            if (_layered) cp.ExStyle |= Native.WS_EX_LAYERED;
            CreateHandle(cp);
        }

        /// <summary>
        /// The panel's vertical padding, accent stub height and gap to the strip for a style,
        /// at 96 DPI. One place for them so --slide proves the geometry the panel actually
        /// uses rather than numbers it was told agree.
        ///
        /// Modern has no stub - the lit cell on the strip already says which button the
        /// panel describes, and a stub on a rounded, floating card reads as a speech-bubble
        /// tail - and stands further off the strip, so it reads as floating over it rather
        /// than growing out of it. Padding is even top and bottom because MeasureHeight
        /// counts it twice; the header row carries the extra air above the list.
        /// </summary>
        public static void Metrics(Look look, out int padY, out int accent, out int gap)
        {
            if (look == Look.Modern)
            {
                padY = 10;
                accent = 0;
                gap = 8;
                return;
            }

            padY = 9;
            accent = 3;
            gap = 4;
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
                                 _gap, out accentAtTop);

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

            // A layered window is moved, sized and painted by the one UpdateLayeredWindow in
            // Present, so SetWindowPos is left only what that cannot do: show it and put it
            // on top, on the first show. Drawn before it is shown, for the same reason the
            // window is created without WS_VISIBLE.
            if (_layered)
            {
                Present(b);
                if ((flags & Native.SWP_SHOWWINDOW) != 0)
                    Native.SetWindowPos(Handle, insertAfter, 0, 0, 0, 0,
                                        flags | Native.SWP_NOMOVE | Native.SWP_NOSIZE);
                return;
            }

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
                         _gap, out _accentAtTop);
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

                if (_layered)
                {
                    BuildModernRows(rows, g, content);
                }
                else
                {
                    // The title carries the desktop's name and is what the panel is answering;
                    // the window rows support it. Both tones come from the sampled background,
                    // so a light taskbar gets dark text rather than white on white.
                    //
                    // The note goes at the title's right, as modern puts it, and the title is
                    // trimmed short of it rather than running underneath.
                    if (string.IsNullOrEmpty(content.Corner))
                    {
                        Add(rows, g, content.Title, _titleFont, Palette.PrimaryText(_background));
                    }
                    else
                    {
                        float noteWidth = g.MeasureString(content.Corner, _smallFont).Width + Scale(12);
                        var title = new Row();
                        title.Text = Fit(g, content.Title, _titleFont, _width - _padX * 2 - (int)noteWidth);
                        title.Font = _titleFont;
                        title.Ink = Palette.PrimaryText(_background);
                        title.Note = content.Corner;
                        rows.Add(title);
                    }

                    foreach (string line in content.Lines)
                        Add(rows, g, line, _bodyFont, Palette.SecondaryText(_background));
                }

                if (content.Tiles != null && content.Tiles.Count > 0)
                {
                    var tiles = new Row();
                    tiles.Text = "";
                    tiles.Font = _bodyFont;
                    tiles.Tiles = content.Tiles;
                    rows.Add(tiles);
                }

                size = Measure(rows);
            }

            return rows;
        }

        /// <summary>
        /// The modern rows: a header with the window count at its right, then each line as a
        /// window row where there is a window behind it and as plain muted text where there
        /// is not.
        ///
        /// Nothing is trimmed here. Classic trims with Fit, a MeasureString loop per row;
        /// modern hands DrawString a rectangle and lets it place the ellipsis, which is the
        /// same answer for less work - and the app name, the one width the row's layout
        /// depends on, is measured once, here, not per frame.
        /// </summary>
        void BuildModernRows(List<Row> rows, Graphics g, TooltipContent content)
        {
            var header = new Row();
            header.Kind = RowKind.Header;
            header.Text = content.Title ?? "";
            header.Note = content.Note != null ? content.Note : content.Corner;
            header.Font = _headFont;
            header.Ink = Palette.PrimaryText(_background);
            rows.Add(header);

            IList<WindowEntry> windows = content.Windows;
            float maxApp = (_width - _padX * 2 - TextColumn()) * 0.6f;

            for (int i = 0; i < content.Lines.Count; i++)
            {
                WindowEntry window = windows != null && i < windows.Count ? windows[i] : null;
                var row = new Row();
                row.Font = _bodyFont;

                if (window == null)
                {
                    row.Kind = RowKind.Plain;
                    row.Text = content.Lines[i];
                    row.Ink = DetailInk();
                    rows.Add(row);
                    continue;
                }

                // App first and the title after it, as Controller.Describe orders them - but
                // in two weights rather than joined by a dash. A window with no app name
                // leads with its title instead; one whose title is its app name says it once.
                bool hasApp = window.App.Length > 0;
                row.Kind = RowKind.Window;
                row.App = hasApp ? window.App : window.Title;
                row.Detail = !hasApp || window.Title == window.App ? "" : window.Title;
                row.AppWidth = Math.Min(maxApp,
                    g.MeasureString(row.App, _appFont, PointF.Empty, StringFormat.GenericTypographic).Width + 1f);
                row.Icon = AppIcons.Get(window, _iconSize);
                row.Ink = Palette.BodyText(_background);
                rows.Add(row);
            }
        }

        /// <summary>Where a window row's text starts, past its icon, from the panel's padding.</summary>
        int TextColumn()
        {
            return _iconSize + Scale(10);
        }

        /// <summary>
        /// The muted tone of a window's title and of the rows that are not windows: halfway
        /// from Secondary to Muted, so the app name in front of it reads first without the
        /// title fading into something that looks disabled.
        /// </summary>
        Color DetailInk()
        {
            return Palette.Blend(Palette.SecondaryText(_background), Palette.MutedText(_background), 0.5f);
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
                height += RowHeight(rows[i]);

            return height;
        }

        int RowHeight(Row row)
        {
            if (row.Tiles != null) return TilesHeight();
            if (row.Kind == RowKind.Header) return _headHeight;
            if (row.Kind == RowKind.Window) return _rowHeight;
            return LineHeight(row.Font);
        }

        /// <summary>
        /// The tile row: the gap above it, then one tile - label, gauge, detail, chart, each
        /// padded - and nothing below, since the panel's own padding follows.
        /// </summary>
        int TilesHeight()
        {
            return Scale(TileGap) + TileHeight();
        }

        int TileHeight()
        {
            return Scale(TilePad) * 2 + LineHeight(_bodyFont) + Scale(GaugeSize)
                 + Scale(4) + LineHeight(_smallFont) + Scale(ChartHeight);
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

            // A layered window fed by UpdateLayeredWindow is never asked to paint, but a
            // paint message that does arrive still has to be validated, or it repeats.
            if (_layered)
            {
                Native.EndPaint(Handle, ref ps);
                return;
            }

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

        // --- the modern panel -------------------------------------------------

        /// <summary>
        /// Draws the modern panel at <paramref name="b"/> and hands it to the window in one
        /// UpdateLayeredWindow, which moves and sizes it in the same call.
        ///
        /// Why a layered window at all: Windows 10 has no rounded corners for an ordinary
        /// window and no shadow for a popup that is not a menu, and the obvious substitutes
        /// both look worse - a SetWindowRgn corner is a hard pixel staircase, CS_DROPSHADOW
        /// is a hard grey edge on two sides. Per-pixel alpha gives an antialiased corner and
        /// a shadow that fades, and DWM composes it, so there is nothing for a video playing
        /// underneath to leave trails in.
        ///
        /// The window is larger than the panel by the shadow's reach, but all of the
        /// geometry - Place, the eased edges, --slide - is still about the panel; the margin
        /// is a constant added here and nowhere else, so the pinned bottom edge stays pinned.
        ///
        /// Text keeps ClearType. Its colour fringes assume an opaque background, and the panel
        /// body is fully opaque; only the corners and the shadow are translucent, and no text
        /// is drawn there.
        /// </summary>
        void Present(Rectangle b)
        {
            if (b.Width <= 0 || b.Height <= 0) return;

            int w = b.Width + _spread * 2;
            int h = b.Height + _spread * 2;

            try
            {
                // Grown, never shrunk, and sized up front for where the height is heading -
                // the same reasoning as the classic back buffer in Paint.
                EnsureSurface(Math.Max(w, _width + _spread * 2),
                              Math.Max(h, TargetHeight() + _spread * 2));

                using (var g = Graphics.FromImage(_surface))
                {
                    g.Clear(Color.Transparent);

                    // The shadow is centred a little below the panel, so the window's top
                    // margin is the spread less the drop and the panel sits that high in it.
                    DrawShadow(g, new Rectangle(0, 0, w, h));

                    g.TranslateTransform(_spread, _spread - _drop);
                    RenderModern(g, b.Width, b.Height);
                }

                var dst = new Native.POINT();
                dst.X = b.X - _spread;
                dst.Y = b.Y - (_spread - _drop);
                var size = new Native.SIZE();
                size.cx = w;
                size.cy = h;
                var src = new Native.POINT();
                var blend = new Native.BLENDFUNCTION();
                blend.BlendOp = Native.AC_SRC_OVER;
                blend.SourceConstantAlpha = 255;
                blend.AlphaFormat = Native.AC_SRC_ALPHA;

                if (!Native.UpdateLayeredWindow(Handle, IntPtr.Zero, ref dst, ref size, _memDC, ref src,
                                                0, ref blend, Native.ULW_ALPHA))
                    Log.Write("tooltip: UpdateLayeredWindow failed (win32 "
                              + System.Runtime.InteropServices.Marshal.GetLastWin32Error() + ")");
            }
            catch (Exception ex)
            {
                Log.Write("tooltip: present failed - " + ex.Message);
            }
        }

        /// <summary>
        /// A 32bpp top-down DIB section selected into a memory DC, with a GDI+ Bitmap laid
        /// over the very same bits. GDI+ draws premultiplied straight into the memory
        /// UpdateLayeredWindow reads, so a frame is drawn once and copied by nobody.
        /// </summary>
        void EnsureSurface(int width, int height)
        {
            if (_surface != null && _dibWidth >= width && _dibHeight >= height) return;

            ReleaseSurface();

            var bi = new Native.BITMAPINFOHEADER();
            bi.biSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.BITMAPINFOHEADER));
            bi.biWidth = width;
            bi.biHeight = -height;     // negative: top-down, so row 0 is the top, as GDI+ expects
            bi.biPlanes = 1;
            bi.biBitCount = 32;

            _memDC = Native.CreateCompatibleDC(IntPtr.Zero);
            _dib = Native.CreateDIBSection(_memDC, ref bi, 0, out _bits, IntPtr.Zero, 0);
            if (_memDC == IntPtr.Zero || _dib == IntPtr.Zero || _bits == IntPtr.Zero)
            {
                ReleaseSurface();
                throw new InvalidOperationException("no DIB section for the panel");
            }

            _oldBitmap = Native.SelectObject(_memDC, _dib);
            _surface = new Bitmap(width, height, width * 4,
                                  System.Drawing.Imaging.PixelFormat.Format32bppPArgb, _bits);
            _dibWidth = width;
            _dibHeight = height;
        }

        void ReleaseSurface()
        {
            if (_surface != null) { _surface.Dispose(); _surface = null; }
            if (_memDC != IntPtr.Zero && _oldBitmap != IntPtr.Zero) Native.SelectObject(_memDC, _oldBitmap);
            if (_dib != IntPtr.Zero) Native.DeleteObject(_dib);
            if (_memDC != IntPtr.Zero) Native.DeleteDC(_memDC);
            _memDC = _dib = _oldBitmap = _bits = IntPtr.Zero;
            _dibWidth = _dibHeight = 0;
        }

        /// <summary>
        /// The shadow's nine-slice source: a small rounded square, blurred, in black with
        /// premultiplied alpha. Built once, because blurring the panel's own outline on every
        /// frame of a slide would cost milliseconds a frame for a shape whose corners never
        /// change; stretching the straight middle of this one costs nine DrawImage calls.
        ///
        /// The square is two corners and two pixels across, so its middle slice is two pixels
        /// of pure straight edge. Three box passes approximate a Gaussian; each reaches a
        /// third of the spread, so together they fade out just inside the window's margin.
        /// </summary>
        Bitmap BuildShadow(out int corner)
        {
            int core = _radius * 2 + 4;
            int size = core + _spread * 2;
            corner = _spread + _radius + 1;

            // Lighter under a light taskbar, where a dark halo reads as dirt rather than depth.
            int peak = Palette.IsLight(_background) ? 70 : 130;

            var alpha = new int[size * size];
            using (var mask = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(mask))
                using (var brush = new SolidBrush(Color.FromArgb(peak, 0, 0, 0)))
                using (var path = Rounded(new Rectangle(_spread, _spread, core, core), _radius))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.FillPath(brush, path);
                }

                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                        alpha[y * size + x] = mask.GetPixel(x, y).A;
            }

            int reach = Math.Max(1, _spread / 3);
            var scratch = new int[alpha.Length];
            for (int pass = 0; pass < 3; pass++)
            {
                BoxBlur(alpha, scratch, size, reach, true);
                BoxBlur(scratch, alpha, size, reach, false);
            }

            var shadow = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            var data = shadow.LockBits(new Rectangle(0, 0, size, size),
                                       System.Drawing.Imaging.ImageLockMode.WriteOnly,
                                       System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            var pixels = new int[alpha.Length];
            for (int i = 0; i < alpha.Length; i++) pixels[i] = alpha[i] << 24;   // black: colour is zero premultiplied
            System.Runtime.InteropServices.Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
            shadow.UnlockBits(data);
            return shadow;
        }

        /// <summary>One pass of a running-sum box blur along rows or columns of a square.</summary>
        static void BoxBlur(int[] src, int[] dst, int size, int reach, bool rows)
        {
            int span = reach * 2 + 1;
            for (int line = 0; line < size; line++)
            {
                int sum = 0;
                for (int k = -reach; k <= reach; k++)
                    sum += src[At(line, Math.Max(0, Math.Min(size - 1, k)), size, rows)];

                for (int i = 0; i < size; i++)
                {
                    dst[At(line, i, size, rows)] = sum / span;
                    sum += src[At(line, Math.Min(size - 1, i + reach + 1), size, rows)]
                         - src[At(line, Math.Max(0, i - reach), size, rows)];
                }
            }
        }

        static int At(int line, int i, int size, bool rows)
        {
            return rows ? line * size + i : i * size + line;
        }

        /// <summary>
        /// The shadow stretched to <paramref name="outer"/>, the whole window: corners copied
        /// one to one, the two-pixel middles stretched along the edges and across the centre.
        /// Nearest-neighbour with half-pixel offset, so a stretched slice repeats its own
        /// pixels and never blends in its neighbour's.
        /// </summary>
        void DrawShadow(Graphics g, Rectangle outer)
        {
            int c = _shadowCorner;
            int s = _shadow.Width;
            int mid = s - c * 2;
            if (outer.Width < c * 2 || outer.Height < c * 2 || mid <= 0) return;

            InterpolationMode interpolation = g.InterpolationMode;
            PixelOffsetMode offset = g.PixelOffsetMode;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;

            int[] sx = { 0, c, s - c };
            int[] sw = { c, mid, c };
            int[] dx = { outer.X, outer.X + c, outer.Right - c };
            int[] dw = { c, outer.Width - c * 2, c };
            int[] dy = { outer.Y, outer.Y + c, outer.Bottom - c };
            int[] dh = { c, outer.Height - c * 2, c };

            for (int row = 0; row < 3; row++)
                for (int col = 0; col < 3; col++)
                    g.DrawImage(_shadow, new Rectangle(dx[col], dy[row], dw[col], dh[row]),
                                sx[col], sx[row], sw[col], sw[row], GraphicsUnit.Pixel);

            g.PixelOffsetMode = offset;
            g.InterpolationMode = interpolation;
        }

        /// <summary>
        /// The panel itself, in its own coordinates: a rounded card one step off the taskbar
        /// colour with a hairline edge, then the rows from the stationary edge exactly as the
        /// classic Render lays them out.
        /// </summary>
        void RenderModern(Graphics g, int width, int height)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            Color surface = Palette.Lift(_background, 0.07f);
            using (var back = new SolidBrush(surface))
            using (var path = Rounded(new Rectangle(0, 0, width, height), _radius))
                g.FillPath(back, path);

            // The hairline is what separates the card from a wallpaper of the same darkness;
            // the shadow alone goes missing against a black one.
            Color edge = Palette.IsLight(_background) ? Color.FromArgb(30, 0, 0, 0) : Color.FromArgb(36, 255, 255, 255);
            using (var pen = new Pen(edge, 1f))
            using (var path = Rounded(new RectangleF(0.5f, 0.5f, width - 1f, height - 1f), _radius))
                g.DrawPath(pen, path);

            bool listsWindows = false;
            for (int i = 0; i < _rows.Count; i++)
                if (_rows[i].Kind == RowKind.Window) listsWindows = true;

            int y = RowOrigin(height, _padY, _accent, RowsHeight(), _accentAtTop);

            using (var format = (StringFormat)StringFormat.GenericTypographic.Clone())
            {
                format.LineAlignment = StringAlignment.Center;
                format.Trimming = StringTrimming.EllipsisCharacter;
                format.FormatFlags |= StringFormatFlags.NoWrap;

                for (int i = 0; i < _rows.Count; i++)
                {
                    Row row = _rows[i];
                    int rh = RowHeight(row);

                    if (row.Tiles != null) DrawTiles(g, row.Tiles, y);
                    else if (row.Kind == RowKind.Header) DrawHeader(g, row, y, rh, width, format);
                    else if (row.Kind == RowKind.Window) DrawWindowRow(g, row, y, rh, width, surface, format);
                    else
                    {
                        // "+3 more" sits in the text column under the titles it continues; a
                        // hint in a panel with no windows starts at the padding like the title.
                        int x = _padX + (listsWindows ? TextColumn() : 0);
                        using (var ink = new SolidBrush(row.Ink))
                            g.DrawString(row.Text, row.Font, ink, new RectangleF(x, y, width - _padX - x, rh), format);
                    }

                    y += rh;
                }
            }
        }

        void DrawHeader(Graphics g, Row row, int y, int rh, int width, StringFormat format)
        {
            float noteWidth = 0f;
            if (!string.IsNullOrEmpty(row.Note))
            {
                noteWidth = g.MeasureString(row.Note, _smallFont, PointF.Empty, format).Width;
                using (var right = (StringFormat)format.Clone())
                using (var ink = new SolidBrush(Palette.MutedText(_background)))
                {
                    right.Alignment = StringAlignment.Far;
                    g.DrawString(row.Note, _smallFont, ink, new RectangleF(_padX, y, width - _padX * 2, rh), right);
                }
                noteWidth += Scale(12);
            }

            using (var ink = new SolidBrush(row.Ink))
                g.DrawString(row.Text, row.Font, ink,
                             new RectangleF(_padX, y, Math.Max(0f, width - _padX * 2 - noteWidth), rh), format);
        }

        void DrawWindowRow(Graphics g, Row row, int y, int rh, int width, Color surface, StringFormat format)
        {
            var icon = new Rectangle(_padX, y + (rh - _iconSize) / 2, _iconSize, _iconSize);
            if (row.Icon != null)
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(row.Icon, icon);
            }
            else
            {
                DrawLetterTile(g, icon, row.App, surface);
            }

            float x = _padX + TextColumn();
            using (var ink = new SolidBrush(row.Ink))
                g.DrawString(row.App, _appFont, ink, new RectangleF(x, y, row.AppWidth, rh), format);

            if (row.Detail.Length == 0) return;

            float detailX = x + row.AppWidth + Scale(8);
            float room = width - _padX - detailX;
            if (room <= 0f) return;

            using (var ink = new SolidBrush(DetailInk()))
                g.DrawString(row.Detail, _bodyFont, ink, new RectangleF(detailX, y, room, rh), format);
        }

        /// <summary>
        /// Stands in for an icon that could not be had: the app's first letter on a soft
        /// square, so the column of icons stays a column and the row still says whose it is.
        /// </summary>
        void DrawLetterTile(Graphics g, Rectangle r, string app, Color surface)
        {
            using (var fill = new SolidBrush(Palette.Lift(surface, 0.16f)))
            using (var path = Rounded(r, Math.Max(2, r.Width / 4)))
                g.FillPath(fill, path);

            string letter = string.IsNullOrEmpty(app) ? "?" : app.Substring(0, 1).ToUpperInvariant();
            using (var ink = new SolidBrush(Palette.SecondaryText(surface)))
            using (var format = new StringFormat())
            {
                format.Alignment = StringAlignment.Center;
                format.LineAlignment = StringAlignment.Center;
                g.DrawString(letter, _smallFont, ink, r, format);
            }
        }

        // --- the classic panel ------------------------------------------------

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

                // Right-aligned and centred on the title's line, which is taller than its own.
                if (!string.IsNullOrEmpty(row.Note))
                {
                    using (var right = new StringFormat())
                    using (var ink = new SolidBrush(Palette.MutedText(_background)))
                    {
                        right.Alignment = StringAlignment.Far;
                        right.LineAlignment = StringAlignment.Center;
                        right.FormatFlags |= StringFormatFlags.NoWrap;
                        g.DrawString(row.Note, _smallFont, ink,
                                     new RectangleF(_padX, y, _width - _padX * 2, RowHeight(row)), right);
                    }
                }

                if (row.Tiles != null) DrawTiles(g, row.Tiles, y);

                y += RowHeight(row);
            }
        }

        // --- stats tiles ------------------------------------------------------

        /// <summary>
        /// The tiles side by side across the panel's width, equal shares of it. The width is
        /// the panel's fixed one, so the tiles never resize as the figures change; they only
        /// get narrower if tooltipWidth is set narrower, and the gauge shrinks to fit.
        /// </summary>
        void DrawTiles(Graphics g, IList<StatsTile> tiles, int y)
        {
            int gap = Scale(TileGap);
            int top = y + gap;
            int inner = _width - _padX * 2;
            int width = (inner - gap * (tiles.Count - 1)) / tiles.Count;
            int height = TileHeight();

            for (int i = 0; i < tiles.Count; i++)
                DrawTile(g, tiles[i], new Rectangle(_padX + i * (width + gap), top, width, height));
        }

        /// <summary>
        /// One tile, top to bottom: label, the visual - a ring gauge with the figure inside,
        /// or for a tile without one its figures - then the detail line and the chart.
        ///
        /// The card is a step further off the taskbar colour than the panel is, the same
        /// direction Palette.Lift always goes, so it separates on a light taskbar as on a
        /// dark one. Everything coloured is the accent, matching the meters on the strip,
        /// so the tile and the panel it opens read as the same thing.
        /// </summary>
        void DrawTile(Graphics g, StatsTile tile, Rectangle r)
        {
            using (var card = new SolidBrush(Palette.Lift(_background, 0.12f)))
            using (var path = Rounded(r, Scale(6)))
                g.FillPath(card, path);

            int pad = Scale(TilePad);
            int x = r.X + pad;
            int w = r.Width - pad * 2;
            int y = r.Y + pad;

            using (var format = new StringFormat())
            {
                format.Trimming = StringTrimming.EllipsisCharacter;
                format.FormatFlags = StringFormatFlags.NoWrap;

                using (var label = new SolidBrush(Palette.SecondaryText(_background)))
                    g.DrawString(tile.Label ?? "", _bodyFont, label,
                                 new RectangleF(x, y, w, LineHeight(_bodyFont)), format);
                y += LineHeight(_bodyFont);

                var visual = new Rectangle(x, y, w, Scale(GaugeSize));
                if (tile.HasGauge) DrawGauge(g, tile, visual);
                else DrawFigures(g, tile, visual);
                y = visual.Bottom + Scale(4);

                format.Alignment = StringAlignment.Center;
                using (var detail = new SolidBrush(Palette.MutedText(_background)))
                    g.DrawString(tile.Detail ?? "", _smallFont, detail,
                                 new RectangleF(x, y, w, LineHeight(_smallFont)), format);
                y += LineHeight(_smallFont);
            }

            DrawChart(g, tile, new Rectangle(x, y, w, Scale(ChartHeight)));
        }

        /// <summary>
        /// A ring around the figure, filled clockwise from twelve o'clock by Gauge on a faint
        /// track. Round caps so the leading end reads as moving, not cut off. The track is
        /// drawn even with no reading, so an unmeasured gauge is visibly an empty one.
        /// </summary>
        void DrawGauge(Graphics g, StatsTile tile, Rectangle box)
        {
            float stroke = Scale(GaugeStroke);
            float size = Math.Min(box.Width, box.Height) - stroke;
            if (size < stroke) return;

            var ring = new RectangleF(box.X + (box.Width - size) / 2f, box.Y + (box.Height - size) / 2f,
                                      size, size);

            using (var track = new Pen(Color.FromArgb(50, _highlight), stroke))
                g.DrawEllipse(track, ring);

            if (tile.Gauge > 0f)
            {
                using (var arc = new Pen(_highlight, stroke))
                {
                    arc.StartCap = LineCap.Round;
                    arc.EndCap = LineCap.Round;
                    g.DrawArc(arc, ring, -90f, 360f * Math.Min(1f, tile.Gauge));
                }
            }

            using (var ink = new SolidBrush(Palette.PrimaryText(_background)))
            using (var format = new StringFormat())
            {
                format.Alignment = StringAlignment.Center;
                format.LineAlignment = StringAlignment.Center;
                g.DrawString(tile.Figure ?? "", _gaugeFont, ink, ring, format);
            }
        }

        /// <summary>
        /// A tile without a gauge: its one or two figures, centred in the same box a ring
        /// would fill, so the detail line and chart below land level with the other tiles'.
        ///
        /// With a FigureTemplate the lines are a block rather than centred one by one. Each
        /// line centred on its own was the first version, and it moved every second: going
        /// from "0 KB/s" to "1023 KB/s" shifted the whole line sideways, and with the two
        /// lines different widths the arrows no longer stood one above the other. The block
        /// is as wide as the template makes the widest line, centred once, with each line's
        /// lead pinned to its left edge and the rest right-aligned to its right - so the
        /// arrows hold a column, the units hold a column, and only the digits change.
        /// </summary>
        void DrawFigures(Graphics g, StatsTile tile, Rectangle box)
        {
            int line = LineHeight(_figureFont);
            int lines = tile.Figure2 != null ? 2 : 1;
            float y = box.Y + (box.Height - line * lines) / 2f;

            using (var ink = new SolidBrush(Palette.PrimaryText(_background)))
            using (var format = new StringFormat())
            {
                format.Trimming = StringTrimming.EllipsisCharacter;
                format.FormatFlags = StringFormatFlags.NoWrap;

                if (tile.FigureTemplate == null)
                {
                    format.Alignment = StringAlignment.Center;
                    g.DrawString(tile.Figure ?? "", _figureFont, ink, new RectangleF(box.X, y, box.Width, line), format);
                    if (tile.Figure2 != null)
                        g.DrawString(tile.Figure2, _figureFont, ink, new RectangleF(box.X, y + line, box.Width, line), format);
                    return;
                }

                // The block's width: the widest lead, a space, and the template. Measured with
                // the typographic format, which leaves out the padding GDI+ adds by default -
                // that padding is per string, so it would otherwise be counted once per part.
                string lead1, rest1, lead2, rest2;
                Split(tile.Figure, out lead1, out rest1);
                Split(tile.Figure2, out lead2, out rest2);

                float leadWidth = Math.Max(FigureWidth(g, lead1), FigureWidth(g, lead2));
                float block = leadWidth + FigureWidth(g, " " + tile.FigureTemplate);
                block = Math.Min(block, box.Width);
                float left = box.X + (box.Width - block) / 2f;

                DrawFigureLine(g, ink, lead1, rest1, left, y, block, line);
                if (tile.Figure2 != null) DrawFigureLine(g, ink, lead2, rest2, left, y + line, block, line);
            }
        }

        void DrawFigureLine(Graphics g, Brush ink, string lead, string rest,
                            float left, float y, float width, int line)
        {
            using (var format = (StringFormat)StringFormat.GenericTypographic.Clone())
            {
                format.FormatFlags |= StringFormatFlags.NoWrap;
                format.LineAlignment = StringAlignment.Near;

                g.DrawString(lead, _figureFont, ink, new PointF(left, y), format);

                format.Alignment = StringAlignment.Far;
                g.DrawString(rest, _figureFont, ink, new RectangleF(left, y, width, line), format);
            }
        }

        /// <summary>A figure's lead, up to its first space, and the rest after it.</summary>
        static void Split(string figure, out string lead, out string rest)
        {
            figure = figure ?? "";
            int space = figure.IndexOf(' ');
            lead = space < 0 ? "" : figure.Substring(0, space);
            rest = space < 0 ? figure : figure.Substring(space + 1);
        }

        float FigureWidth(Graphics g, string text)
        {
            if (string.IsNullOrEmpty(text)) return 0f;
            return g.MeasureString(text, _figureFont, PointF.Empty, StringFormat.GenericTypographic).Width;
        }

        /// <summary>
        /// The last minute as a filled shape for Primary, with a plain line in the text tone
        /// for Secondary. No box around it - a boxed strip read as a progress bar, not a
        /// graph - only a faint baseline, so an empty chart is a floor with nothing on it
        /// rather than nothing at all.
        /// </summary>
        void DrawChart(Graphics g, StatsTile tile, Rectangle area)
        {
            using (var floor = new Pen(Color.FromArgb(60, _highlight), Math.Max(1f, (float)_scale)))
                g.DrawLine(floor, area.X, area.Bottom - 0.5f, area.Right, area.Bottom - 0.5f);

            // Clipped to the area: the point older than the window sits left of it, and is
            // there only so the line enters from the edge.
            Region previous = g.Clip;
            g.SetClip(area);
            try
            {
                PointF[] primary = Plot(tile.Primary, area);
                if (primary.Length >= 2)
                {
                    // Filled down to the floor, so the shape of the last minute reads at a
                    // glance; the line on top keeps the edge crisp where the fill is faint.
                    var outline = new PointF[primary.Length + 2];
                    Array.Copy(primary, outline, primary.Length);
                    outline[primary.Length] = new PointF(primary[primary.Length - 1].X, area.Bottom);
                    outline[primary.Length + 1] = new PointF(primary[0].X, area.Bottom);

                    using (var fill = new SolidBrush(Color.FromArgb(80, _highlight)))
                        g.FillPolygon(fill, outline);
                    using (var pen = new Pen(_highlight, Math.Max(1f, (float)(1.5 * _scale))))
                        g.DrawLines(pen, primary);
                }

                PointF[] secondary = Plot(tile.Secondary, area);
                if (secondary.Length >= 2)
                {
                    using (var pen = new Pen(Palette.SecondaryText(_background), Math.Max(1f, (float)_scale)))
                        g.DrawLines(pen, secondary);
                }
            }
            finally
            {
                g.Clip = previous;
                previous.Dispose();
            }
        }

        /// <summary>
        /// A rounded rectangle. RectangleF so the modern panel's hairline can sit on half-pixel
        /// edges - a 1px line stroked on whole ones straddles two rows - while every other
        /// caller passes a Rectangle and takes the implicit conversion.
        /// </summary>
        static GraphicsPath Rounded(RectangleF r, int radius)
        {
            var path = new GraphicsPath();
            float d = Math.Max(1f, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        /// <summary>Normalised points onto the area, y up from its floor. Empty for none.</summary>
        static PointF[] Plot(IList<PointF> points, Rectangle area)
        {
            if (points == null) return new PointF[0];

            var result = new PointF[points.Count];
            for (int i = 0; i < points.Count; i++)
            {
                float y = Math.Max(0f, Math.Min(1f, points[i].Y));
                result[i] = new PointF(area.X + points[i].X * area.Width,
                                       area.Bottom - y * area.Height);
            }
            return result;
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
            ReleaseSurface();
            if (_shadow != null) { _shadow.Dispose(); _shadow = null; }
            if (_headFont != null) _headFont.Dispose();
            if (_appFont != null) _appFont.Dispose();
            _titleFont.Dispose();
            _bodyFont.Dispose();
            _gaugeFont.Dispose();
            _figureFont.Dispose();
            _smallFont.Dispose();
        }
    }
}

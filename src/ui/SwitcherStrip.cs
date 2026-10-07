using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace DesktopSwitcher
{
    /// <summary>
    /// The numbered button strip that lives in the taskbar.
    ///
    /// A NativeWindow rather than a Form, because a Form cannot become a child of
    /// Shell_TrayWnd. It owns no desktop state: it renders whatever list it is given
    /// and reports intent through events, leaving DesktopService to act.
    ///
    /// ANIMATION: per-button visual state lives in ButtonVisual as floats in 0..1,
    /// separate from the Desktop model, each with the target it is heading for beside
    /// it. SyncVisuals sets targets; a frame timer eases the values toward them and
    /// stops the moment they all arrive. Render stays pure - it reads visual state and
    /// draws, mutating nothing - which is what lets the animator be a timer and a
    /// handful of eases rather than a rewrite.
    ///
    /// Two independent per-button floats can only ever cross-fade, though, so the
    /// underline bar is not one of them: its rectangle is strip-level state, eased as
    /// geometry and drawn once outside the per-button loop, which is what makes it
    /// travel from the old current button to the new one. The M6 note here promised
    /// Render would need no change to animate; that held for the buttons, and did not
    /// anticipate the bar.
    ///
    /// The frame timer drives the hover panel as well, which is a window of its own and
    /// repaints itself. The two motions always begin together - the WM_MOUSEMOVE that
    /// lifts a button's hover tone is the same one that aims the panel at it - so a second
    /// timer would duplicate the elapsed-time bookkeeping to run in lockstep with this one.
    /// OnFrame therefore asks the strip and the panel separately whether they moved, so a
    /// slide happening entirely above the taskbar does not repaint the taskbar for it.
    ///
    /// STATS TILE: optional, after '+', at index _desktops.Count + 1. It has no desktop and
    /// raises no intent; its whole job is the panel, which shows CPU, memory and network and
    /// refreshes once a second while it is up. Hover shows only an ordinary tooltip naming
    /// the tile; the figures come from a click, which pins the panel, and a pinned panel is
    /// where the panel *rests*: it still follows the pointer
    /// to describe other buttons, and wherever it would otherwise have hidden - the pointer
    /// leaving, a click, a desktop change - it returns to the tile instead. The content is
    /// the controller's, through StatsProvider, for the same reason tooltip text is.
    ///
    /// Pinned comes in two strengths. Opened by a click on the tile, it is a popup: a press
    /// anywhere but the tile closes it - see ClickWatcher. Opened by "Keep open" from the
    /// tile's menu, it is kept: clicks elsewhere leave it alone, and only the tile or the
    /// menu again close it. Either way, while the taskbar is not on
    /// screen - auto-hidden, or under a fullscreen app - the panel is put away without
    /// being unpinned, and comes back with the taskbar. See StatsCovered.
    /// </summary>
    sealed class SwitcherStrip : NativeWindow, IDisposable
    {
        /// <summary>Continuous visual state, deliberately decoupled from the model.</summary>
        struct ButtonVisual
        {
            public float Highlight;   // 0 = inactive, 1 = current desktop
            public float Hover;       // 0 = idle,     1 = pointer over
            public float Dot;         // 0 = empty,    1 = has windows

            public float HighlightTarget;
            public float HoverTarget;
            public float DotTarget;
        }

        /// <summary>
        /// How long an already-open panel waits before changing what it says.
        ///
        /// Not a second hover delay - the full delay is paid once, to open the first panel,
        /// and re-charging it per button would wreck the one thing the panel is for, which
        /// is reading along the strip.
        ///
        /// It gates the text and nothing else. For one release it gated everything, and that
        /// was worse than the strobe it fixed: for 80ms the panel sat still, attached to the
        /// button the pointer had left, describing a desktop that was no longer the one
        /// being asked about. A panel that is wrong and motionless reads as broken in a way
        /// a panel that is merely busy does not.
        ///
        /// Splitting it is what makes the delay invisible. The panel starts travelling on
        /// the same mouse message, so something answers the pointer immediately; the text
        /// lands 80ms later, which - animationMs being 80 as well - is about when the glide
        /// finishes. The two arrive together and the wait has nowhere to show itself.
        ///
        /// The cost per crossing is now MoveTo only: no BuildRows, so no MeasureString per
        /// row on the thread that is also running the frame timer. (An earlier note here
        /// claimed a window-inventory query per crossing too. That was wrong - WindowInventory
        /// caches for a second, so a sweep never re-enumerates.)
        /// </summary>
        public const int ReshowMs = 80;

        /// <summary>
        /// How often an open stats panel re-reads its figures. A second is what Task Manager
        /// uses at its normal speed, and it is slow enough that a figure can be read before
        /// it changes. The timer behind it runs only while the stats panel is on screen.
        /// </summary>
        public const int StatsRefreshMs = 1000;

        /// <summary>
        /// How often a pinned panel checks that the taskbar it rests on is still on screen.
        /// Faster than the refresh because the cases it catches are visible: an auto-hide
        /// taskbar slides away a moment after the pointer leaves, and a panel lingering a
        /// full second after it reads as left behind. The check is two cheap calls.
        /// </summary>
        public const int StatsGuardMs = 250;

        /// <summary>
        /// Posted by the ClickWatcher for every mouse press on the system, with the screen
        /// point in lParam. Private to this window class, so WM_APP's range is ours.
        /// </summary>
        const int WM_CLICK_ANYWHERE = Native.WM_APP + 1;

        readonly int _buttonWidth;
        readonly int _plusWidth;
        readonly int _statsWidth;      // 0 means no stats tile at all
        readonly int _barHeight;
        readonly uint _hoverDelay;
        readonly int _tooltipWidth;
        readonly int _animationMs;
        readonly double _dpiScale;

        List<Desktop> _desktops = new List<Desktop>();
        ButtonVisual[] _visuals = new ButtonVisual[0];

        // Which desktops have windows, by Guid - keyed the same way _desktops is read in
        // SyncVisuals, so a set change and an occupancy refresh landing in either order
        // still end up pointing at the right button. Empty until the first refresh, which
        // reads as every button empty rather than every button occupied - the safer of
        // the two wrong answers for the one frame before real data arrives.
        ICollection<Guid> _occupied = new HashSet<Guid>();

        // The underline bar, in strip coordinates. Strip-level rather than per-button
        // because a bar that travels is one object moving, not two fading.
        float _barX, _barWidth, _barLevel;
        float _barXTarget, _barWidthTarget, _barLevelTarget;

        Timer _frameTimer;             // runs only while something is actually moving
        int _lastFrame;                // Environment.TickCount at the last stepped frame

        int _hoverIndex = -1;          // == _desktops.Count means '+', one past it the stats tile
        int _menuIndex = -1;           // button whose context menu is open, by the same index
        bool _trackingMouse;
        bool _disposed;

        bool _statsPinned;             // the panel rests on the stats tile instead of hiding
        bool _statsKept;               // pinned from "Keep open": a click elsewhere does not close it
        bool _showingStats;            // what the open panel says is the stats, not a button's
        bool _statsDismissed;          // unpinned by a click; no hover panel until the pointer leaves the tile
        bool _statsSuppressed;         // pinned, but put away because the taskbar is not on screen
        int _statsShownAt;             // Environment.TickCount the figures were last read
        Timer _statsTimer;             // runs only while the stats panel is up, or suppressed
        ClickWatcher _clicks;          // exists only while pinned

        Color _background;
        Color _highlight;
        Bitmap _buffer;
        Font _font;

        TooltipWindow _tooltip;
        Timer _reshowTimer;            // runs only between a button change and the follow

        public SwitcherStrip(IntPtr parent, Rectangle bounds,
                             int buttonWidth, int plusWidth, int statsWidth, int barHeight,
                             Color background, Color highlight,
                             uint hoverDelay, int tooltipWidth, int animationMs,
                             double dpiScale)
        {
            _buttonWidth = buttonWidth;
            _plusWidth = plusWidth;
            _statsWidth = Math.Max(0, statsWidth);
            _barHeight = barHeight;
            _hoverDelay = hoverDelay;
            _tooltipWidth = tooltipWidth;
            _animationMs = animationMs;
            _dpiScale = dpiScale;
            _background = background;
            _highlight = highlight;

            var cp = new CreateParams();
            cp.Caption = "DesktopSwitcherStrip";
            cp.X = bounds.X;
            cp.Y = bounds.Y;
            cp.Width = bounds.Width;
            cp.Height = bounds.Height;
            cp.Parent = parent;
            cp.Style = Native.WS_CHILD | Native.WS_VISIBLE;
            cp.ExStyle = Native.WS_EX_NOACTIVATE;
            CreateHandle(cp);
        }

        /// <summary>Left click on a desktop button.</summary>
        public event Action<Guid> SwitchRequested;

        /// <summary>Middle click on a desktop button.</summary>
        public event Action<Guid> RemoveRequested;

        /// <summary>Shift + right click on a desktop button - send the active window there.</summary>
        public event Action<Guid> MoveWindowRequested;

        /// <summary>Left click on the '+' button.</summary>
        public event EventHandler CreateRequested;

        /// <summary>
        /// Right click on any button: the index HitTest returned, and that button in screen
        /// coordinates for the menu to anchor to.
        ///
        /// The handler owes this class a MenuClosed() call on every path, including
        /// declining to show a menu at all - the button stays lit while its menu is up, and
        /// that is the only thing that puts it out.
        /// </summary>
        public event Action<int, Rectangle> ContextMenuRequested;

        /// <summary>
        /// Supplies the text for a button's tooltip, by the same index HitTest returns -
        /// so _desktops.Count means the '+' button. Returning null suppresses the tooltip.
        /// The stats tile comes here too, for its hover hint - StatsIndex - while it is not
        /// pinned; its figures come from StatsProvider.
        ///
        /// The strip contributes geometry only. Content needs the desktop model, the
        /// window inventory and the foreground tracker, none of which belong here.
        /// </summary>
        public Func<int, TooltipContent> TooltipProvider { get; set; }

        /// <summary>
        /// Supplies the stats panel's text, told whether the panel is kept open so the title
        /// can say so - a kept panel ignoring clicks elsewhere needs to explain itself, a
        /// popup does not. Called once when the panel opens and again every StatsRefreshMs
        /// while it stays up. Returning null suppresses the panel.
        /// </summary>
        public Func<bool, TooltipContent> StatsProvider { get; set; }

        /// <summary>
        /// The stats panel was pinned, kept or closed - (pinned, kept) - from a click on the
        /// tile, a click elsewhere, or SetStatsPinned. The strip is rebuilt from scratch after
        /// an Explorer restart, a theme change or an unlock, and the state would die with it;
        /// the controller keeps it from here and hands it back to the new strip.
        /// </summary>
        public event Action<bool, bool> StatsPinChanged;

        // --- layout -----------------------------------------------------------

        public static int MeasureWidth(int desktopCount, int buttonWidth, int plusWidth, int statsWidth)
        {
            return desktopCount * buttonWidth + plusWidth + Math.Max(0, statsWidth);
        }

        public int Width { get { return MeasureWidth(_desktops.Count, _buttonWidth, _plusWidth, _statsWidth); } }

        /// <summary>
        /// The stats tile's index, by the same numbering HitTest uses, or -1 when there is no
        /// tile. Public so the controller can tell its context menu apart from '+'.
        /// </summary>
        public int StatsIndex { get { return _statsWidth > 0 ? _desktops.Count + 1 : -1; } }

        /// <summary>Every button, '+' and the stats tile included: what _visuals is sized to.</summary>
        int SlotCount { get { return _desktops.Count + (_statsWidth > 0 ? 2 : 1); } }

        Rectangle ButtonBounds(int index, int height)
        {
            if (index < _desktops.Count)
                return new Rectangle(index * _buttonWidth, 0, _buttonWidth, height);

            int plusX = _desktops.Count * _buttonWidth;
            if (index == _desktops.Count || _statsWidth <= 0)
                return new Rectangle(plusX, 0, _plusWidth, height);

            return new Rectangle(plusX + _plusWidth, 0, _statsWidth, height);
        }

        int HitTest(int x, int y)
        {
            if (x < 0) return -1;

            int buttonsWidth = _desktops.Count * _buttonWidth;
            if (x < buttonsWidth) return x / _buttonWidth;
            if (x < buttonsWidth + _plusWidth) return _desktops.Count;
            if (x < buttonsWidth + _plusWidth + _statsWidth) return _desktops.Count + 1;
            return -1;
        }

        /// <summary>
        /// A button in screen coordinates, spanning the full height of the strip. What the
        /// hover panel anchors to and what the context menu opens against, so the two come
        /// out of the same edge of the same rectangle.
        ///
        /// Empty when the strip has no window rect, which means it is being torn down.
        /// </summary>
        Rectangle AnchorFor(int index)
        {
            Native.RECT strip;
            if (!Native.GetWindowRect(Handle, out strip)) return Rectangle.Empty;

            Rectangle button = ButtonBounds(index, strip.Height);
            return new Rectangle(strip.Left + button.X, strip.Top, button.Width, strip.Height);
        }

        /// <summary>
        /// The whole strip in screen coordinates. What the removal confirmation anchors
        /// to - unlike a button anchor, it does not travel with whichever button was
        /// clicked, since the dialog is modal and has no pointer to follow.
        ///
        /// Empty when the strip has no window rect, which means it is being torn down.
        /// </summary>
        public Rectangle ScreenBounds()
        {
            Native.RECT strip;
            if (!Native.GetWindowRect(Handle, out strip)) return Rectangle.Empty;

            return new Rectangle(strip.Left, strip.Top, strip.Width, strip.Height);
        }

        // --- model ------------------------------------------------------------

        /// <summary>
        /// Replaces the rendered list. Visual state is rebuilt to match, preserving
        /// nothing across a set change since indices may refer to different desktops.
        /// </summary>
        public void SetDesktops(IList<Desktop> desktops)
        {
            // Whether these are the same desktops in the same order, decided before the
            // list is replaced. It is the difference between animating and snapping: the
            // bar sliding from button 3 to button 2 means something when they are the
            // desktops they were, and means nothing when a removal has just renumbered
            // everything after the one that went. Same for the first list a freshly built
            // strip is handed, after login or an Explorer restart - it must come up
            // already correct rather than sliding in from the left edge.
            bool sameSet = SameSet(_desktops, desktops);

            _desktops = new List<Desktop>(desktops);

            if (_visuals.Length != SlotCount)
                _visuals = new ButtonVisual[SlotCount];

            if (_hoverIndex >= SlotCount) _hoverIndex = -1;
            if (_menuIndex >= SlotCount) _menuIndex = -1;

            // Anything the panel was saying about desktop N may now describe a different
            // desktop, since a removal renumbers everything after it. The stats panel
            // describes no desktop and is left exactly where it is: this runs on every
            // desktop switch, and a pinned panel that blinked out and back each time would
            // be pinned in name only. Not even re-anchored, because it does not need to be -
            // the strip is right-anchored and the tile is its last button, so the tile's
            // place on screen does not move when the count does. (Re-anchoring here would be
            // actively wrong: the controller resizes the strip only after this returns, so
            // the window rect AnchorFor reads is still the old one.)
            if (!ShowingStats) RestOrHide();

            SyncVisuals();
            if (!sameSet) Settle();

            Invalidate();
        }

        /// <summary>Same desktops, same order. Identity is the Guid, never the index.</summary>
        static bool SameSet(IList<Desktop> a, IList<Desktop> b)
        {
            if (a.Count != b.Count) return false;

            for (int i = 0; i < a.Count; i++)
                if (a[i].Id != b[i].Id) return false;

            return true;
        }

        /// <summary>
        /// Points visual state at the model. The only place that decides what "current"
        /// and "hovered" look like, which is why it sets targets rather than values -
        /// every caller that changes what the strip should look like already comes
        /// through here, so every caller animates for free.
        /// </summary>
        void SyncVisuals()
        {
            int current = -1;

            for (int i = 0; i < _visuals.Length; i++)
            {
                bool isCurrent = i < _desktops.Count && _desktops[i].IsCurrent;
                if (isCurrent) current = i;

                _visuals[i].HighlightTarget = isCurrent ? 1f : 0f;

                // A button whose menu is open reads as hovered whatever the pointer is
                // doing, so it stays visibly the one the menu belongs to. A pinned stats
                // tile likewise, so it is visibly the thing holding the panel up - and the
                // thing to click to put it away.
                bool pinned = _statsPinned && i == StatsIndex;
                _visuals[i].HoverTarget = (i == _hoverIndex || i == _menuIndex || pinned) ? 1f : 0f;

                // '+' and the stats tile have no desktop and so are never in _occupied - i
                // at or beyond _desktops.Count reads false here the same way isCurrent does.
                bool occupied = i < _desktops.Count && _occupied.Contains(_desktops[i].Id);
                _visuals[i].DotTarget = occupied ? 1f : 0f;
            }

            if (current >= 0)
            {
                Rectangle bounds = ButtonBounds(current, 0);
                _barXTarget = bounds.X;
                _barWidthTarget = bounds.Width;
                _barLevelTarget = 1f;
            }
            else
            {
                // No current desktop - mid-reconcile, or the shell went away. Fade the bar
                // out where it stands: sliding it to x=0 would point it at a button that is
                // not current either, which is worse than pointing at nothing.
                _barLevelTarget = 0f;
            }

            Animate();
        }

        public void SetBackground(Color color)
        {
            if (_background == color) return;
            _background = color;
            Invalidate();
        }

        /// <summary>
        /// Which desktops have windows, refreshed independently of the desktop set - a
        /// window opening or closing does not change which buttons exist. Null holds
        /// whatever is already on screen: the shell being unavailable mid-refresh is not
        /// the same thing as every desktop actually being empty.
        ///
        /// Goes through SyncVisuals like every other change of intent, so the dot fades
        /// rather than snaps, and Invalidate is still needed alongside it for the
        /// animationMs = 0 case - see the same pairing in OnMouseMove.
        /// </summary>
        public void SetOccupancy(ICollection<Guid> occupied)
        {
            if (occupied == null) return;
            _occupied = occupied;
            SyncVisuals();
            Invalidate();
        }

        // --- animation --------------------------------------------------------

        /// <summary>
        /// Starts the frame timer, unless there is nothing to animate. Called from
        /// SyncVisuals, so every change of intent gets one of these and nothing else
        /// has to remember to.
        /// </summary>
        void Animate()
        {
            if (_disposed) return;

            // animationMs = 0 means off: apply the targets where they stand and never
            // create a timer at all. The panel is included, so the one setting means the
            // same thing to everything that moves.
            if (_animationMs <= 0) { Settle(); return; }

            if (Settled()) return;

            if (_frameTimer == null)
            {
                _frameTimer = new Timer();
                _frameTimer.Interval = Motion.FrameMs;
                _frameTimer.Tick += OnFrame;
            }

            if (!_frameTimer.Enabled)
            {
                // Only when starting from stopped. Re-basing on every retarget would throw
                // away the time since the last frame, so a moving pointer - which retargets
                // on every WM_MOUSEMOVE - would keep resetting the clock and stall the
                // animation it is supposed to be driving.
                _lastFrame = Environment.TickCount;
                _frameTimer.Start();
            }
        }

        void OnFrame(object sender, EventArgs e)
        {
            int now = Environment.TickCount;

            // Unchecked because TickCount wraps to int.MinValue after ~24.9 days of
            // uptime, and this subtraction is still the right elapsed time across the
            // wrap. This process runs from login to logout, so it will see one.
            int elapsed = unchecked(now - _lastFrame);
            if (elapsed <= 0) return;

            _lastFrame = now;
            float rate = Motion.Rate(elapsed, _animationMs);

            // Stepped separately and asked separately. The panel is a window of its own and
            // repaints itself as it moves, so a strip that repainted whenever *anything*
            // moved would redraw the taskbar sixty times a second for a slide happening
            // entirely above it.
            bool strip = Step(rate);
            bool panel = _tooltip != null && _tooltip.Step(rate);

            if (strip) Invalidate();
            if (strip || panel) return;

            // Everything is on its target. A permanently ticking 60Hz timer that repaints
            // nothing is not something a taskbar utility gets to do.
            _frameTimer.Stop();
        }

        /// <summary>Steps every animated value. True when any of them moved.</summary>
        bool Step(float rate)
        {
            bool moved = false;

            // |= and not ||=, deliberately: every value must be stepped, not just the ones
            // before the first that moved.
            for (int i = 0; i < _visuals.Length; i++)
            {
                moved |= Motion.Ease(ref _visuals[i].Highlight, _visuals[i].HighlightTarget, rate, Motion.ToneEpsilon);
                moved |= Motion.Ease(ref _visuals[i].Hover, _visuals[i].HoverTarget, rate, Motion.ToneEpsilon);
                moved |= Motion.Ease(ref _visuals[i].Dot, _visuals[i].DotTarget, rate, Motion.ToneEpsilon);
            }

            moved |= Motion.Ease(ref _barX, _barXTarget, rate, Motion.PixelEpsilon);
            moved |= Motion.Ease(ref _barWidth, _barWidthTarget, rate, Motion.PixelEpsilon);
            moved |= Motion.Ease(ref _barLevel, _barLevelTarget, rate, Motion.ToneEpsilon);

            return moved;
        }

        bool Settled()
        {
            for (int i = 0; i < _visuals.Length; i++)
            {
                if (_visuals[i].Highlight != _visuals[i].HighlightTarget) return false;
                if (_visuals[i].Hover != _visuals[i].HoverTarget) return false;
                if (_visuals[i].Dot != _visuals[i].DotTarget) return false;
            }

            if (_tooltip != null && !_tooltip.Settled) return false;

            return _barX == _barXTarget && _barWidth == _barWidthTarget
                && _barLevel == _barLevelTarget;
        }

        /// <summary>
        /// Arrive now, without animating. What a desktop set change wants, and what
        /// animation being switched off means.
        /// </summary>
        void Settle()
        {
            Step(1f);
            if (_tooltip != null) _tooltip.Settle();
            if (_frameTimer != null) _frameTimer.Stop();
        }

        public void Invalidate()
        {
            if (Handle != IntPtr.Zero)
                Native.InvalidateRect(Handle, IntPtr.Zero, false);
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

                if (_buffer == null || _buffer.Width != w || _buffer.Height != h)
                {
                    if (_buffer != null) _buffer.Dispose();
                    _buffer = new Bitmap(w, h);
                }

                using (var g = Graphics.FromImage(_buffer))
                {
                    Render(g, w, h);
                }

                using (var target = Graphics.FromHdc(hdc))
                {
                    target.DrawImageUnscaled(_buffer, 0, 0);
                }
            }
            catch (Exception ex)
            {
                Log.Write("strip: paint failed - " + ex.Message);
            }
            finally
            {
                Native.EndPaint(Handle, ref ps);
            }
        }

        /// <summary>
        /// Pure: reads model and visual state, draws, mutates nothing. Everything that
        /// moves is a float the animator has already stepped, which is what keeps the
        /// animator to a timer and a handful of eases.
        /// </summary>
        void Render(Graphics g, int width, int height)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            using (var back = new SolidBrush(_background))
                g.FillRectangle(back, 0, 0, width, height);

            EnsureFont(height);

            for (int i = 0; i < _desktops.Count; i++)
                DrawButton(g, ButtonBounds(i, height), _desktops[i].Number.ToString(), _visuals[i]);

            int plusIndex = _desktops.Count;
            if (plusIndex < _visuals.Length)
                DrawButton(g, ButtonBounds(plusIndex, height), "+", _visuals[plusIndex]);

            int statsIndex = StatsIndex;
            if (statsIndex >= 0 && statsIndex < _visuals.Length)
                DrawStatsButton(g, ButtonBounds(statsIndex, height), _visuals[statsIndex]);

            DrawBar(g, height);
        }

        /// <summary>
        /// The underline under the current desktop, drawn once for the whole strip rather
        /// than per button - it is one bar that travels, and a bar mid-travel belongs to
        /// no button. After the cells, so a neighbour's fill cannot paint over it as it
        /// crosses.
        /// </summary>
        void DrawBar(Graphics g, int height)
        {
            if (_barLevel <= 0.001f || _barWidth < 1f) return;

            int barHeight = Math.Max(2, (int)Math.Round(_barHeight * _barLevel));
            var bar = new Rectangle((int)Math.Round(_barX), height - barHeight,
                                    (int)Math.Round(_barWidth), barHeight);

            using (var brush = new SolidBrush(_highlight))
                g.FillRectangle(brush, bar);
        }

        void DrawButton(Graphics g, Rectangle bounds, string caption, ButtonVisual visual)
        {
            DrawCell(g, bounds, visual);

            using (var brush = new SolidBrush(Palette.RampText(_background, Emphasis(visual))))
            using (var format = new StringFormat())
            {
                format.Alignment = StringAlignment.Center;
                format.LineAlignment = StringAlignment.Center;

                // Nudge up so the glyph looks centred in the space above the bar.
                var textBounds = new Rectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height - _barHeight);
                g.DrawString(caption, _font, brush, textBounds, format);

                DrawDot(g, textBounds, caption, visual.Dot);
            }
        }

        /// <summary>The cell behind a button's glyph, lifted by hover and current.</summary>
        void DrawCell(Graphics g, Rectangle bounds, ButtonVisual visual)
        {
            // Hover and current both separate the cell from the bar; current adds the
            // underline. Which way that separation goes is Palette's problem - on a light
            // taskbar the cell darkens instead of lightening.
            float lift = Math.Max(visual.Hover * 0.55f, visual.Highlight * 0.85f);
            if (lift > 0.001f)
            {
                using (var fill = new SolidBrush(Palette.Lift(_background, lift * 0.10f)))
                    g.FillRectangle(fill, bounds);
            }

            // The underline is not drawn here. See DrawBar: it travels between buttons,
            // and a bar drawn inside this loop can only fade out of one cell and into the
            // next, which reads as two bars rather than one moving.
        }

        /// <summary>How far up the text ramp a glyph sits, 0 resting to 1 full.</summary>
        static float Emphasis(ButtonVisual visual)
        {
            // Current desktop reads at full strength; hover carries an inactive one part of
            // the way there. HoverShare is the ratio the two tone steps used to stand in
            // before Palette owned the ramp - 30 of the 55 between resting and full - kept
            // exactly so a dark taskbar renders the same tones it always did.
            const float HoverShare = 30f / 55f;
            return Math.Min(1f, visual.Highlight + visual.Hover * HoverShare);
        }

        /// <summary>
        /// The stats tile: a dot with two arcs either side, "(( . ))" - a broadcast mark
        /// with the mast taken out.
        ///
        /// The mast was the first version on the table and was dropped on purpose: a mast
        /// with arcs is the network icon, and Windows draws its own a hundred pixels to the
        /// right in the tray. Dot and arcs alone keep the look and lose the resemblance.
        /// It replaced a three-bar chart drawn in the grey text ramp.
        ///
        /// In the accent colour rather than the text ramp - the one mark on the strip that is
        /// not a number gets the one colour that is not grey. Muted at rest and full strength
        /// when hovered or pinned, which carries the same "this one is live" cue the ramp
        /// gives the numbers. Full accent at rest competed with the current-desktop underline,
        /// which is the only other thing on the strip in that colour.
        ///
        /// Drawn rather than a character: no Segoe UI glyph has this shape, and the symbol
        /// fonts that do would fall back to colour emoji or ignore the accent entirely.
        /// Sized off the font, not the cell, so it is as tall as a digit beside it and centred
        /// on the same line - textBounds is the same nudged-up rectangle the numbers centre in.
        /// </summary>
        void DrawStatsButton(Graphics g, Rectangle bounds, ButtonVisual visual)
        {
            DrawCell(g, bounds, visual);

            var textBounds = new Rectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height - _barHeight);

            float cx = textBounds.X + textBounds.Width / 2f;
            float cy = textBounds.Y + textBounds.Height / 2f;

            // Outer arc as tall as a digit is, near enough; the inner one and the dot are
            // proportions of it so the mark scales as one shape with DPI.
            float glyph = Math.Max(8f, _font.Size * 0.85f);
            float outer = glyph * 0.55f;
            float inner = glyph * 0.32f;
            float dot = Math.Max(1.5f, glyph * 0.12f);
            float stroke = Math.Max(1.25f, (float)(_dpiScale * 1.3));

            // 65% at rest, 100% hovered or pinned. Alpha rather than a blend toward the
            // background so it holds the same hue on a light taskbar as on a dark one.
            float strength = 0.65f + 0.35f * Math.Min(1f, visual.Hover);
            Color ink = Color.FromArgb((int)(255 * strength), _highlight);

            using (var pen = new Pen(ink, stroke))
            using (var brush = new SolidBrush(ink))
            {
                // Round caps, or the arc ends are square stubs that read as a broken circle.
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;

                g.FillEllipse(brush, cx - dot, cy - dot, dot * 2, dot * 2);

                // 90 degrees each, centred on the horizontal: wide enough to read as arcs,
                // short enough that the pairs never close into rings.
                foreach (float r in new float[] { inner, outer })
                {
                    g.DrawArc(pen, cx - r, cy - r, r * 2, r * 2, -45f, 90f);   // right
                    g.DrawArc(pen, cx - r, cy - r, r * 2, r * 2, 135f, 90f);   // left
                }
            }
        }

        /// <summary>
        /// A muted dot beside the number, faded in by visual.Dot: dim when the desktop has
        /// windows, invisible when it does not.
        ///
        /// Beside the glyph rather than under it. Under was tried first and cost two
        /// rounds of fighting the same problem: the button is barely 30px tall, so any
        /// vertical space taken for the dot comes straight out of the number's centring,
        /// and the dot ended up either visibly nudging the glyph up or sitting jammed
        /// against the underline. The button has width to spare that it does not have
        /// height for, so the dot goes there instead and the glyph's position is
        /// untouched - textBounds is exactly what it was before this feature existed.
        ///
        /// Measures the caption to place the dot off its actual right edge rather than a
        /// fixed offset, because Number can be two digits - up to 32 desktops - and a
        /// fixed offset tuned for one digit would sit on top of the second.
        ///
        /// A fixed tone rather than one that rides the hover/current ramp: it answers "is
        /// this desktop occupied", a fact about the desktop and not about the pointer, and
        /// riding the ramp would read as a second state mark competing with the underline.
        /// </summary>
        void DrawDot(Graphics g, Rectangle textBounds, string caption, float amount)
        {
            if (amount <= 0.001f) return;

            int diameter = Math.Max(2, _barHeight);
            int gap = Math.Max(2, _barHeight);

            float halfGlyph = g.MeasureString(caption, _font).Width / 2f;
            int x = textBounds.X + textBounds.Width / 2 + (int)Math.Ceiling(halfGlyph) + gap;

            // Clamped to the button's own right edge so a two-digit number on a narrow
            // button cannot push the dot into the next button over.
            int rightLimit = textBounds.Right - diameter;
            if (x > rightLimit) x = rightLimit;

            int y = textBounds.Y + textBounds.Height / 2 - diameter / 2;

            Color muted = Palette.MutedText(_background);
            using (var brush = new SolidBrush(Color.FromArgb((int)(255 * amount), muted)))
                g.FillEllipse(brush, x, y, diameter, diameter);
        }

        void EnsureFont(int height)
        {
            // Sized in pixels from the taskbar height, so it tracks DPI without
            // depending on the device context's notion of points.
            float size = Math.Max(9f, height * 0.30f);
            if (_font != null && Math.Abs(_font.Size - size) < 0.5f) return;

            if (_font != null) _font.Dispose();
            _font = new Font("Segoe UI", size, FontStyle.Regular, GraphicsUnit.Pixel);
        }

        // --- input ------------------------------------------------------------

        /// <summary>
        /// Asks for WM_MOUSELEAVE and one WM_MOUSEHOVER after the delay.
        ///
        /// Both are one-shot, and TME_HOVER only fires while the pointer stays inside a
        /// small system-defined rect. Moving from one button to the next can easily stay
        /// inside that rect, so this must be re-armed whenever the hovered button
        /// changes or the hover simply never fires again.
        /// </summary>
        void ArmTracking()
        {
            var tme = new Native.TRACKMOUSEEVENT();
            tme.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.TRACKMOUSEEVENT));
            tme.dwFlags = Native.TME_LEAVE | Native.TME_HOVER;
            tme.hwndTrack = Handle;
            tme.dwHoverTime = _hoverDelay;
            Native.TrackMouseEvent(ref tme);
            _trackingMouse = true;
        }

        void OnMouseMove(int x, int y)
        {
            if (!_trackingMouse) ArmTracking();

            int index = HitTest(x, y);
            if (index == _hoverIndex) return;

            _hoverIndex = index;
            _statsDismissed = false;   // off the tile, so the click that dismissed it is done
            SyncVisuals();
            Invalidate();

            // Restart the delay against the button now under the pointer.
            ArmTracking();

            if (index < 0) { RestOrHide(); return; }

            // An open panel follows the pointer in two speeds. The geometry goes now, so it
            // is already travelling before the pointer has settled; the text waits out
            // ReshowMs, so a sweep along the strip is one continuous glide carrying one
            // change of text rather than a flip-book of them.
            MoveTooltip();
            QueueReshow();
        }

        void OnMouseLeave()
        {
            _trackingMouse = false;

            // Cleared before RestOrHide, not after: a pinned panel's refresh decides whether
            // to keep going by where the pointer is, and it has to see the pointer as gone.
            bool wasHovering = _hoverIndex != -1;
            _hoverIndex = -1;
            _statsDismissed = false;

            RestOrHide();

            if (!wasHovering) return;

            // SyncVisuals keeps a pinned button lit, so opening a menu - which takes the
            // pointer off the strip and fires this - does not darken the button under it.
            SyncVisuals();
            Invalidate();
        }

        void OnClick(int x, int y, MouseButtons button, bool shift)
        {
            // Whatever the click does, the panel describing it is now stale.
            HideTooltip();
            ArmTracking();

            int index = HitTest(x, y);
            if (index < 0) { RestOrHide(); return; }

            // The menu is the touchpad's only way to the actions the other buttons carry,
            // so it answers on every button, '+' included. With nothing listening - the
            // menu turned off in config - the click falls through to what right click did
            // before the menu existed.
            if (button == MouseButtons.Right && !shift && OpenMenu(index)) return;

            // Before the '+' check, which would otherwise claim it: the tile is also an
            // index at or past _desktops.Count.
            if (index == StatsIndex)
            {
                // Only left click means anything here. Pinning shows the panel at once,
                // without the hover delay - the click is the request.
                if (button == MouseButtons.Left)
                {
                    bool pin = !_statsPinned;
                    SetStatsPinned(pin);

                    // Unpinning is a request to put the panel away, but the pointer is still
                    // on the tile and tracking was re-armed above, so the hover would put it
                    // straight back 400ms later and the click would look like it did nothing.
                    // Held off until the pointer leaves the tile.
                    if (!pin) _statsDismissed = true;
                }
                else RestOrHide();
                return;
            }

            if (index >= _desktops.Count)
            {
                if (button == MouseButtons.Left) Raise(CreateRequested);
            }
            else
            {
                Guid id = _desktops[index].Id;
                switch (button)
                {
                    case MouseButtons.Left:   Raise(SwitchRequested, id); break;
                    case MouseButtons.Right:  Raise(MoveWindowRequested, id); break;   // shift held
                    case MouseButtons.Middle: Raise(RemoveRequested, id); break;
                }
            }

            // The click hid whatever was open. Pinned, the panel goes back to the tile -
            // a click that changes nothing, like the current desktop's own button, would
            // otherwise leave it dark until the pointer next left the strip.
            RestOrHide();
        }

        // --- context menu -----------------------------------------------------

        /// <summary>False when there is no menu to open, leaving the click to its old meaning.</summary>
        bool OpenMenu(int index)
        {
            if (ContextMenuRequested == null) return false;

            Rectangle anchor = AnchorFor(index);
            if (anchor.Width == 0) return false;

            // Pinned before the handler runs, because the handler shows the menu and the
            // button must already be lit underneath it.
            _menuIndex = index;
            SyncVisuals();
            Invalidate();

            try
            {
                ContextMenuRequested(index, anchor);
            }
            catch (Exception ex)
            {
                // Nothing is going to close a menu that was never shown, so let the button go.
                Log.Write("strip: menu handler threw - " + ex.Message);
                MenuClosed();
            }

            return true;
        }

        /// <summary>
        /// The menu raised by ContextMenuRequested has gone. Idempotent, and required on
        /// every path out of the handler - including one that showed nothing.
        /// </summary>
        public void MenuClosed()
        {
            if (_menuIndex == -1) return;

            _menuIndex = -1;

            // Tracking lapsed while the menu held the pointer, and no mouse message is owed
            // to us just because the menu closed. Re-read where the pointer actually is, or
            // the strip sits dead - unlit, no panel - until it leaves and comes back.
            _hoverIndex = HitTestScreen(Cursor.Position);
            _trackingMouse = false;
            if (_hoverIndex >= 0) ArmTracking();

            SyncVisuals();
            Invalidate();

            // The panel declined to show while the menu was up. A pinned one comes back now
            // rather than waiting for the pointer to leave.
            if (_statsPinned) ShowStats();
        }

        int HitTestScreen(Point screen)
        {
            var point = new Native.POINT();
            point.X = screen.X;
            point.Y = screen.Y;
            if (!Native.ScreenToClient(Handle, ref point)) return -1;

            Native.RECT client;
            if (!Native.GetWindowRect(Handle, out client)) return -1;
            if (point.Y < 0 || point.Y >= client.Height) return -1;

            return HitTest(point.X, point.Y);
        }

        // --- tooltip ----------------------------------------------------------

        /// <summary>
        /// Schedules the open panel onto the button now under the pointer, ReshowMs from
        /// now, restarting the wait if the pointer crosses another button first - so a
        /// sweep along the strip resolves to a single panel. See ReshowMs.
        ///
        /// Does nothing when no panel is open: the first one is WM_MOUSEHOVER's to show,
        /// after the full delay, and this must not become a second route to opening one.
        ///
        /// Only OnMouseMove calls this, and only when the hovered button actually changed,
        /// so the wait is measured from entering a button rather than from the last mouse
        /// message. A pointer drifting about inside one button cannot defer the panel
        /// indefinitely.
        /// </summary>
        void QueueReshow()
        {
            if (_disposed || _tooltip == null || !_tooltip.IsVisible) return;

            if (_reshowTimer == null)
            {
                _reshowTimer = new Timer();
                _reshowTimer.Interval = ReshowMs;
                _reshowTimer.Tick += OnReshow;
            }

            // Stop before Start, always: assigning Enabled = true while it is already true
            // is a no-op and would leave the original countdown running, which is the exact
            // opposite of the restart this needs.
            _reshowTimer.Stop();
            _reshowTimer.Start();
        }

        void OnReshow(object sender, EventArgs e)
        {
            // One-shot. ShowTooltip stops it too, but not on the path where it bails out.
            _reshowTimer.Stop();
            ShowTooltip();
        }

        void CancelReshow()
        {
            if (_reshowTimer != null) _reshowTimer.Stop();
        }

        void ShowTooltip()
        {
            // Whatever was queued, this is it happening - by hover delay, by re-show timer
            // or by a caller that got there first.
            CancelReshow();

            if (_disposed || _hoverIndex < 0) return;

            // Hovering the tile gets an ordinary tooltip saying what it is, from
            // TooltipProvider like any other button; the figures are only ever shown by a
            // click. A live readout appearing on hover turned out to be more than anyone
            // passing the pointer along the strip asked for. Pinned, the panel is already the
            // stats and stays the stats - swapping it for the hint would read as the click
            // having been undone. And just after a click put it away, nothing at all: the
            // hint popping up in its place would look like the panel refusing to close.
            if (_hoverIndex == StatsIndex)
            {
                if (_statsPinned) { ShowStats(); return; }
                if (_statsDismissed) return;
            }

            if (TooltipProvider == null) return;

            // The menu already says what this button can do, and the panel would only be in
            // front of it or behind it.
            if (_menuIndex >= 0) return;

            TooltipContent content;
            try
            {
                content = TooltipProvider(_hoverIndex);
            }
            catch (Exception ex)
            {
                Log.Write("strip: tooltip provider threw - " + ex.Message);
                return;
            }

            if (content == null) { HideTooltip(); return; }

            EnsureTooltip();

            // The button in screen coordinates: the panel anchors to it, and the accent
            // stub lines up under it.
            Rectangle anchor = AnchorFor(_hoverIndex);
            if (anchor.Width == 0) return;

            _tooltip.Show(content, anchor);

            // It says something about a button now, so the stats refresh has nothing left
            // to refresh and must not drag the panel back to the tile a second from now.
            _showingStats = false;
            StopStatsRefresh();

            // Show only retargets - a panel already open now has somewhere new to be, and
            // this is what puts a frame timer behind it. SyncVisuals does the same job for
            // every other kind of change; the panel is the one that does not go through it.
            Animate();
        }

        /// <summary>
        /// Moves an open panel onto the button now under the pointer, without disturbing
        /// what it says. The immediate half of the pair; QueueReshow is the other.
        /// </summary>
        void MoveTooltip()
        {
            if (_disposed || _tooltip == null || !_tooltip.IsVisible) return;

            // Same reason ShowTooltip declines: the menu already answers for this button, and
            // a panel sliding out from under it is noise.
            if (_menuIndex >= 0) return;

            Rectangle anchor = AnchorFor(_hoverIndex);
            if (anchor.Width == 0) return;

            _tooltip.MoveTo(anchor);
            Animate();
        }

        /// <summary>
        /// The single hide path, which makes it the single place a queued re-show has to be
        /// dropped. Every caller that invalidates the panel - leaving the strip, clicking,
        /// a desktop set change - already comes through here, so none of them has to know
        /// the timer exists.
        /// </summary>
        void HideTooltip()
        {
            CancelReshow();
            StopStatsRefresh();
            _showingStats = false;
            _statsSuppressed = false;   // with the timer stopped, nothing would bring it back
            if (_tooltip != null) _tooltip.Hide();
        }

        void EnsureTooltip()
        {
            if (_tooltip == null)
                _tooltip = new TooltipWindow(_background, _highlight, _tooltipWidth, _dpiScale);
        }

        // --- stats panel ------------------------------------------------------

        /// <summary>The open panel is showing the stats, as opposed to a button or nothing.</summary>
        bool ShowingStats
        {
            get { return _showingStats && _tooltip != null && _tooltip.IsVisible; }
        }

        /// <summary>
        /// Whether the stats panel is on screen and taking its own readings once a second.
        /// The controller skips its reconcile-tick reading while this is true: two callers
        /// sampling on unrelated clocks, with MinIntervalMs settling the collisions, made
        /// the window each rate was measured over wander between 0.6s and 1.4s, and the CPU
        /// figure jumped about with it. One sampler keeps the window a steady second.
        /// </summary>
        public bool StatsShowing { get { return ShowingStats; } }

        /// <summary>
        /// Where the panel goes when nothing is asking for it: back to the tile when pinned,
        /// away otherwise. Every place that used to hide the panel unconditionally - leaving
        /// the strip, a click, a desktop set change - comes through here instead, so none of
        /// them has to know a pin exists.
        /// </summary>
        void RestOrHide()
        {
            if (_statsPinned && StatsIndex >= 0) ShowStats();
            else HideTooltip();
        }

        /// <summary>
        /// Pins the stats panel as a popup, or unpins it. Pinning shows it now, unpinning puts
        /// it away now. A no-op when there is no tile.
        /// </summary>
        public void SetStatsPinned(bool pinned)
        {
            SetStatsPinned(pinned, false);
        }

        /// <summary>
        /// Pins the stats panel, kept or as a popup, or unpins it. A kept panel has no click
        /// watch behind it at all, rather than one that ignores what it sees: the hook is a
        /// system-wide cost, and a panel that will not act on clicks has no reason to pay it.
        /// Switching a popup to kept, or back, leaves the panel exactly where it is.
        /// </summary>
        public void SetStatsPinned(bool pinned, bool kept)
        {
            if (_disposed || StatsIndex < 0) pinned = false;
            if (!pinned) kept = false;
            if (pinned == _statsPinned && kept == _statsKept) return;

            bool wasPinned = _statsPinned;
            _statsPinned = pinned;
            _statsKept = kept;
            _statsDismissed = false;
            SyncVisuals();
            Invalidate();

            WatchClicks(pinned && !kept);

            // Re-shown on a change of strength too, not only on opening, so the title picks
            // up or drops "(kept open)" now rather than at the next refresh.
            if (pinned) ShowStats();
            else if (wasPinned && (ShowingStats || _statsSuppressed)) HideTooltip();

            if (StatsPinChanged != null)
            {
                try { StatsPinChanged(pinned, kept); }
                catch (Exception ex) { Log.Write("strip: handler threw - " + ex.Message); }
            }
        }

        /// <summary>Whether the panel is kept open, as opposed to open as a popup or closed.</summary>
        public bool StatsKept { get { return _statsKept; } }

        /// <summary>
        /// Puts the stats on the panel, anchored to the tile, and keeps them current. Safe
        /// to call on a panel already showing them - that is exactly what each refresh is.
        /// </summary>
        void ShowStats()
        {
            CancelReshow();

            int index = StatsIndex;
            if (_disposed || index < 0 || StatsProvider == null) return;

            // Same as ShowTooltip: the menu is up and answering for its button. MenuClosed
            // brings a pinned panel back.
            if (_menuIndex >= 0) return;

            // Checked here rather than only on the guard tick, because this is also how a
            // desktop change, a closed menu and a rebuild bring the panel back - and a
            // Win+Ctrl+Arrow from inside a fullscreen game must not put it over the game.
            if (StatsCovered()) { SuppressStats(); return; }

            TooltipContent content;
            try
            {
                content = StatsProvider(_statsKept);
            }
            catch (Exception ex)
            {
                Log.Write("strip: stats provider threw - " + ex.Message);
                return;
            }

            if (content == null) { HideTooltip(); return; }

            EnsureTooltip();

            Rectangle anchor = AnchorFor(index);
            if (anchor.Width == 0) return;

            _tooltip.Show(content, anchor);
            _showingStats = true;
            _statsSuppressed = false;
            _statsShownAt = Environment.TickCount;
            StartStatsRefresh();

            // A panel arriving from another button slides here, and the frame timer is
            // what carries it - see the same call at the end of ShowTooltip.
            Animate();
        }

        void StartStatsRefresh()
        {
            if (_disposed) return;

            if (_statsTimer == null)
            {
                _statsTimer = new Timer();
                _statsTimer.Interval = StatsGuardMs;
                _statsTimer.Tick += OnStatsRefresh;
            }

            // Not restarted if already running: a refresh is itself a call to ShowStats, and
            // restarting from inside the tick it is handling would be harmless, but a hover
            // re-show landing mid-interval would push the next refresh back each time.
            if (!_statsTimer.Enabled) _statsTimer.Start();
        }

        void StopStatsRefresh()
        {
            if (_statsTimer != null) _statsTimer.Stop();
        }

        /// <summary>
        /// Every StatsGuardMs: is the taskbar still there, and is a second up since the
        /// figures were last read. The figures are re-read only on the second, so the
        /// faster tick costs the check and nothing else.
        /// </summary>
        void OnStatsRefresh(object sender, EventArgs e)
        {
            // Pinned, with the pointer on the tile or off the strip. With the pointer on
            // another button the panel is on its way there, and a refresh would yank it back.
            bool wanted = _statsPinned && (_hoverIndex == StatsIndex || _hoverIndex < 0);

            if (_statsSuppressed)
            {
                if (!_statsPinned) { HideTooltip(); return; }
                if (StatsCovered()) return;   // still away; keep watching for the taskbar

                // The taskbar is back. If the pointer is already on another button, that
                // button's hover owns the panel now, and leaving the strip will bring the
                // stats back through RestOrHide like any other time.
                _statsSuppressed = false;
                if (wanted) ShowStats();
                else StopStatsRefresh();
                return;
            }

            if (!ShowingStats || !wanted)
            {
                StopStatsRefresh();
                return;
            }

            if (StatsCovered()) { SuppressStats(); return; }

            // Half a guard tick of slack: a WinForms timer lands a few ms either side of its
            // interval, and a strict 1000 would miss on the fourth tick at 998 and wait for
            // the fifth, turning a one-second refresh into one of 1.0 or 1.25 at random.
            int age = unchecked(Environment.TickCount - _statsShownAt);
            if (age < 0 || age >= StatsRefreshMs - StatsGuardMs / 2) ShowStats();
        }

        /// <summary>
        /// The taskbar the panel rests on is not on screen, so the panel should not be
        /// either. Three ways that happens:
        ///
        /// The strip is hidden - it is the taskbar's child, so it is hidden whenever the
        /// taskbar is. An auto-hide taskbar is different again: it is not hidden but slid
        /// off the monitor edge with a two-pixel sliver left showing, so the strip is
        /// "visible" and simply not on the monitor. Hence the containment test rather than
        /// IsWindowVisible alone.
        ///
        /// And a fullscreen app, which covers the taskbar without moving it. That one the
        /// shell has to tell us; see Native.SHQueryUserNotificationState.
        /// </summary>
        bool StatsCovered()
        {
            if (!Native.IsWindowVisible(Handle)) return true;

            Native.RECT strip;
            if (!Native.GetWindowRect(Handle, out strip)) return true;

            var info = new Native.MONITORINFO();
            info.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.MONITORINFO));
            IntPtr monitor = Native.MonitorFromWindow(Handle, Native.MONITOR_DEFAULTTONEAREST);
            if (monitor != IntPtr.Zero && Native.GetMonitorInfo(monitor, ref info))
            {
                Native.RECT m = info.rcMonitor;
                if (strip.Left < m.Left || strip.Top < m.Top
                    || strip.Right > m.Right || strip.Bottom > m.Bottom)
                    return true;
            }

            int state;
            try
            {
                if (Native.SHQueryUserNotificationState(out state) != 0) return false;
            }
            catch (Exception)
            {
                // Missing export or a shell that will not answer: assume nothing is in the
                // way. Wrong only over a fullscreen app, which is how it behaved before.
                return false;
            }

            return state == Native.QUNS_BUSY
                || state == Native.QUNS_RUNNING_D3D_FULL_SCREEN
                || state == Native.QUNS_PRESENTATION_MODE
                || state == Native.QUNS_NOT_PRESENT;
        }

        /// <summary>
        /// Puts the panel away without unpinning it, and keeps the guard tick running so it
        /// comes back when the taskbar does. Not HideTooltip, which stops that tick.
        /// </summary>
        void SuppressStats()
        {
            CancelReshow();
            _showingStats = false;
            _statsSuppressed = true;
            if (_tooltip != null && _tooltip.IsVisible) _tooltip.Hide();
            StartStatsRefresh();
        }

        /// <summary>
        /// Installs or removes the system-wide click watch that closes a pinned panel on a
        /// click elsewhere. Only ever present while pinned.
        /// </summary>
        void WatchClicks(bool watch)
        {
            if (watch && _clicks == null && !_disposed)
            {
                try
                {
                    _clicks = new ClickWatcher(Handle, WM_CLICK_ANYWHERE);
                }
                catch (Exception ex)
                {
                    // Survivable: the panel still closes from its own tile, as it always did.
                    Log.Write("strip: no click watch - " + ex.Message);
                }
            }
            else if (!watch && _clicks != null)
            {
                _clicks.Dispose();
                _clicks = null;
            }
        }

        /// <summary>
        /// A mouse press somewhere on the system, from the ClickWatcher. Anywhere but the
        /// tile unpins the panel - the tile's own click is OnClick's, which toggles it, and
        /// answering it here as well would close the panel on the press and reopen it on
        /// the release.
        ///
        /// Ignored while a context menu is up. The press is then usually on the menu, and
        /// one of its items is "Keep open"; unpinning on the press would close the popup the
        /// item was about to keep. A press outside the menu closes the
        /// menu, and MenuClosed brings the panel back - a second click closes it.
        ///
        /// A press on another of the strip's buttons counts as outside. That is the popup
        /// convention, and it is what a panel opened by a click is: open until you click
        /// elsewhere. A switch by keyboard still leaves it open - see SetDesktops. A kept
        /// panel never gets here, since it has no watch installed; the check is for a press
        /// already posted when "Keep open" was chosen.
        /// </summary>
        void OnClickAnywhere(Point screen)
        {
            if (!_statsPinned || _statsKept || _menuIndex >= 0) return;
            if (HitTestScreen(screen) == StatsIndex) return;

            SetStatsPinned(false);
        }

        void Raise(Action<Guid> handler, Guid id)
        {
            if (handler == null) return;
            try { handler(id); }
            catch (Exception ex) { Log.Write("strip: handler threw - " + ex.Message); }
        }

        void Raise(EventHandler handler)
        {
            if (handler == null) return;
            try { handler(this, EventArgs.Empty); }
            catch (Exception ex) { Log.Write("strip: handler threw - " + ex.Message); }
        }

        // --- window procedure --------------------------------------------------

        /// <summary>Modifier state as the mouse message itself reported it.</summary>
        static bool Shift(IntPtr wParam)
        {
            return ((int)(long)wParam & Native.MK_SHIFT) != 0;
        }

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case Native.WM_PAINT:
                    Paint();
                    m.Result = IntPtr.Zero;
                    return;

                case Native.WM_ERASEBKGND:
                    // Everything is painted in WM_PAINT from a back buffer; letting the
                    // system erase first would just flicker.
                    m.Result = new IntPtr(1);
                    return;

                case Native.WM_SETCURSOR:
                    // A plain NativeWindow gets no default cursor handling (that's a
                    // Control-only behaviour), so without this the strip just keeps
                    // showing whatever cursor was active before the pointer arrived -
                    // a taskbar resize cursor, a stray wait cursor, etc.
                    Cursor.Current = Cursors.Default;
                    m.Result = new IntPtr(1);
                    return;

                case Native.WM_MOUSEMOVE:
                    OnMouseMove(Native.LoWord(m.LParam), Native.HiWord(m.LParam));
                    return;

                case Native.WM_MOUSEHOVER:
                    ShowTooltip();
                    return;

                case Native.WM_MOUSELEAVE:
                    OnMouseLeave();
                    return;

                case Native.WM_LBUTTONUP:
                    OnClick(Native.LoWord(m.LParam), Native.HiWord(m.LParam),
                            MouseButtons.Left, Shift(m.WParam));
                    return;

                case Native.WM_RBUTTONUP:
                    OnClick(Native.LoWord(m.LParam), Native.HiWord(m.LParam),
                            MouseButtons.Right, Shift(m.WParam));
                    return;

                case Native.WM_MBUTTONUP:
                    OnClick(Native.LoWord(m.LParam), Native.HiWord(m.LParam),
                            MouseButtons.Middle, Shift(m.WParam));
                    return;

                case WM_CLICK_ANYWHERE:
                    OnClickAnywhere(new Point(Native.LoWord(m.LParam), Native.HiWord(m.LParam)));
                    return;
            }

            base.WndProc(ref m);
        }

        // --- lifetime ---------------------------------------------------------

        public void Destroy()
        {
            if (Handle != IntPtr.Zero) DestroyHandle();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Before the handle goes, or a frame already queued lands on a destroyed
            // window and invalidates a rectangle that is no longer anybody's.
            if (_frameTimer != null) { _frameTimer.Stop(); _frameTimer.Dispose(); _frameTimer = null; }

            // Same reason, and one worse: a re-show landing after the tooltip is gone would
            // call ShowTooltip on a disposed panel.
            if (_reshowTimer != null) { _reshowTimer.Stop(); _reshowTimer.Dispose(); _reshowTimer = null; }
            if (_statsTimer != null) { _statsTimer.Stop(); _statsTimer.Dispose(); _statsTimer = null; }

            // A system-wide hook must not outlive the window it posts to. The controller
            // hands the pin to the next strip, which installs its own.
            if (_clicks != null) { _clicks.Dispose(); _clicks = null; }

            // Before the strip goes, so an Explorer restart cannot strand the panel on
            // screen: the tooltip is top-level and would otherwise outlive its parent.
            if (_tooltip != null) { _tooltip.Dispose(); _tooltip = null; }

            Destroy();

            if (_buffer != null) { _buffer.Dispose(); _buffer = null; }
            if (_font != null) { _font.Dispose(); _font = null; }
        }
    }
}

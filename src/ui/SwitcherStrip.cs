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

        readonly int _buttonWidth;
        readonly int _plusWidth;
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

        int _hoverIndex = -1;          // == _desktops.Count means the '+' button
        int _menuIndex = -1;           // button whose context menu is open, by the same index
        bool _trackingMouse;
        bool _disposed;

        Color _background;
        Color _highlight;
        Bitmap _buffer;
        Font _font;

        TooltipWindow _tooltip;
        Timer _reshowTimer;            // runs only between a button change and the follow

        public SwitcherStrip(IntPtr parent, Rectangle bounds,
                             int buttonWidth, int plusWidth, int barHeight,
                             Color background, Color highlight,
                             uint hoverDelay, int tooltipWidth, int animationMs,
                             double dpiScale)
        {
            _buttonWidth = buttonWidth;
            _plusWidth = plusWidth;
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
        ///
        /// The strip contributes geometry only. Content needs the desktop model, the
        /// window inventory and the foreground tracker, none of which belong here.
        /// </summary>
        public Func<int, TooltipContent> TooltipProvider { get; set; }

        // --- layout -----------------------------------------------------------

        public static int MeasureWidth(int desktopCount, int buttonWidth, int plusWidth)
        {
            return desktopCount * buttonWidth + plusWidth;
        }

        public int Width { get { return MeasureWidth(_desktops.Count, _buttonWidth, _plusWidth); } }

        Rectangle ButtonBounds(int index, int height)
        {
            if (index >= _desktops.Count)
                return new Rectangle(_desktops.Count * _buttonWidth, 0, _plusWidth, height);
            return new Rectangle(index * _buttonWidth, 0, _buttonWidth, height);
        }

        int HitTest(int x, int y)
        {
            if (x < 0) return -1;

            int buttonsWidth = _desktops.Count * _buttonWidth;
            if (x < buttonsWidth) return x / _buttonWidth;
            if (x < buttonsWidth + _plusWidth) return _desktops.Count;
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

            if (_visuals.Length != _desktops.Count + 1)
                _visuals = new ButtonVisual[_desktops.Count + 1];

            if (_hoverIndex > _desktops.Count) _hoverIndex = -1;
            if (_menuIndex > _desktops.Count) _menuIndex = -1;

            // Anything the panel was saying about desktop N may now describe a different
            // desktop, since a removal renumbers everything after it.
            HideTooltip();

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
                // doing, so it stays visibly the one the menu belongs to.
                _visuals[i].HoverTarget = (i == _hoverIndex || i == _menuIndex) ? 1f : 0f;

                // The '+' button has no desktop and so is never in _occupied - i beyond
                // _desktops.Count reads false here the same way isCurrent does above.
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

            // Current desktop reads at full strength; hover carries an inactive one part of
            // the way there. HoverShare is the ratio the two tone steps used to stand in
            // before Palette owned the ramp - 30 of the 55 between resting and full - kept
            // exactly so a dark taskbar renders the same tones it always did.
            const float HoverShare = 30f / 55f;
            float emphasis = Math.Min(1f, visual.Highlight + visual.Hover * HoverShare);

            using (var brush = new SolidBrush(Palette.RampText(_background, emphasis)))
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
            SyncVisuals();
            Invalidate();

            // Restart the delay against the button now under the pointer.
            ArmTracking();

            if (index < 0) { HideTooltip(); return; }

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
            HideTooltip();

            if (_hoverIndex == -1) return;

            _hoverIndex = -1;

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
            if (index < 0) return;

            // The menu is the touchpad's only way to the actions the other buttons carry,
            // so it answers on every button, '+' included. With nothing listening - the
            // menu turned off in config - the click falls through to what right click did
            // before the menu existed.
            if (button == MouseButtons.Right && !shift && OpenMenu(index)) return;

            if (index >= _desktops.Count)
            {
                if (button == MouseButtons.Left) Raise(CreateRequested);
                return;
            }

            Guid id = _desktops[index].Id;
            switch (button)
            {
                case MouseButtons.Left:   Raise(SwitchRequested, id); break;
                case MouseButtons.Right:  Raise(MoveWindowRequested, id); break;   // shift held
                case MouseButtons.Middle: Raise(RemoveRequested, id); break;
            }
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

            if (_disposed || _hoverIndex < 0 || TooltipProvider == null) return;

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

            if (_tooltip == null)
                _tooltip = new TooltipWindow(_background, _highlight, _tooltipWidth, _dpiScale);

            // The button in screen coordinates: the panel anchors to it, and the accent
            // stub lines up under it.
            Rectangle anchor = AnchorFor(_hoverIndex);
            if (anchor.Width == 0) return;

            _tooltip.Show(content, anchor);

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
            if (_tooltip != null) _tooltip.Hide();
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

            // Before the strip goes, so an Explorer restart cannot strand the panel on
            // screen: the tooltip is top-level and would otherwise outlive its parent.
            if (_tooltip != null) { _tooltip.Dispose(); _tooltip = null; }

            Destroy();

            if (_buffer != null) { _buffer.Dispose(); _buffer = null; }
            if (_font != null) { _font.Dispose(); _font = null; }
        }
    }
}

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
    /// ANIMATION READINESS: per-button visual state lives in ButtonVisual as floats in
    /// 0..1, separate from the Desktop model. They are currently snapped to 0 or 1.
    /// A future animator tweens them on a timer and Render needs no change, because
    /// Render is pure - it reads visual state and draws, mutating nothing.
    /// </summary>
    sealed class SwitcherStrip : NativeWindow, IDisposable
    {
        /// <summary>Continuous visual state, deliberately decoupled from the model.</summary>
        struct ButtonVisual
        {
            public float Highlight;   // 0 = inactive, 1 = current desktop
            public float Hover;       // 0 = idle,     1 = pointer over
        }

        readonly int _buttonWidth;
        readonly int _plusWidth;
        readonly int _barHeight;
        readonly uint _hoverDelay;
        readonly int _tooltipWidth;
        readonly double _dpiScale;

        List<Desktop> _desktops = new List<Desktop>();
        ButtonVisual[] _visuals = new ButtonVisual[0];

        int _hoverIndex = -1;          // == _desktops.Count means the '+' button
        int _menuIndex = -1;           // button whose context menu is open, by the same index
        bool _trackingMouse;
        bool _disposed;

        Color _background;
        Color _highlight;
        Bitmap _buffer;
        Font _font;

        TooltipWindow _tooltip;

        public SwitcherStrip(IntPtr parent, Rectangle bounds,
                             int buttonWidth, int plusWidth, int barHeight,
                             Color background, Color highlight,
                             uint hoverDelay, int tooltipWidth, double dpiScale)
        {
            _buttonWidth = buttonWidth;
            _plusWidth = plusWidth;
            _barHeight = barHeight;
            _hoverDelay = hoverDelay;
            _tooltipWidth = tooltipWidth;
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
            _desktops = new List<Desktop>(desktops);

            if (_visuals.Length != _desktops.Count + 1)
                _visuals = new ButtonVisual[_desktops.Count + 1];

            if (_hoverIndex > _desktops.Count) _hoverIndex = -1;
            if (_menuIndex > _desktops.Count) _menuIndex = -1;

            // Anything the panel was saying about desktop N may now describe a different
            // desktop, since a removal renumbers everything after it.
            HideTooltip();

            SyncVisuals();
            Invalidate();
        }

        /// <summary>
        /// Snaps visual state to the model. The only place that decides what "current"
        /// and "hovered" look like; an animator would ease toward these targets instead
        /// of assigning them.
        /// </summary>
        void SyncVisuals()
        {
            for (int i = 0; i < _visuals.Length; i++)
            {
                bool isCurrent = i < _desktops.Count && _desktops[i].IsCurrent;
                _visuals[i].Highlight = isCurrent ? 1f : 0f;

                // A button whose menu is open reads as hovered whatever the pointer is
                // doing, so it stays visibly the one the menu belongs to.
                _visuals[i].Hover = (i == _hoverIndex || i == _menuIndex) ? 1f : 0f;
            }
        }

        public void SetBackground(Color color)
        {
            if (_background == color) return;
            _background = color;
            Invalidate();
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
        /// Pure: reads model and visual state, draws, mutates nothing. Keeping it pure
        /// is what lets animation be added later without restructuring.
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
        }

        void DrawButton(Graphics g, Rectangle bounds, string caption, ButtonVisual visual)
        {
            // Hover and current both lighten the cell; current adds the underline bar.
            float lift = Math.Max(visual.Hover * 0.55f, visual.Highlight * 0.85f);
            if (lift > 0.001f)
            {
                using (var fill = new SolidBrush(Lighten(_background, lift * 0.10f)))
                    g.FillRectangle(fill, bounds);
            }

            if (visual.Highlight > 0.001f)
            {
                int barHeight = Math.Max(2, (int)Math.Round(_barHeight * visual.Highlight));
                var bar = new Rectangle(bounds.X, bounds.Bottom - barHeight, bounds.Width, barHeight);
                using (var brush = new SolidBrush(_highlight))
                    g.FillRectangle(brush, bar);
            }

            // Current desktop reads brighter; hover lifts an inactive one part way.
            int tone = 200 + (int)(visual.Highlight * 55) + (int)(visual.Hover * 30);
            if (tone > 255) tone = 255;
            Color text = Color.FromArgb(tone, tone, tone);

            using (var brush = new SolidBrush(text))
            using (var format = new StringFormat())
            {
                format.Alignment = StringAlignment.Center;
                format.LineAlignment = StringAlignment.Center;

                // Nudge up so the glyph looks centred in the space above the bar.
                var textBounds = new Rectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height - _barHeight);
                g.DrawString(caption, _font, brush, textBounds, format);
            }
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

        static Color Lighten(Color color, float amount)
        {
            int r = color.R + (int)((255 - color.R) * amount);
            int g = color.G + (int)((255 - color.G) * amount);
            int b = color.B + (int)((255 - color.B) * amount);
            return Color.FromArgb(Clamp(r), Clamp(g), Clamp(b));
        }

        static int Clamp(int v)
        {
            return v < 0 ? 0 : (v > 255 ? 255 : v);
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

            if (index < 0)
                HideTooltip();
            else if (_tooltip != null && _tooltip.IsVisible)
                ShowTooltip();   // already open: follow the pointer without re-waiting
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

        void ShowTooltip()
        {
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
            // bar lines up under it.
            Rectangle anchor = AnchorFor(_hoverIndex);
            if (anchor.Width == 0) return;

            _tooltip.Show(content, anchor);
        }

        void HideTooltip()
        {
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

            // Before the strip goes, so an Explorer restart cannot strand the panel on
            // screen: the tooltip is top-level and would otherwise outlive its parent.
            if (_tooltip != null) { _tooltip.Dispose(); _tooltip = null; }

            Destroy();

            if (_buffer != null) { _buffer.Dispose(); _buffer = null; }
            if (_font != null) { _font.Dispose(); _font = null; }
        }
    }
}

using System;
using System.Drawing;

namespace DesktopSwitcher
{
    /// <summary>
    /// Finds the taskbar, works out where the strip belongs, and keeps it parented
    /// there.
    ///
    /// The strip becomes a child of Shell_TrayWnd rather than a floating topmost
    /// window. That inherits, for free, everything a floating window would have to
    /// reimplement: it moves with the taskbar, hides when the taskbar auto-hides or a
    /// fullscreen app takes over, and appears on every virtual desktop.
    ///
    /// The cost is that an Explorer restart destroys our child window along with its
    /// parent. That is why the strip is disposable and the controller rebuilds it.
    /// </summary>
    sealed class TaskbarHost
    {
        IntPtr _tray;
        IntPtr _rebar;
        IntPtr _notifyArea;

        double _dpiScale = 1.0;

        public IntPtr TrayWindow { get { return _tray; } }
        public bool Located { get { return _tray != IntPtr.Zero; } }

        /// <summary>DPI scale of the taskbar's monitor. 1.0 at 96 DPI.</summary>
        public double DpiScale { get { return _dpiScale; } }

        /// <summary>Scales a size authored for 96 DPI to the taskbar's actual DPI.</summary>
        public int Scale(int value)
        {
            return (int)Math.Round(value * _dpiScale);
        }

        /// <summary>
        /// Re-finds the taskbar windows. Handles must be refreshed after an Explorer
        /// restart, because the new Explorer creates entirely new windows.
        /// </summary>
        public bool Locate()
        {
            _tray = Native.FindWindow("Shell_TrayWnd", null);
            if (_tray == IntPtr.Zero)
            {
                _rebar = IntPtr.Zero;
                _notifyArea = IntPtr.Zero;
                return false;
            }

            _rebar = Native.FindWindowEx(_tray, IntPtr.Zero, "ReBarWindow32", null);
            _notifyArea = Native.FindWindowEx(_tray, IntPtr.Zero, "TrayNotifyWnd", null);
            _dpiScale = Native.GetDpiScale(_tray);
            return true;
        }

        public bool IsHealthy()
        {
            return _tray != IntPtr.Zero
                && Native.IsWindow(_tray)
                && _tray == Native.FindWindow("Shell_TrayWnd", null);
        }

        /// <summary>True once the notification area exists and the strip can be anchored.</summary>
        public bool HasAnchor
        {
            get
            {
                EnsureChildren();
                return _notifyArea != IntPtr.Zero;
            }
        }

        /// <summary>
        /// Re-resolves the taskbar's child windows if they have gone away.
        ///
        /// A restarted Explorer creates Shell_TrayWnd before TrayNotifyWnd, so a Locate
        /// during that window leaves the anchor null. Without this, every later
        /// computation falls back to the taskbar's right edge and the strip is stranded
        /// to the right of the clock, with the once-per-second Reassert faithfully
        /// keeping it there.
        /// </summary>
        void EnsureChildren()
        {
            if (_tray == IntPtr.Zero) return;

            if (_notifyArea == IntPtr.Zero || !Native.IsWindow(_notifyArea))
                _notifyArea = Native.FindWindowEx(_tray, IntPtr.Zero, "TrayNotifyWnd", null);

            if (_rebar == IntPtr.Zero || !Native.IsWindow(_rebar))
                _rebar = Native.FindWindowEx(_tray, IntPtr.Zero, "ReBarWindow32", null);
        }

        /// <summary>True when the taskbar is docked left or right rather than top or bottom.</summary>
        public bool IsVertical
        {
            get
            {
                Native.RECT r;
                if (_tray == IntPtr.Zero || !Native.GetWindowRect(_tray, out r)) return false;
                return r.Height > r.Width;
            }
        }

        /// <summary>Caption of MediaController's tile when it is docked beside the clock.</summary>
        const string TileCaption = "MediaControllerTileRight";

        /// <summary>
        /// Screen X that the strip's right edge (plus margin) sits against: the left edge of the
        /// notification area, otherwise the taskbar's right edge - or, when MediaController's
        /// tile has taken the slot beside the clock, the left edge of that tile.
        ///
        /// The tile announces that through its window caption ("...Right"), not through where it
        /// happens to be: a tile on the strip's other side has the plain caption and is ignored,
        /// so the two apps cannot each wait for the other. Looked up on every call, not cached,
        /// because the tile comes and goes with its app and Explorer restarts. The watchdog's
        /// once-per-second Reassert is what follows it.
        /// </summary>
        int AnchorScreenX(Native.RECT tray)
        {
            Native.RECT notify;
            int x = tray.Right;
            int notifyLeft = -1;
            if (_notifyArea != IntPtr.Zero && Native.GetWindowRect(_notifyArea, out notify))
            {
                x = notify.Left;
                notifyLeft = notify.Left;
            }

            IntPtr tile = _tray == IntPtr.Zero ? IntPtr.Zero : Native.FindWindowEx(_tray, IntPtr.Zero, null, TileCaption);
            Native.RECT t;
            bool tileUsed = false;
            _anchorIsTile = false;
            if (tile != IntPtr.Zero && Native.IsWindowVisible(tile)
                && Native.GetWindowRect(tile, out t) && t.Left < x && t.Left > tray.Left)
            {
                x = t.Left;
                tileUsed = true;
                _anchorIsTile = true;
                Native.GetWindowThreadProcessId(tile, out _tilePid);
            }

            if (Log.Enabled) LogAnchorChange(x, notifyLeft, tileUsed);
            return x;
        }

        int _lastAnchorX = int.MinValue;
        int _lastNotifyLeft = int.MinValue;
        bool _lastTileUsed;

        /// <summary>
        /// Records every move of the anchor, and which of its two inputs moved it.
        ///
        /// The strip visibly jumps left and back now and then, and there are two quite
        /// different causes: the notification area widening because a tray icon was promoted
        /// out of the overflow flyout for a few seconds, and MediaController's tile arriving
        /// or leaving. Both look the same on screen and nothing else records either. The
        /// notification area's left edge is logged on its own, so a change in it with no tile
        /// involved is a tray icon, and a change in the tile flag is MediaController.
        ///
        /// Only on change, so the call that happens on every watchdog tick costs one
        /// comparison and writes nothing when the taskbar is still.
        /// </summary>
        void LogAnchorChange(int x, int notifyLeft, bool tileUsed)
        {
            if (x == _lastAnchorX && notifyLeft == _lastNotifyLeft && tileUsed == _lastTileUsed) return;

            string cause;
            if (_lastAnchorX == int.MinValue) cause = "first reading";
            else if (tileUsed != _lastTileUsed) cause = tileUsed ? "MediaController tile appeared" : "MediaController tile gone";
            else if (notifyLeft != _lastNotifyLeft) cause = "notification area " + (notifyLeft < _lastNotifyLeft ? "widened" : "narrowed")
                                                           + " (tray icon shown or hidden)";
            else cause = "tile moved";

            Log.Write("taskbar: anchor x " + (_lastAnchorX == int.MinValue ? "-" : _lastAnchorX.ToString())
                      + " -> " + x + "; notify.Left " + notifyLeft + ", tile " + (tileUsed ? "used" : "not used")
                      + "; " + cause);

            _lastAnchorX = x;
            _lastNotifyLeft = notifyLeft;
            _lastTileUsed = tileUsed;
        }

        // --- glide ------------------------------------------------------------

        bool _anchorIsTile;     // set by AnchorScreenX: the strip is against the tile, not the tray
        uint _tilePid;

        /// <summary>Process id of the tile's owner while it is the anchor, else 0.</summary>
        public uint TileProcessId { get { return _anchorIsTile ? _tilePid : 0; } }

        int _glideLastRaw = int.MinValue;   // the anchor as last seen, before any glide
        float _glideOffset;                 // pixels the shown anchor still lags the real one

        /// <summary>True while the strip is still catching up with a moved anchor.</summary>
        public bool Gliding { get { return _glideOffset != 0f; } }

        /// <summary>
        /// Forgets any glide in progress, so the next placement lands exactly. Called when
        /// the strip is rebuilt: a new strip, a new taskbar or a new display have no earlier
        /// position worth easing from.
        /// </summary>
        public void ResetGlide()
        {
            _glideLastRaw = int.MinValue;
            _glideOffset = 0f;
        }

        /// <summary>
        /// The anchor the strip is actually drawn against: the real one, plus whatever of
        /// the last move it has not yet covered.
        ///
        /// A tray icon appearing or going moves the notification area's left edge, and the
        /// strip used to jump the same distance on the next watchdog tick. The move is not
        /// eased by interpolating the strip's position but by remembering how far behind it
        /// is: when the real anchor moves from a to b, the offset takes up (a - b) so the
        /// strip stays where it was, and StepGlide then eases that offset to zero. It is the
        /// anchor that glides and not the strip's left edge because the strip's width changes
        /// on its own (a desktop added or removed) and must land at once, with its right
        /// edge still against the tray; the offset is added to the anchor, so it does.
        ///
        /// A second move mid-glide simply adds to the offset, so retargeting is free - the
        /// same property Motion.Rate is chosen for.
        /// </summary>
        internal int GlideAnchor(int raw)
        {
            if (_glideLastRaw != int.MinValue && raw != _glideLastRaw)
                _glideOffset += _glideLastRaw - raw;
            _glideLastRaw = raw;
            return raw + (int)Math.Round(_glideOffset);
        }

        /// <summary>
        /// True when the anchor is not where the strip was last placed against it. A few
        /// window-rect reads and no writes, so it is cheap enough to ask many times a second;
        /// that is the point of it. The watchdog's once-per-second Reassert was the only thing
        /// noticing a tray change, so MediaController's tile - which follows the notification
        /// area on its own timer - moved left over the strip, and the strip only then went
        /// after it. Asking this every few tens of milliseconds closes that gap.
        /// </summary>
        public bool AnchorMoved()
        {
            Native.RECT tray;
            if (_tray == IntPtr.Zero || _glideLastRaw == int.MinValue || IsVertical) return false;
            EnsureChildren();
            if (!Native.GetWindowRect(_tray, out tray)) return false;
            return AnchorScreenX(tray) != _glideLastRaw;
        }

        /// <summary>
        /// One frame of the glide. True when the offset moved and the strip needs placing
        /// again; false once it has landed.
        /// </summary>
        public bool StepGlide(int elapsedMs)
        {
            return Motion.Ease(ref _glideOffset, 0f, Motion.Rate(elapsedMs, GlideMs), Motion.PixelEpsilon);
        }

        /// <summary>
        /// How long the strip takes to settle after the tray moves it. Its own number, not
        /// animationMs, and the same one MediaController's tile uses, so the two move alike
        /// alone or together.
        ///
        /// Not animationMs (100) because the ease is exponential and covers the same fraction of
        /// what is left every frame: for a 30 px move at 100 ms that is 17 px in the first 15 ms
        /// frame, which reads as a jump with a short tail. animationMs also drives the hover
        /// panel and the underline, which want to be quick; this wants to be seen.
        /// </summary>
        public const int GlideMs = 300;

        /// <summary>
        /// Strip bounds in Shell_TrayWnd client coordinates.
        ///
        /// The strip is right-anchored to the notification area, so the gap to the
        /// clock stays constant as buttons are added or removed, and it grows leftward
        /// into the empty tail of the task-button area.
        /// </summary>
        public bool TryComputeBounds(int width, int margin, out Rectangle bounds)
        {
            bounds = Rectangle.Empty;
            EnsureChildren();

            Native.RECT tray;
            if (_tray == IntPtr.Zero || !Native.GetWindowRect(_tray, out tray)) return false;

            int anchorScreenX = AnchorScreenX(tray);
            Native.RECT notify;

            if (IsVertical)
            {
                // Not the configured layout, but degrade sensibly instead of drawing
                // the strip off-screen: sit just above the notification area.
                int vBottom;
                if (_notifyArea != IntPtr.Zero && Native.GetWindowRect(_notifyArea, out notify))
                    vBottom = notify.Top - margin;
                else
                    vBottom = tray.Bottom - margin;

                var vTopLeft = new Native.POINT();
                vTopLeft.X = tray.Left;
                vTopLeft.Y = vBottom - tray.Width;
                if (!Native.ScreenToClient(_tray, ref vTopLeft)) return false;

                bounds = new Rectangle(vTopLeft.X, vTopLeft.Y, tray.Width, tray.Width);
                return true;
            }

            var topLeft = new Native.POINT();
            topLeft.X = GlideAnchor(anchorScreenX) - margin - width;
            topLeft.Y = tray.Top;
            if (!Native.ScreenToClient(_tray, ref topLeft)) return false;

            bounds = new Rectangle(topLeft.X, topLeft.Y, width, tray.Height);
            return true;
        }

        /// <summary>
        /// Makes the window a child of the taskbar. The window must already be a popup
        /// with WS_EX_NOACTIVATE; SetParent alone does not change the style bits, so
        /// WS_CHILD is applied explicitly.
        /// </summary>
        public bool Attach(IntPtr child)
        {
            if (_tray == IntPtr.Zero || child == IntPtr.Zero) return false;

            int style = Native.GetWindowLong(child, Native.GWL_STYLE);
            style = (style & ~Native.WS_POPUP) | Native.WS_CHILD;
            Native.SetWindowLong(child, Native.GWL_STYLE, style);

            IntPtr previous = Native.SetParent(child, _tray);
            if (previous == IntPtr.Zero && Native.GetParent(child) != _tray)
            {
                Log.Write("taskbar: SetParent failed");
                return false;
            }

            Log.Write("taskbar: strip attached to Shell_TrayWnd");
            return true;
        }

        public bool IsAttached(IntPtr child)
        {
            return child != IntPtr.Zero
                && Native.IsWindow(child)
                && Native.GetParent(child) == _tray;
        }

        /// <summary>
        /// Re-applies position and z-order. The strip overlaps the tail of the
        /// task-button area rather than reserving space, so it must stay above its
        /// siblings or task buttons would paint over it.
        /// </summary>
        public bool Reassert(IntPtr child, int width, int margin)
        {
            Rectangle bounds;
            if (!TryComputeBounds(width, margin, out bounds)) return false;

            return Native.SetWindowPos(child, Native.HWND_TOP,
                bounds.X, bounds.Y, bounds.Width, bounds.Height,
                Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        }

        /// <summary>
        /// Samples the taskbar's rendered colour just left of where the strip sits.
        ///
        /// A child window gets no DWM blur from its parent, so a hardcoded colour would
        /// read as a visible patch against a translucent taskbar. Sampling the live
        /// pixel keeps the strip blending in whatever the theme and wallpaper are.
        ///
        /// stripWidth must be the width of the strip that is actually on screen, and the
        /// caller must have destroyed it first. Both were got wrong for a long time: the
        /// probe was computed for two desktops however many there were, so with four at
        /// 125% the point landed inside the strip, and the strip was still up when it was
        /// read. What came back was the colour the strip was already painted, which it then
        /// dutifully repainted itself - a sample that could confirm anything except that
        /// the taskbar had changed.
        ///
        /// Fails rather than guessing when the taskbar is not the window at that pixel. The
        /// screen DC returns what is on screen, not what the taskbar is, and on waking from
        /// sleep the lock screen is on screen - fullscreen, over the taskbar - when the
        /// display change arrives. GetPixel read the lock screen's wallpaper, succeeded, and
        /// the strip repainted itself that colour and kept it: nothing resamples after an
        /// unlock, so it survived until an Explorer restart, a theme change, or the tray's
        /// reload item. Delaying the read cannot help, and the delay already there was aimed
        /// at this and missed. The pixel had settled. It belonged to a different window.
        /// </summary>
        public bool TrySampleBackground(int stripWidth, int margin, out Color color)
        {
            color = Color.Empty;
            EnsureChildren();

            Native.RECT tray;
            if (_tray == IntPtr.Zero || !Native.GetWindowRect(_tray, out tray)) return false;

            int anchorScreenX = AnchorScreenX(tray);

            // Just left of where the strip will sit, vertically centred - empty taskbar
            // unless a great many windows are open. The step off the strip's edge is
            // scaled like every other size here: unscaled, it was 8 physical pixels at
            // any DPI, which on a 150% display is barely five of the ones the layout is
            // authored in and leaves the probe hugging the strip's edge.
            int x = anchorScreenX - margin - stripWidth - Scale(8);
            int y = tray.Top + tray.Height / 2;
            if (x < tray.Left) x = tray.Left + Scale(4);

            // Whose pixel is this? Anything covering the taskbar - the lock screen after a
            // wake, a fullscreen app - would otherwise be sampled as though it were the
            // taskbar. Returning false here hands the caller its existing "keep the last
            // good colour" path, which is the right answer: a translucent taskbar over an
            // unchanged wallpaper is still the colour it was.
            if (Native.RootWindowFromPoint(x, y) != _tray)
            {
                Log.Write("taskbar: probe point is not the taskbar - sample skipped");
                return false;
            }

            // The screen DC, not the taskbar's own. Under DWM a foreign window's DC does
            // not contain its composited pixels, so GetPixel there returns CLR_INVALID;
            // the screen DC reflects what is actually displayed.
            IntPtr hdc = Native.GetDC(IntPtr.Zero);
            if (hdc == IntPtr.Zero) return false;

            try
            {
                uint pixel = Native.GetPixel(hdc, x, y);
                if (pixel == 0xFFFFFFFF) return false; // CLR_INVALID

                color = Color.FromArgb((int)(pixel & 0xFF),
                                       (int)((pixel >> 8) & 0xFF),
                                       (int)((pixel >> 16) & 0xFF));
                return true;
            }
            finally
            {
                Native.ReleaseDC(IntPtr.Zero, hdc);
            }
        }

        /// <summary>Diagnostic snapshot of the taskbar layout.</summary>
        public string Describe()
        {
            if (_tray == IntPtr.Zero) return "taskbar not found";
            EnsureChildren();

            Native.RECT tray, rebar, notify;
            Native.GetWindowRect(_tray, out tray);

            string s = string.Format("Shell_TrayWnd  ({0},{1})-({2},{3})  {4}x{5}  {6}",
                tray.Left, tray.Top, tray.Right, tray.Bottom,
                tray.Width, tray.Height, IsVertical ? "VERTICAL" : "horizontal");

            if (_rebar != IntPtr.Zero && Native.GetWindowRect(_rebar, out rebar))
                s += string.Format("\n  ReBarWindow32  x {0} -> {1}", rebar.Left, rebar.Right);

            if (_notifyArea != IntPtr.Zero && Native.GetWindowRect(_notifyArea, out notify))
                s += string.Format("\n  TrayNotifyWnd  x {0} -> {1}", notify.Left, notify.Right);

            return s;
        }
    }
}

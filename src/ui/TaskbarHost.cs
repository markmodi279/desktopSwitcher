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

            // Anchor to the notification area when present, otherwise the taskbar edge.
            int anchorScreenX;
            Native.RECT notify;
            if (_notifyArea != IntPtr.Zero && Native.GetWindowRect(_notifyArea, out notify))
                anchorScreenX = notify.Left;
            else
                anchorScreenX = tray.Right;

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
            topLeft.X = anchorScreenX - margin - width;
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

            int anchorScreenX;
            Native.RECT notify;
            if (_notifyArea != IntPtr.Zero && Native.GetWindowRect(_notifyArea, out notify))
                anchorScreenX = notify.Left;
            else
                anchorScreenX = tray.Right;

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

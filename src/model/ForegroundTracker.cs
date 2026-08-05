using System;

namespace DesktopSwitcher
{
    /// <summary>
    /// Remembers the last real application window to hold focus.
    ///
    /// "Send the active window to desktop N" cannot simply read GetForegroundWindow at
    /// click time: clicking anywhere on the taskbar hands focus to Explorer, so by the
    /// time the handler runs the foreground window is Shell_TrayWnd. Moving that fails
    /// with E_ACCESSDENIED, and looking up its application view fails with
    /// TYPE_E_ELEMENTNOTFOUND - both correct answers to the wrong question.
    ///
    /// Sampling on a timer keeps hold of the window the user actually meant.
    /// </summary>
    sealed class ForegroundTracker
    {
        IntPtr _last;
        IntPtr _ignore;

        /// <summary>Window to never track - our own strip.</summary>
        public void Ignore(IntPtr hwnd)
        {
            _ignore = hwnd;
        }

        /// <summary>Cheap enough to call on every tick: one GetForegroundWindow.</summary>
        public void Sample()
        {
            IntPtr fg = Native.GetForegroundWindow();
            if (!IsCandidate(fg)) return;
            _last = fg;
        }

        /// <summary>
        /// The window a move should target: whatever holds focus now if it qualifies,
        /// otherwise the last one that did.
        /// </summary>
        public IntPtr Resolve()
        {
            IntPtr fg = Native.GetForegroundWindow();
            if (IsCandidate(fg)) return fg;

            if (_last != IntPtr.Zero && Native.IsWindow(_last)) return _last;
            return IntPtr.Zero;
        }

        bool IsCandidate(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            if (hwnd == _ignore) return false;
            if (!Native.IsWindow(hwnd)) return false;

            // Excludes the taskbar and tool windows: Shell_TrayWnd has no caption text,
            // so it fails the alt-tab test.
            return Native.IsAltTabWindow(hwnd);
        }
    }
}

using System;
using System.Runtime.InteropServices;
using System.Text;

namespace DesktopSwitcher
{
    /// <summary>
    /// Win32 P/Invoke surface. Grows as later milestones need it.
    /// </summary>
    static class Native
    {
        // --- window enumeration / inspection ---------------------------------

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr GetTopWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);

        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int max);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder text, int max);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int GetWindowLong(IntPtr hWnd, int index);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr param);

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr param);

        // --- DPI ---------------------------------------------------------------

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool SetProcessDpiAwarenessContext(IntPtr context);

        [DllImport("shcore.dll", SetLastError = true)]
        static extern int SetProcessDpiAwareness(int awareness);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool SetProcessDPIAware();

        static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);
        const int PROCESS_PER_MONITOR_DPI_AWARE = 2;

        /// <summary>
        /// Opts the process out of DPI virtualisation. MUST be called before any window
        /// is created.
        ///
        /// Explorer is DPI-aware. A DPI-unaware process that parents a window into the
        /// taskbar has its coordinates silently scaled by 1/dpiScale, so on a 125%
        /// display a strip positioned at x=1110 actually lands at x=888. Everything
        /// looks correct in our own logs and is wrong on screen.
        ///
        /// Falls back through three generations of the API; the oldest ships on Vista.
        /// </summary>
        public static void EnableDpiAwareness()
        {
            try
            {
                if (SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2))
                    return;
            }
            catch (EntryPointNotFoundException) { }
            catch (DllNotFoundException) { }

            try
            {
                if (SetProcessDpiAwareness(PROCESS_PER_MONITOR_DPI_AWARE) == 0) return;
            }
            catch (EntryPointNotFoundException) { }
            catch (DllNotFoundException) { }

            try
            {
                SetProcessDPIAware();
            }
            catch (EntryPointNotFoundException) { }
            catch (DllNotFoundException) { }
        }

        [DllImport("user32.dll")]
        static extern uint GetDpiForWindow(IntPtr hWnd);

        [DllImport("gdi32.dll")]
        static extern int GetDeviceCaps(IntPtr hdc, int index);

        const int LOGPIXELSX = 88;

        /// <summary>
        /// DPI scale for a window's monitor, 1.0 at 96 DPI. Config sizes are authored
        /// for 96 DPI and multiplied by this, so the strip matches the taskbar's own
        /// button size instead of looking cramped on a scaled display.
        /// </summary>
        public static double GetDpiScale(IntPtr hWnd)
        {
            try
            {
                uint dpi = GetDpiForWindow(hWnd);
                if (dpi > 0) return dpi / 96.0;
            }
            catch (EntryPointNotFoundException) { }

            IntPtr hdc = GetDC(IntPtr.Zero);
            if (hdc == IntPtr.Zero) return 1.0;
            try
            {
                int dpi = GetDeviceCaps(hdc, LOGPIXELSX);
                return dpi > 0 ? dpi / 96.0 : 1.0;
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, hdc);
            }
        }

        // --- geometry / parenting ---------------------------------------------

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left, Top, Right, Bottom;
            public int Width { get { return Right - Left; } }
            public int Height { get { return Bottom - Top; } }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X, Y;
        }

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindow(string className, string windowName);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after,
                                                 string className, string windowName);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool ScreenToClient(IntPtr hWnd, ref POINT point);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetParent(IntPtr child, IntPtr newParent);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr GetParent(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter,
                                               int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int SetWindowLong(IntPtr hWnd, int index, int value);

        [DllImport("user32.dll")]
        public static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);

        [DllImport("gdi32.dll")]
        public static extern uint GetPixel(IntPtr hdc, int x, int y);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint RegisterWindowMessage(string message);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool DestroyIcon(IntPtr icon);

        // --- painting ---------------------------------------------------------

        [StructLayout(LayoutKind.Sequential)]
        public struct PAINTSTRUCT
        {
            public IntPtr hdc;
            public int fErase;
            public RECT rcPaint;
            public int fRestore;
            public int fIncUpdate;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
            public byte[] rgbReserved;
        }

        [DllImport("user32.dll")]
        public static extern IntPtr BeginPaint(IntPtr hWnd, out PAINTSTRUCT ps);

        [DllImport("user32.dll")]
        public static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT ps);

        [DllImport("user32.dll")]
        public static extern bool InvalidateRect(IntPtr hWnd, IntPtr rect, bool erase);

        [DllImport("user32.dll")]
        public static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT tme);

        [StructLayout(LayoutKind.Sequential)]
        public struct TRACKMOUSEEVENT
        {
            public int cbSize;
            public uint dwFlags;
            public IntPtr hwndTrack;
            public uint dwHoverTime;
        }

        public const uint TME_LEAVE = 0x00000002;

        public const int WM_PAINT = 0x000F;
        public const int WM_ERASEBKGND = 0x0014;
        public const int WM_SETCURSOR = 0x0020;
        public const int WM_MOUSEMOVE = 0x0200;
        public const int WM_LBUTTONUP = 0x0202;
        public const int WM_RBUTTONUP = 0x0205;
        public const int WM_MBUTTONUP = 0x0208;
        public const int WM_MOUSELEAVE = 0x02A3;
        public const int WM_NCDESTROY = 0x0082;

        public static int LoWord(IntPtr value)
        {
            return unchecked((short)(long)value);
        }

        public static int HiWord(IntPtr value)
        {
            return unchecked((short)((long)value >> 16));
        }

        public static readonly IntPtr HWND_TOP = IntPtr.Zero;

        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOZORDER = 0x0004;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_SHOWWINDOW = 0x0040;

        public const int WS_CHILD = unchecked((int)0x40000000);
        public const int WS_POPUP = unchecked((int)0x80000000);
        public const int WS_EX_NOACTIVATE = 0x08000000;

        // --- focus ------------------------------------------------------------

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool AttachThreadInput(uint attachTo, uint attachFrom, bool attach);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        // --- constants --------------------------------------------------------

        public const uint GW_HWNDNEXT = 2;
        public const uint GW_OWNER = 4;

        public const int GWL_STYLE = -16;
        public const int GWL_EXSTYLE = -20;

        public const int WS_VISIBLE = unchecked((int)0x10000000);
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_APPWINDOW = 0x00040000;
        public const int WS_EX_NOREDIRECTIONBITMAP = 0x00200000;

        // --- helpers ----------------------------------------------------------

        public static string GetText(IntPtr hWnd)
        {
            int len = GetWindowTextLength(hWnd);
            if (len <= 0) return string.Empty;
            var sb = new StringBuilder(len + 1);
            GetWindowText(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }

        public static string GetClass(IntPtr hWnd)
        {
            var sb = new StringBuilder(256);
            GetClassName(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }

        /// <summary>
        /// True for windows a user would consider "an open app window" - the same set
        /// that earns a taskbar button.
        /// </summary>
        public static bool IsAltTabWindow(IntPtr hWnd)
        {
            if (!IsWindowVisible(hWnd)) return false;
            if (GetWindowTextLength(hWnd) == 0) return false;

            int ex = GetWindowLong(hWnd, GWL_EXSTYLE);
            if ((ex & WS_EX_TOOLWINDOW) != 0) return false;

            // Owned windows (dialogs, popups) don't get their own button unless they
            // explicitly ask for one.
            if (GetWindow(hWnd, GW_OWNER) != IntPtr.Zero && (ex & WS_EX_APPWINDOW) == 0)
                return false;

            return true;
        }

        /// <summary>
        /// SetForegroundWindow is refused unless the caller already owns the foreground.
        /// Briefly attaching to the current foreground thread's input queue lifts that.
        /// </summary>
        public static bool ForceForeground(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero || !IsWindow(hWnd)) return false;
            if (SetForegroundWindow(hWnd)) return true;

            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;

            uint pid;
            uint fgThread = GetWindowThreadProcessId(fg, out pid);
            uint ourThread = GetCurrentThreadId();
            if (fgThread == ourThread) return false;

            if (!AttachThreadInput(ourThread, fgThread, true)) return false;
            try
            {
                return SetForegroundWindow(hWnd);
            }
            finally
            {
                AttachThreadInput(ourThread, fgThread, false);
            }
        }
    }
}

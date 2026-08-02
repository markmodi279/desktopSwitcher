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

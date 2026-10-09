using System;
using System.Collections.Generic;
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

        [DllImport("user32.dll")]
        public static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr param);

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr param);

        // --- owning process ----------------------------------------------------

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref int size);

        /// <summary>
        /// The weakest right that still answers "which exe is this". Unlike
        /// PROCESS_QUERY_INFORMATION - and unlike Process.MainModule, which needs a module
        /// snapshot - it is granted against elevated processes from a normal-rights caller,
        /// so an admin console does not come back blank.
        /// </summary>
        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

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

        [DllImport("user32.dll")]
        public static extern IntPtr WindowFromPoint(POINT point);

        [DllImport("user32.dll")]
        public static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);

        public const uint GA_ROOT = 2;

        /// <summary>
        /// The top-level window that owns whatever is on screen at a point.
        ///
        /// GA_ROOT, not a bare GetParent: WindowFromPoint returns the deepest child at the
        /// point, so over an empty stretch of taskbar it answers MSTaskListWClass, and over
        /// the tray it answers something else again. Comparing that against Shell_TrayWnd
        /// directly would reject every point on the taskbar except the few pixels the
        /// taskbar itself still owns. Walking to the root is the question actually being
        /// asked - which window is this pixel part of.
        /// </summary>
        public static IntPtr RootWindowFromPoint(int x, int y)
        {
            var pt = new POINT();
            pt.X = x;
            pt.Y = y;

            IntPtr hit = WindowFromPoint(pt);
            if (hit == IntPtr.Zero) return IntPtr.Zero;

            IntPtr root = GetAncestor(hit, GA_ROOT);
            return root != IntPtr.Zero ? root : hit;
        }

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

        /// <summary>
        /// Paints the pending update region now, on this thread, instead of leaving a
        /// WM_PAINT to be found whenever the queue next runs dry.
        ///
        /// For a window being moved a frame at a time this is what keeps the paint inside
        /// the tick that caused it: a slow frame then delays the next one rather than
        /// queueing behind it, which is the difference between dropping frames and
        /// accumulating a backlog of them.
        /// </summary>
        [DllImport("user32.dll")]
        public static extern bool UpdateWindow(IntPtr hWnd);

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

        public const uint TME_HOVER = 0x00000001;
        public const uint TME_LEAVE = 0x00000002;

        public const int WM_PAINT = 0x000F;
        public const int WM_ERASEBKGND = 0x0014;
        public const int WM_SETCURSOR = 0x0020;
        public const int WM_MOUSEMOVE = 0x0200;
        public const int WM_LBUTTONUP = 0x0202;
        public const int WM_RBUTTONUP = 0x0205;
        public const int WM_MBUTTONUP = 0x0208;
        public const int WM_MOUSEHOVER = 0x02A1;
        public const int WM_MOUSELEAVE = 0x02A3;
        public const int WM_NCDESTROY = 0x0082;

        /// <summary>
        /// Shift, as a mouse message reports it in wParam. Read from the message rather
        /// than asked of the keyboard, so it is the state at the click and not whenever
        /// the handler happens to run.
        /// </summary>
        public const int MK_SHIFT = 0x0004;

        public static int LoWord(IntPtr value)
        {
            return unchecked((short)(long)value);
        }

        public static int HiWord(IntPtr value)
        {
            return unchecked((short)((long)value >> 16));
        }

        public static readonly IntPtr HWND_TOP = IntPtr.Zero;
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOZORDER = 0x0004;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_SHOWWINDOW = 0x0040;
        public const uint SWP_HIDEWINDOW = 0x0080;

        /// <summary>
        /// Discards the client area instead of blitting it to the new position, and
        /// invalidates the whole of it.
        ///
        /// For a window whose contents depend on where it is - the hover panel's accent
        /// stub is placed against a button in screen coordinates, so every pixel of the
        /// panel is position-dependent - the copy is pure waste, and worse than waste: it
        /// briefly shows the old stub at a wrong offset before the real paint lands.
        /// </summary>
        public const uint SWP_NOCOPYBITS = 0x0100;

        public const int WS_CHILD = unchecked((int)0x40000000);
        public const int WS_POPUP = unchecked((int)0x80000000);
        public const int WS_EX_NOACTIVATE = 0x08000000;
        public const int WS_EX_TOPMOST = 0x00000008;
        public const int WS_EX_TRANSPARENT = 0x00000020;

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

        // --- session notifications ---------------------------------------------

        // Lock and unlock are the only way to learn that the screen is ours again after a
        // wake. WM_POWERBROADCAST arrives with the lock screen still up, so on its own it
        // says the machine is awake and nothing about whether the taskbar is visible.
        //
        // Unlike WM_POWERBROADCAST, which every top-level window gets for free, these have
        // to be asked for. Failing is survivable - the resample simply waits for the next
        // trigger - so the caller logs and carries on rather than treating it as fatal.

        [DllImport("wtsapi32.dll", SetLastError = true)]
        public static extern bool WTSRegisterSessionNotification(IntPtr hWnd, uint flags);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        public static extern bool WTSUnRegisterSessionNotification(IntPtr hWnd);

        public const uint NOTIFY_FOR_THIS_SESSION = 0;

        // --- system counters ---------------------------------------------------

        // The stats tile's CPU and memory figures. Both are plain kernel32 reads costing
        // microseconds, which is why they and not PerformanceCounter: the first counter
        // constructed in a process loads the perf libraries and can stall the UI thread
        // for a second or more, and counter names are localised, so "Processor" is not
        // what the category is called on a German install.

        /// <summary>
        /// Cumulative idle, kernel and user time across every processor, in 100ns ticks
        /// since boot. Kernel time INCLUDES idle time - busy is (kernel + user - idle).
        /// FILETIMEs, read straight into longs: both are 8 bytes, little-endian, and the
        /// low dword comes first in each.
        /// </summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

        [StructLayout(LayoutKind.Sequential)]
        public struct MEMORYSTATUSEX
        {
            public uint dwLength;           // must be set to the struct size before the call
            public uint dwMemoryLoad;       // percent, 0..100
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);

        // PDH, for the one figure the kernel32 reads above cannot give: "% Processor
        // Utility", which is what Task Manager shows for CPU on Windows 10. Straight to
        // pdh.dll rather than through PerformanceCounter, for the two reasons in the note
        // above: PdhAddEnglishCounter takes the English path on any display language, and
        // a single counter on a query of our own loads only the provider behind it. What
        // that costs on first use is printed by --stats.
        //
        // Every call returns a PDH status, 0 for success - not a BOOL, and not an HRESULT
        // to be thrown.

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        public static extern uint PdhOpenQuery(string dataSource, IntPtr userData, out IntPtr query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        public static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData,
                                                       out IntPtr counter);

        [DllImport("pdh.dll")]
        public static extern uint PdhCollectQueryData(IntPtr query);

        [DllImport("pdh.dll")]
        public static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format,
                                                              out uint type,
                                                              out PDH_FMT_COUNTERVALUE value);

        [DllImport("pdh.dll")]
        public static extern uint PdhCloseQuery(IntPtr query);

        /// <summary>
        /// PDH's formatted value: a status, then a union of long, double and string pointer.
        /// The union is 8-byte aligned, so it starts at 8, not 4 - Explicit rather than
        /// Sequential so that is written down instead of left to the marshaller.
        /// </summary>
        [StructLayout(LayoutKind.Explicit)]
        public struct PDH_FMT_COUNTERVALUE
        {
            [FieldOffset(0)] public uint CStatus;
            [FieldOffset(8)] public double doubleValue;
        }

        /// <summary>
        /// Format as a double. Percentages over 100 are capped at 100 - PDH's default, and
        /// what Task Manager does with a boosting CPU's utility, which can pass 100.
        /// </summary>
        public const uint PDH_FMT_DOUBLE = 0x00000200;

        /// <summary>
        /// A value's CStatus when it is good. Anything else - and in particular the
        /// invalid-data status a rate counter returns after only one collection, when there
        /// is nothing yet to measure from - means the value is not to be used.
        /// </summary>
        public const uint PDH_CSTATUS_VALID_DATA = 0x00000000;
        public const uint PDH_CSTATUS_NEW_DATA = 0x00000001;

        // --- is the taskbar on screen ------------------------------------------

        // The pinned stats panel is a top-level topmost window, so unlike the strip - a
        // child of the taskbar, which Windows hides along with it - it has to be told when
        // the taskbar is not there to rest on. These answer that cheaply enough to ask four
        // times a second.

        /// <summary>
        /// The shell's own verdict on whether now is a time to put something in front of
        /// the user. It is what toast notifications consult, so it already knows the cases
        /// that matter - a fullscreen game, a fullscreen video, a presentation, the lock
        /// screen - without our having to compare window rects against monitors.
        /// </summary>
        [DllImport("shell32.dll")]
        public static extern int SHQueryUserNotificationState(out int state);

        public const int QUNS_NOT_PRESENT = 1;               // locked, screensaver, user switch
        public const int QUNS_BUSY = 2;                      // a fullscreen app has the foreground
        public const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;   // exclusive fullscreen D3D
        public const int QUNS_PRESENTATION_MODE = 4;

        [StructLayout(LayoutKind.Sequential)]
        public struct MONITORINFO
        {
            public int cbSize;              // must be set to the struct size before the call
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

        [DllImport("user32.dll")]
        public static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

        public const uint MONITOR_DEFAULTTONEAREST = 2;

        // --- low-level mouse hook ----------------------------------------------

        // For closing the pinned stats panel on a click anywhere else. See ClickWatcher.

        public delegate IntPtr LowLevelMouseProc(int code, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc proc,
                                                     IntPtr module, uint threadId);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnhookWindowsHookEx(IntPtr hook);

        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandle(string moduleName);

        [StructLayout(LayoutKind.Sequential)]
        public struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public POINT pt;
        }

        [DllImport("user32.dll")]
        public static extern int GetMessage(out MSG msg, IntPtr hWnd, uint filterMin, uint filterMax);

        [DllImport("user32.dll")]
        public static extern bool PeekMessage(out MSG msg, IntPtr hWnd, uint filterMin, uint filterMax, uint remove);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool PostThreadMessage(uint threadId, int msg, IntPtr wParam, IntPtr lParam);

        public const int WH_MOUSE_LL = 14;
        public const uint PM_NOREMOVE = 0x0000;

        public const int WM_QUIT = 0x0012;
        public const int WM_APP = 0x8000;
        public const int WM_LBUTTONDOWN = 0x0201;
        public const int WM_RBUTTONDOWN = 0x0204;
        public const int WM_MBUTTONDOWN = 0x0207;
        public const int WM_XBUTTONDOWN = 0x020B;

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
        /// The process actually behind a window, which is not always the one that owns the
        /// hwnd: a packaged app's frame belongs to ApplicationFrameHost, and every packaged
        /// app shares that one process. Resolving through to the app's own pid keeps
        /// Settings, Calculator and the Store distinguishable - and keeps a per-pid cache
        /// honest, since otherwise they would all collide on one key.
        ///
        /// Returns 0 if the window has no process, which is not something a live window
        /// should manage.
        /// </summary>
        public static uint GetOwningProcessId(IntPtr hWnd)
        {
            uint pid;
            GetWindowThreadProcessId(hWnd, out pid);
            if (pid == 0) return 0;

            // Class check rather than an image-name check: it is free, where working out
            // that the frame is ApplicationFrameHost costs a process query of its own.
            if (GetClass(hWnd) != "ApplicationFrameWindow") return pid;

            uint hosted = HostedProcessId(hWnd, pid);
            return hosted != 0 ? hosted : pid;
        }

        /// <summary>
        /// The application a process is, ready to show - "Chrome", "Explorer" - or "" if it
        /// cannot be worked out.
        ///
        /// Taken from the process rather than parsed out of a window title, so it is still
        /// right for the many windows whose caption never names their app.
        ///
        /// Never throws: this runs on a hover, and a tooltip must not be able to take the
        /// strip down.
        /// </summary>
        public static string GetAppName(uint pid)
        {
            if (pid == 0) return string.Empty;

            try
            {
                return Pretty(ImageBaseName(pid));
            }
            catch (Exception ex)
            {
                Log.Write("native: app name failed - " + ex.Message);
                return string.Empty;
            }
        }

        static string ImageBaseName(uint pid)
        {
            IntPtr process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (process == IntPtr.Zero) return string.Empty;

            try
            {
                var sb = new StringBuilder(512);
                int size = sb.Capacity;
                if (!QueryFullProcessImageName(process, 0, sb, ref size)) return string.Empty;

                string path = sb.ToString();
                int slash = path.LastIndexOf('\\');
                if (slash >= 0) path = path.Substring(slash + 1);

                if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    path = path.Substring(0, path.Length - 4);

                return path;
            }
            finally
            {
                CloseHandle(process);
            }
        }

        /// <summary>The pid behind a packaged app's frame window, or 0.</summary>
        static uint HostedProcessId(IntPtr frame, uint framePid)
        {
            uint found = 0;

            EnumChildWindows(frame, delegate(IntPtr child, IntPtr param)
            {
                if (GetClass(child) != "Windows.UI.Core.CoreWindow") return true;

                uint pid;
                GetWindowThreadProcessId(child, out pid);
                if (pid == 0 || pid == framePid) return true;

                found = pid;
                return false;
            }, IntPtr.Zero);

            return found;
        }

        /// <summary>
        /// Exe names that do not read as the app they are. Deliberately short - the point
        /// is the handful a user meets daily, not a catalogue.
        /// </summary>
        static readonly Dictionary<string, string> AppAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "msedge", "Edge" },
            { "iexplore", "Internet Explorer" },
            { "WINWORD", "Word" },
            { "EXCEL", "Excel" },
            { "POWERPNT", "PowerPoint" },
            { "OUTLOOK", "Outlook" },
            { "devenv", "Visual Studio" },
            { "Code", "VS Code" },
            { "WindowsTerminal", "Terminal" },
            { "Taskmgr", "Task Manager" },

            // Packaged apps, whose exe names are internal identifiers.
            { "CalculatorApp", "Calculator" },
            { "SystemSettings", "Settings" },
            { "Microsoft.Photos", "Photos" },
            { "WinStore.App", "Store" },

            // Only reached when the hosted CoreWindow could not be found; still better
            // than showing the shell's own process name.
            { "ApplicationFrameHost", "Windows app" },
        };

        static string Pretty(string exe)
        {
            if (string.IsNullOrEmpty(exe)) return string.Empty;

            string alias;
            if (AppAliases.TryGetValue(exe, out alias)) return alias;

            // A vendor shipping a mixed-case exe name has already chosen its casing;
            // only the all-lowercase ones ("chrome", "notepad") want a capital.
            if (exe.ToLowerInvariant() == exe)
                return char.ToUpperInvariant(exe[0]) + exe.Substring(1);

            return exe;
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

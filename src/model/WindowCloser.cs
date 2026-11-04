using System;
using System.Collections.Generic;

namespace DesktopSwitcher
{
    /// <summary>Where one window is in a close-all, as the progress panel shows it.</summary>
    enum CloseState
    {
        /// <summary>Asked, not gone yet, nothing in the way that can be seen.</summary>
        Closing,

        /// <summary>
        /// Still Closing past the first second. Usually a question drawn inside the window
        /// itself - Terminal's "close all tabs?", Notepads' "save?" - which, unlike a real
        /// dialog, does not disable the window and so cannot be told apart from a window
        /// that is merely slow. Shown as "still open" rather than guessed at.
        /// </summary>
        StillOpen,

        /// <summary>A modal prompt is up over it - "Save changes?", almost always.</summary>
        Prompting,

        /// <summary>Was prompting, and is now open and enabled again: the user answered
        /// the prompt with Cancel. Nothing more will happen to it by itself.</summary>
        KeptOpen,

        /// <summary>Would not take the request, or survived a force. Elevated, usually.</summary>
        Refused,

        Closed,
    }

    /// <summary>
    /// Closes every window on one desktop - the menu's "Close all and remove". The policy
    /// of a run, and the panel that shows it, are CloseAllRun's; this is the mechanism.
    ///
    /// Asks before it forces. Each window is sent WM_CLOSE, which is what its own X sends,
    /// so an app with unsaved work puts up its "Save changes?" and waits rather than losing
    /// it. Nothing can find unsaved work out in advance: Windows has no "is this document
    /// dirty" query, and the title markers some editors use (a leading * or a dot) are a
    /// convention many apps do not follow.
    ///
    /// What can be seen, almost at once, is the prompt itself. A modal dialog disables the
    /// window it belongs to, so a window that is still open and has gone disabled is
    /// waiting on its user - no fixed wait needed to tell "slow to close" from "asking".
    /// The prompt opens on the window's own desktop, usually not the one being looked at,
    /// which is why the run offers to go there. Not to bring it here: moving a window
    /// between desktops mid-close proved fragile - Brave and Terminal moved, Notepads
    /// vanished with its prompt unanswered.
    ///
    /// Only real dialogs are seen this way. Many apps draw the question inside their own
    /// window (Terminal, Notepads, anything built on XAML), which leaves it enabled, so
    /// those read as merely still open.
    ///
    /// Forcing ends the owning process. There is no per-window force in Win32 - a window
    /// that will not close belongs to a process, and only the process can be stopped - so
    /// a forced Chrome takes its windows on every other desktop with it. The panel says so.
    /// </summary>
    sealed class WindowCloser
    {
        /// <summary>
        /// Processes never forced. Explorer owns every File Explorer window and the taskbar
        /// the strip lives in; ApplicationFrameHost owns every packaged app's frame, so
        /// ending it closes all of them at once - it is only reached when a frame's own app
        /// could not be resolved. Both close politely anyway; refusing to force them only
        /// matters in the rare case they do not.
        /// </summary>
        static readonly HashSet<string> NeverForce = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "explorer.exe",
            "ApplicationFrameHost.exe",
        };

        const string CoreWindowClass = "Windows.UI.Core.CoreWindow";

        static readonly uint OwnPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;

        readonly VirtualDesktopApi _api;
        readonly WindowInventory _inventory;

        public WindowCloser(VirtualDesktopApi api, WindowInventory inventory)
        {
            _api = api;
            _inventory = inventory;
        }

        /// <summary>
        /// The windows a close-all on this desktop would ask: the inventory's list, less
        /// windows pinned to every desktop, which would vanish from all of them, and less
        /// our own - the Settings window is an ordinary app window, and asking it to close
        /// is harmless, but forcing its process would end the app doing the closing.
        ///
        /// Costs a pin check per window, a few COM calls each. The confirmation asks, and a
        /// run asks again before it removes the desktop; the menu uses HasCloseable.
        /// </summary>
        public List<WindowEntry> Closeable(Guid desktop)
        {
            var result = new List<WindowEntry>();
            IList<WindowEntry> windows = _inventory.WindowsOn(desktop);

            for (int i = 0; i < windows.Count; i++)
                if (IsCloseable(windows[i])) result.Add(windows[i]);

            return result;
        }

        /// <summary>
        /// Whether Closeable would list anything, stopping at the first window that
        /// qualifies. What the menu asks, to grey its item out: the pin check is a few COM
        /// calls per other desktop, and paying it for every window on every right-click
        /// put a visible delay in front of the menu on a busy machine.
        /// </summary>
        public bool HasCloseable(Guid desktop)
        {
            IList<WindowEntry> windows = _inventory.WindowsOn(desktop);
            for (int i = 0; i < windows.Count; i++)
                if (IsCloseable(windows[i])) return true;
            return false;
        }

        /// <summary>
        /// Our own process first, then pinning: the cheap check before the one that goes
        /// to the shell.
        ///
        /// A CoreWindow the inventory failed to pair with its frame is kept. It is still an
        /// open app on this desktop, and leaving it out let the run call the desktop empty
        /// and remove it with the app still there. It is waited on, never asked - see Ask.
        /// </summary>
        bool IsCloseable(WindowEntry w)
        {
            if (Native.GetOwningProcessId(w.Handle) == OwnPid) return false;
            return !IsPinned(w.Handle);
        }

        bool IsPinned(IntPtr hwnd)
        {
            try
            {
                return _api.IsPinned(hwnd);
            }
            catch (Exception ex)
            {
                // Mid-restart. Not pinned is the answer that leaves the window in the list,
                // where the confirmation's count shows it - a guess the user sees, rather
                // than one they do not.
                Log.Write("closer: pin check failed - " + ex.Message);
                return false;
            }
        }

        /// <summary>The sweep behind Closeable is now wrong about exactly this desktop.</summary>
        public void Invalidate()
        {
            _inventory.Invalidate();
        }

        /// <summary>
        /// Asks one window to close. False when it refused the message outright, which is
        /// what an elevated window does to an unelevated sender (UIPI) - nothing will close
        /// it from here, and the run shows it as refused straight away rather than as
        /// "closing" forever.
        ///
        /// Posted, never sent. SendMessage would block this thread on the app's close
        /// handler, and a handler that opens a modal save prompt does not return until the
        /// prompt is answered - on a desktop nobody is looking at.
        /// </summary>
        public static bool Ask(IntPtr hwnd)
        {
            // A packaged app is closed through its frame, never its CoreWindow. Posting
            // WM_CLOSE to the CoreWindow as well is the likeliest reason Notepads once
            // vanished with its save prompt unanswered. An unpaired one is left to go with
            // its frame, and reported as asked - it is not refusing anything - so the run
            // shows it as still open until it does, and Force close is there if it never
            // does.
            if (Native.GetClass(hwnd) == CoreWindowClass)
            {
                Log.Write("closer: not asking a CoreWindow, waiting on its frame");
                return true;
            }

            if (Native.PostMessage(hwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero)) return true;

            int error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            Log.Write(delegate
            {
                return "closer: close refused by \"" + Native.GetText(hwnd) + "\" (error " + error + ")";
            });
            return false;
        }

        /// <summary>
        /// Gone, asking, or neither yet. Gone means destroyed, or hidden: plenty of apps -
        /// chat clients, Steam, anything with a tray icon - answer WM_CLOSE by hiding, which
        /// is how they close, and the window is no longer on the desktop either way.
        /// Counting those as open would offer to force-quit an app that did exactly what
        /// its X is meant to do.
        /// </summary>
        public static bool IsGone(IntPtr hwnd)
        {
            return !Native.IsWindow(hwnd) || !Native.IsWindowVisible(hwnd);
        }

        /// <summary>True while a modal prompt is up over the window.</summary>
        public static bool IsPrompting(IntPtr hwnd)
        {
            return !Native.IsWindowEnabled(hwnd);
        }

        /// <summary>
        /// Ends the processes behind these windows. A process refused here - elevated,
        /// protected, or in NeverForce - simply keeps its windows; the run sees them still
        /// open and marks them refused.
        /// </summary>
        public static void Force(IList<IntPtr> windows)
        {
            var tried = new HashSet<uint>();

            for (int i = 0; i < windows.Count; i++)
            {
                // Resolved now, not taken from the sweep: GetOwningProcessId sees through a
                // packaged app's frame to the app itself rather than to ApplicationFrameHost,
                // and a window gone since has no pid to end.
                uint pid = Native.GetOwningProcessId(windows[i]);
                if (pid == 0 || pid == OwnPid || !tried.Add(pid)) continue;

                string path = Native.GetProcessPath(pid);
                string exe = path.Substring(path.LastIndexOf('\\') + 1);

                // Fails closed. An unreadable path cannot be checked against NeverForce,
                // and terminating whatever it is might be ending Explorer and the taskbar
                // with it. A process whose name cannot be read is very likely one that
                // cannot be terminated either, so little is given up.
                if (exe.Length == 0)
                {
                    Log.Write("closer: will not force pid " + pid + ", its exe could not be read");
                    continue;
                }

                if (NeverForce.Contains(exe))
                {
                    Log.Write("closer: will not force " + exe);
                    continue;
                }

                bool ok = Native.KillProcess(pid);
                Log.Write("closer: force " + exe + (ok ? " ended" : " refused"));
            }
        }
    }
}

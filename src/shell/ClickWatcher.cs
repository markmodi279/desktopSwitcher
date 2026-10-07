using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace DesktopSwitcher
{
    /// <summary>
    /// Reports every mouse button press anywhere on screen, by posting a message to a
    /// window. What lets the pinned stats panel close on a click outside it, the way a
    /// popup does.
    ///
    /// Nothing narrower can see those clicks. The strip only hears clicks on itself, and
    /// the panel is WS_EX_TRANSPARENT, so it hears none at all. A foreground-change hook
    /// was the cheaper option and was not enough: clicking in the window that already has
    /// focus changes nothing, and that is the most common click there is. A low-level
    /// mouse hook sees every press, whoever it lands on.
    ///
    /// The hook runs on a thread of its own, never on the UI thread. A low-level hook is
    /// called on the thread that installed it, and every mouse event on the system - every
    /// move, not just clicks - waits for that thread to answer. On the UI thread that means
    /// the cursor across the whole desktop stutters whenever the strip is busy: a COM call
    /// into the shell, an inventory sweep, a 70ms adapter enumeration. Past
    /// LowLevelHooksTimeout Windows also quietly removes a slow hook, and the panel would
    /// stop closing for no visible reason. This thread does nothing but pump, so it always
    /// answers at once.
    ///
    /// The callback itself only posts. Hit-testing the point against the strip happens on
    /// the UI thread, where the strip's state lives, when the message arrives.
    ///
    /// Installed only while the panel is pinned, so the rest of the time the app has no
    /// hook in the system at all.
    /// </summary>
    sealed class ClickWatcher : IDisposable
    {
        readonly IntPtr _target;
        readonly int _message;

        // Held in a field for the life of the hook. The native side keeps only a function
        // pointer, which does not keep the delegate alive; collected, the next mouse event
        // calls into freed memory.
        readonly Native.LowLevelMouseProc _proc;

        readonly Thread _thread;
        readonly ManualResetEvent _ready = new ManualResetEvent(false);

        // Both cross threads: _threadId is written by the hook thread, _disposed by the UI
        // thread. Volatile so neither read can be served from a stale copy.
        volatile uint _threadId;
        volatile bool _disposed;

        /// <summary>
        /// Starts watching. Each press posts <paramref name="message"/> to
        /// <paramref name="target"/>, with the screen point packed into lParam the way a
        /// mouse message packs it - read it back with Native.LoWord and Native.HiWord.
        /// </summary>
        public ClickWatcher(IntPtr target, int message)
        {
            _target = target;
            _message = message;
            _proc = OnMouse;

            _thread = new Thread(Run);
            _thread.IsBackground = true;   // never the thing keeping the process alive
            _thread.Name = "ClickWatcher";
            _thread.Start();

            // So Dispose always has a thread id and a queue to post WM_QUIT to. Installing a
            // hook takes microseconds; the timeout is for a thread that never got scheduled.
            _ready.WaitOne(1000);
        }

        void Run()
        {
            // A thread has no message queue until it first touches one, and
            // PostThreadMessage to a thread without one fails. Created before the id is
            // published, so any Dispose that can see the id can also reach the queue.
            Native.MSG msg;
            Native.PeekMessage(out msg, IntPtr.Zero, 0, 0, Native.PM_NOREMOVE);
            _threadId = Native.GetCurrentThreadId();

            // A full fence, not just volatile: this is a store followed by a load of
            // _disposed, mirrored in Dispose, and volatile alone lets a store and a later load
            // trade places. With both fenced, at least one side sees the other's write.
            Thread.MemoryBarrier();

            IntPtr hook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _proc,
                                                  Native.GetModuleHandle(null), 0);
            if (hook == IntPtr.Zero)
                Log.Write("clicks: hook failed - " + Marshal.GetLastWin32Error());

            _ready.Set();

            try
            {
                // The hook is called from inside GetMessage; there is nothing else for this
                // loop to do. Exits on WM_QUIT (0) or error (-1). Skipped when Dispose ran
                // before the id above was published: it had no queue to post WM_QUIT to,
                // and this check is the other half of that handshake.
                if (hook != IntPtr.Zero && !_disposed)
                    while (Native.GetMessage(out msg, IntPtr.Zero, 0, 0) > 0) { }
            }
            finally
            {
                // On the thread that installed it, which is the one place it is certain to
                // be allowed.
                if (hook != IntPtr.Zero) Native.UnhookWindowsHookEx(hook);
            }
        }

        IntPtr OnMouse(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0)
            {
                switch ((int)(long)wParam)
                {
                    case Native.WM_LBUTTONDOWN:
                    case Native.WM_RBUTTONDOWN:
                    case Native.WM_MBUTTONDOWN:
                    case Native.WM_XBUTTONDOWN:
                        // MSLLHOOKSTRUCT opens with the point, in physical screen
                        // coordinates - the same space GetWindowRect answers in for this
                        // per-monitor-aware process. Read straight off the pointer rather
                        // than marshalling the whole struct for two ints.
                        int x = Marshal.ReadInt32(lParam, 0);
                        int y = Marshal.ReadInt32(lParam, 4);
                        int packed = (x & 0xFFFF) | (y << 16);
                        Native.PostMessage(_target, _message, IntPtr.Zero, new IntPtr(packed));
                        break;
                }
            }

            return Native.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Thread.MemoryBarrier();   // see the matching fence in Run

            // Not joined: the thread is a background one, it unhooks on its way out, and
            // waiting on it from the UI thread is a wait the UI does not need to make.
            // _disposed is set first, so a thread that has not published its id yet sees it
            // and skips its loop.
            //
            // _ready is deliberately not closed. If the constructor's wait timed out, the
            // thread has yet to Set it, and Set on a closed handle throws on a background
            // thread - which ends the process. The finaliser collects it.
            uint id = _threadId;
            if (id != 0) Native.PostThreadMessage(id, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }
    }
}

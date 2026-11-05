using System;

namespace DesktopSwitcher
{
    /// <summary>
    /// Calls back the moment Windows reports an event on a window: it moved, was shown,
    /// hidden or destroyed, or came to the foreground. One process or all of them.
    ///
    /// Everything the controller used to find out by polling it now hears about through one
    /// of these. Started as the watcher on MediaController's tile, which glides when the tray
    /// moves it: the strip sits against the tile, so anything slower than the tile's own
    /// frames leaves the tile drawn over the strip for as long as the strip is late, and a
    /// poll, however fast, is always a frame or two behind what it is polling. The same
    /// reasoning then retired the other polls - a 40 ms timer asking whether the tray had
    /// moved and a 300 ms one asking which window had focus, which between them woke the
    /// process nearly thirty times a second to learn, almost always, that nothing had
    /// changed. A hook costs nothing while nothing happens.
    ///
    /// Out of context means no DLL is injected anywhere; it is a plain notification, posted
    /// to the thread that made the hook, so the callback runs on the UI thread between other
    /// messages and needs no locking. Only events about a window itself are passed on -
    /// not its caret, scroll bars, or the accessible children inside it - which is also
    /// most of the volume a busy hook would otherwise wake us for.
    /// </summary>
    sealed class WinEventWatcher : IDisposable
    {
        readonly Native.WinEventProc _proc;   // held: native code keeps only a pointer to it
        readonly Action<IntPtr> _changed;
        IntPtr _hook;

        /// <summary>The process being watched; 0 when the hook is system-wide.</summary>
        public uint ProcessId { get; private set; }

        public WinEventWatcher(uint eventMin, uint eventMax, uint processId, bool skipOwnProcess,
                               Action<IntPtr> changed)
        {
            ProcessId = processId;
            _changed = changed;
            _proc = OnEvent;

            uint flags = Native.WINEVENT_OUTOFCONTEXT;
            if (skipOwnProcess) flags |= Native.WINEVENT_SKIPOWNPROCESS;

            _hook = Native.SetWinEventHook(eventMin, eventMax, IntPtr.Zero, _proc, processId, 0, flags);
            if (_hook == IntPtr.Zero)
                Log.Write("winevent: hook failed for 0x" + eventMin.ToString("X") + " pid " + processId);
        }

        /// <summary>Whether the hook was made. The watchdog is the fallback when it was not.</summary>
        public bool Hooked { get { return _hook != IntPtr.Zero; } }

        void OnEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint threadId, uint time)
        {
            if (_hook == IntPtr.Zero) return;   // one already queued when Dispose ran
            if (idObject != Native.OBJID_WINDOW || idChild != Native.CHILDID_SELF) return;

            // An exception escaping here unwinds into user32's dispatch, not into anything of
            // ours that could report it. Logged and dropped: the watchdog's next tick covers
            // whatever this event would have done.
            try { _changed(hwnd); }
            catch (Exception ex) { Log.Write("winevent: handler failed - " + ex.Message); }
        }

        public void Dispose()
        {
            if (_hook != IntPtr.Zero) { Native.UnhookWinEvent(_hook); _hook = IntPtr.Zero; }
        }
    }
}

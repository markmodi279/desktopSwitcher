using System;

namespace DesktopSwitcher
{
    /// <summary>
    /// Calls back the moment any window of one process moves or resizes.
    ///
    /// What lets the strip follow MediaController's tile frame by frame. The tile glides when
    /// the tray moves it, and the strip sits against the tile, so anything slower than the
    /// tile's own frames leaves the tile drawn over the strip for as long as the strip is late.
    /// A poll, however fast, is always a frame or two behind what it is polling.
    ///
    /// Filtered to one process, so the callback is not woken by every window on the desktop.
    /// Out of context means no DLL is injected anywhere; it is a plain notification.
    /// </summary>
    sealed class LocationWatcher : IDisposable
    {
        readonly Native.WinEventProc _proc;   // held: native code keeps only a pointer to it
        readonly Action _changed;
        IntPtr _hook;

        public uint ProcessId { get; private set; }

        public LocationWatcher(uint processId, Action changed)
        {
            ProcessId = processId;
            _changed = changed;
            _proc = OnEvent;
            _hook = Native.SetWinEventHook(Native.EVENT_OBJECT_LOCATIONCHANGE, Native.EVENT_OBJECT_LOCATIONCHANGE,
                IntPtr.Zero, _proc, processId, 0, Native.WINEVENT_OUTOFCONTEXT);
            if (_hook == IntPtr.Zero) Log.Write("location: hook failed for pid " + processId);
        }

        void OnEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint threadId, uint time)
        {
            if (idObject == Native.OBJID_WINDOW) _changed();
        }

        public void Dispose()
        {
            if (_hook != IntPtr.Zero) { Native.UnhookWinEvent(_hook); _hook = IntPtr.Zero; }
        }
    }
}

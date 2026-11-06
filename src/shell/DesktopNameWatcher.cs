using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace DesktopSwitcher
{
    /// <summary>
    /// Says whether Explorer has written a desktop name since the last time it was asked.
    ///
    /// Names were read only on set changes, and our own rename patched the model by hand,
    /// so a rename made anywhere else - Task View's name box, another tool, a second copy
    /// of this app - never reached the strip. The hover panel and the menus went on showing
    /// the old name until a desktop was added or removed, or the app restarted. Found by
    /// renaming from a selftest beside the running app: the log said renamed, the panel
    /// said "Desktop 3".
    ///
    /// The notification sink cannot help. On 19045 IVirtualDesktopNotification has no
    /// rename callback; the one that does is a later interface this build does not offer.
    ///
    /// Re-reading every name on every tick would work, but the tick was just made to do
    /// nothing at all when nothing has changed (see DesktopService.BackstopMs), and this
    /// would be the one thing it still did. So instead the registry tells us: one
    /// RegNotifyChangeKeyValue on Explorer's VirtualDesktops key, watching the subtree,
    /// signals an event when anything under it is written. The tick checks the event with
    /// a zero wait, which costs nothing, and re-reads names only when it is set.
    ///
    /// The key, not just Desktops under it: Desktops only exists once some desktop has
    /// been named, and on a fresh profile there is nothing to watch. The parent holds
    /// VirtualDesktopIDs as well, so a set change also signals - harmless, a set change
    /// re-reads names anyway. The current desktop is kept in SessionInfo, elsewhere, so
    /// switching does not.
    ///
    /// A notification is one-shot: once signalled it must be re-armed. Re-arming before
    /// the read means a write landing during the read signals again rather than being
    /// lost.
    ///
    /// Not REG_NOTIFY_THREAD_AGNOSTIC, which is Windows 8 on and fine here, but not needed:
    /// the registration ends when the thread that made it exits, and this is created and
    /// re-armed on the UI thread, which lives as long as the app does.
    /// </summary>
    sealed class DesktopNameWatcher : IDisposable
    {
        const string KeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops";

        static readonly UIntPtr HKEY_CURRENT_USER = new UIntPtr(0x80000001u);
        const int KEY_NOTIFY = 0x0010;
        const int REG_NOTIFY_CHANGE_NAME = 0x1;      // subkey added or removed
        const int REG_NOTIFY_CHANGE_LAST_SET = 0x4;  // value written

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        static extern int RegOpenKeyEx(UIntPtr hKey, string subKey, int options, int samDesired, out IntPtr result);

        [DllImport("advapi32.dll")]
        static extern int RegNotifyChangeKeyValue(IntPtr hKey, bool watchSubtree, int notifyFilter,
                                                  IntPtr hEvent, bool asynchronous);

        [DllImport("advapi32.dll")]
        static extern int RegCloseKey(IntPtr hKey);

        readonly AutoResetEvent _changed = new AutoResetEvent(false);
        IntPtr _key;

        /// <summary>
        /// Starts watching. Without the key - a profile that has never had a second desktop
        /// may not have it - this is inert and Changed is always false; Arm is retried on
        /// each call until the key turns up.
        /// </summary>
        public DesktopNameWatcher()
        {
            Arm();
        }

        bool Arm()
        {
            if (_key == IntPtr.Zero)
            {
                IntPtr key;
                if (RegOpenKeyEx(HKEY_CURRENT_USER, KeyPath, 0, KEY_NOTIFY, out key) != 0)
                    return false;
                _key = key;
            }

            int rc = RegNotifyChangeKeyValue(_key, true,
                REG_NOTIFY_CHANGE_NAME | REG_NOTIFY_CHANGE_LAST_SET,
                _changed.SafeWaitHandle.DangerousGetHandle(), true);
            if (rc == 0) return true;

            Log.Write("names: registry watch failed, error " + rc);
            RegCloseKey(_key);
            _key = IntPtr.Zero;
            return false;
        }

        /// <summary>
        /// True once per burst of writes since the last call. Re-arms as it answers.
        /// A watch that could not be set up reports true on the call that finally sets it
        /// up, since anything could have changed while nobody was looking.
        /// </summary>
        public bool Changed()
        {
            if (_key == IntPtr.Zero) return Arm();
            if (!_changed.WaitOne(0)) return false;
            Arm();
            return true;
        }

        public void Dispose()
        {
            if (_key != IntPtr.Zero)
            {
                // Closing the key ends the pending notification; it signals the event one
                // last time, which nobody is waiting on.
                RegCloseKey(_key);
                _key = IntPtr.Zero;
            }
            _changed.Close();
        }
    }
}

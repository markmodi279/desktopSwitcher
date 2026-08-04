using System;
using System.Collections.Generic;

namespace DesktopSwitcher
{
    /// <summary>
    /// Which windows are open on which desktop.
    ///
    /// Windows on other virtual desktops stay enumerable - they are DWM-cloaked, not
    /// hidden - so one flat EnumWindows sweep plus one GetWindowDesktopId per window
    /// buckets the whole machine at once. There is no shell API that answers "what is on
    /// desktop N" directly.
    ///
    /// Cloaking cannot be used as the filter, because the shell cloaks suspended UWP
    /// frames with the same flag it uses for windows on an inactive desktop. What
    /// separates them is that the ghosts have no desktop: GetWindowDesktopId fails or
    /// returns GUID_NULL for them, which TryGetWindowDesktop already reports as false.
    ///
    /// Built on demand and cached briefly - never on a timer. A hover is the only thing
    /// that asks, and this costs a COM call per open window.
    /// </summary>
    sealed class WindowInventory
    {
        /// <summary>
        /// Long enough that moving along the strip reuses one sweep, short enough that a
        /// window opened while the tooltip is up is not stale for noticeably long.
        /// </summary>
        const int CacheMs = 1000;

        readonly VirtualDesktopApi _api;
        readonly System.Diagnostics.Stopwatch _age = new System.Diagnostics.Stopwatch();

        Dictionary<Guid, List<string>> _byDesktop = new Dictionary<Guid, List<string>>();

        public WindowInventory(VirtualDesktopApi api)
        {
            _api = api;
        }

        /// <summary>Forces the next query to re-sweep. Called when the desktop set changes.</summary>
        public void Invalidate()
        {
            _age.Reset();
        }

        /// <summary>
        /// Window titles on a desktop, most recently used first, or an empty list. The
        /// full list is returned; capping for display is the caller's business, so it can
        /// report an accurate "+N more".
        /// </summary>
        public IList<string> WindowsOn(Guid desktop)
        {
            EnsureFresh();

            List<string> titles;
            if (_byDesktop.TryGetValue(desktop, out titles)) return titles;
            return new List<string>();
        }

        void EnsureFresh()
        {
            if (_age.IsRunning && _age.ElapsedMilliseconds < CacheMs) return;
            Refresh();
        }

        void Refresh()
        {
            var map = new Dictionary<Guid, List<string>>();

            try
            {
                // GetTopWindow/GW_HWNDNEXT rather than EnumWindows: same set, but in
                // z-order, so each desktop's list reads most-recently-used first.
                IntPtr hwnd = Native.GetTopWindow(IntPtr.Zero);
                while (hwnd != IntPtr.Zero)
                {
                    AddIfReal(map, hwnd);
                    hwnd = Native.GetWindow(hwnd, Native.GW_HWNDNEXT);
                }
            }
            catch (ShellUnavailableException)
            {
                // Explorer is mid-restart. Keep the last sweep rather than blanking every
                // tooltip; the next hover after it returns rebuilds.
                Log.Write("inventory: shell unavailable, holding last sweep");
                return;
            }
            catch (Exception ex)
            {
                Log.Write("inventory: sweep failed - " + ex.Message);
                return;
            }

            _byDesktop = map;
            _age.Restart();

            if (Log.Enabled)
                Log.Write(delegate { return "inventory: swept " + Total(map) + " windows across " + map.Count + " desktops"; });
        }

        void AddIfReal(Dictionary<Guid, List<string>> map, IntPtr hwnd)
        {
            if (!Native.IsAltTabWindow(hwnd)) return;

            Guid desktop;
            if (!_api.TryGetWindowDesktop(hwnd, out desktop)) return;

            string title = Native.GetText(hwnd);
            if (title.Length == 0) return;

            List<string> titles;
            if (!map.TryGetValue(desktop, out titles))
            {
                titles = new List<string>();
                map[desktop] = titles;
            }
            titles.Add(title);
        }

        static int Total(Dictionary<Guid, List<string>> map)
        {
            int n = 0;
            foreach (List<string> titles in map.Values) n += titles.Count;
            return n;
        }
    }
}

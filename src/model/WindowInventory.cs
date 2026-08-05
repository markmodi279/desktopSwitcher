using System;
using System.Collections.Generic;

namespace DesktopSwitcher
{
    /// <summary>
    /// One open window, as a tooltip wants to read it: the app that owns it, then what it
    /// is showing. Kept apart rather than pre-joined so the display format stays the
    /// caller's business.
    /// </summary>
    sealed class WindowEntry
    {
        public string App;
        public string Title;

        public WindowEntry(string app, string title)
        {
            App = app;
            Title = title;
        }
    }

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
    /// that asks, and this costs a COM call plus a process query per open window.
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

        Dictionary<Guid, List<WindowEntry>> _byDesktop = new Dictionary<Guid, List<WindowEntry>>();

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
        /// Windows on a desktop, most recently used first, or an empty list. The full list
        /// is returned; capping for display is the caller's business, so it can report an
        /// accurate "+N more".
        /// </summary>
        public IList<WindowEntry> WindowsOn(Guid desktop)
        {
            EnsureFresh();

            List<WindowEntry> windows;
            if (_byDesktop.TryGetValue(desktop, out windows)) return windows;
            return new List<WindowEntry>();
        }

        /// <summary>
        /// One window read the way a sweep reads it, for a caller that already holds the
        /// handle and is asking about that window rather than about a desktop. No cache and
        /// no desktop lookup, so it costs one process query.
        ///
        /// A packaged app is named from its hosted CoreWindow by GetOwningProcessId, so it
        /// comes out right without the frame pairing a sweep needs - that exists to stop one
        /// app being listed twice, which cannot arise with a single handle.
        /// </summary>
        public static WindowEntry Describe(IntPtr hwnd)
        {
            string title = Native.GetText(hwnd);
            string app = Native.GetAppName(Native.GetOwningProcessId(hwnd));
            return new WindowEntry(app, TrimRepeat(title, app));
        }

        void EnsureFresh()
        {
            if (_age.IsRunning && _age.ElapsedMilliseconds < CacheMs) return;
            Refresh();
        }

        void Refresh()
        {
            var map = new Dictionary<Guid, List<WindowEntry>>();

            // Ten Chrome windows are one process, so resolve each pid once. Local to the
            // sweep on purpose: a pid the kernel has since recycled can never hand back a
            // dead process's name.
            var apps = new Dictionary<uint, string>();

            try
            {
                // GetTopWindow/GW_HWNDNEXT rather than EnumWindows: same set, but in
                // z-order, so each desktop's list reads most-recently-used first.
                var candidates = new List<IntPtr>();
                IntPtr hwnd = Native.GetTopWindow(IntPtr.Zero);
                while (hwnd != IntPtr.Zero)
                {
                    if (Native.IsAltTabWindow(hwnd)) candidates.Add(hwnd);
                    hwnd = Native.GetWindow(hwnd, Native.GW_HWNDNEXT);
                }

                var uwp = new UwpPairs(candidates);

                for (int i = 0; i < candidates.Count; i++)
                    AddIfReal(map, apps, uwp, candidates[i]);
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

        void AddIfReal(Dictionary<Guid, List<WindowEntry>> map, Dictionary<uint, string> apps,
                       UwpPairs uwp, IntPtr hwnd)
        {
            string title = Native.GetText(hwnd);
            if (title.Length == 0) return;

            // A packaged app puts two windows in the list - the frame and the CoreWindow -
            // where the taskbar shows one. Keep the frame, which is the one the shell
            // treats as the window, and read the app off its CoreWindow.
            if (uwp.IsDuplicateCore(hwnd, title)) return;

            Guid desktop;
            if (!_api.TryGetWindowDesktop(hwnd, out desktop)) return;

            string app = AppOf(apps, uwp.AppWindow(hwnd, title));

            List<WindowEntry> windows;
            if (!map.TryGetValue(desktop, out windows))
            {
                windows = new List<WindowEntry>();
                map[desktop] = windows;
            }
            windows.Add(new WindowEntry(app, TrimRepeat(title, app)));
        }

        static string AppOf(Dictionary<uint, string> apps, IntPtr hwnd)
        {
            uint pid = Native.GetOwningProcessId(hwnd);
            if (pid == 0) return string.Empty;

            string app;
            if (!apps.TryGetValue(pid, out app))
            {
                app = Native.GetAppName(pid);
                apps[pid] = app;
            }

            return app;
        }

        /// <summary>
        /// Drops the app name from the end of a title, where Win32 convention puts it, now
        /// that it leads the line instead: "Inbox - Gmail - Google Chrome" under Chrome is
        /// just "Inbox - Gmail".
        ///
        /// Only the last segment, and only when it plainly is the app - a title is the
        /// window's own words and guessing wrong costs more than a little repetition.
        /// </summary>
        static string TrimRepeat(string title, string app)
        {
            if (app.Length == 0 || title.Length == 0) return title;

            int cut = LastSeparator(title);
            if (cut < 0) return title;

            string tail = title.Substring(cut + 3);
            string head = title.Substring(0, cut);

            // A long tail is a sentence, not a signature - and an empty head would leave
            // the row saying nothing at all.
            if (tail.Length > 32 || head.Trim().Length == 0) return title;

            string a = Squash(app);
            string t = Squash(tail);
            if (a.Length == 0 || t.Length == 0) return title;

            // "Microsoft Edge" ends with "Edge".
            if (t == a || t.EndsWith(a)) return head;

            // Where neither name contains the other, the last word still lines up:
            // "Visual Studio Code" against VS Code. Short words are too easy to collide
            // with a real title word to trust.
            string aWord = Squash(LastWord(app));
            string tWord = Squash(LastWord(tail));
            if (aWord.Length >= 3 && aWord == tWord) return head;

            return title;
        }

        static string LastWord(string text)
        {
            string[] words = text.Split(' ');
            return words[words.Length - 1];
        }

        /// <summary>
        /// Where the last " - " style separator starts, or -1. The en and em dashes are
        /// written as escapes so the source stays ASCII; see the note on Controller.Bullet
        /// for why that matters to the in-box compiler. All three are three chars wide.
        /// </summary>
        static int LastSeparator(string title)
        {
            int best = -1;
            string[] seps = { " - ", " \u2013 ", " \u2014 " };

            foreach (string sep in seps)
            {
                int at = title.LastIndexOf(sep, StringComparison.Ordinal);
                if (at > best) best = at;
            }

            return best;
        }

        /// <summary>Case and punctuation removed, so "Google Chrome" and "chrome" compare.</summary>
        static string Squash(string text)
        {
            var sb = new System.Text.StringBuilder(text.Length);
            foreach (char c in text)
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        /// <summary>
        /// Packaged-app frames paired with the CoreWindows that actually run them, as seen
        /// in one sweep.
        ///
        /// A packaged app is two top-level windows: an ApplicationFrameWindow belonging to
        /// the shell's ApplicationFrameHost, and the app's own CoreWindow. Both read as
        /// ordinary app windows to the alt-tab filter, so the desktop that shows one
        /// Calculator in Task View would otherwise list two - and the frame, being the
        /// shell's, cannot name the app on its own.
        ///
        /// They cannot be matched by parentage: on current builds the CoreWindow is a
        /// sibling of the frame, not its child, which is why Native's child lookup only
        /// ever catches some of them. What the two do share is their caption, which the
        /// shell keeps in step.
        /// </summary>
        sealed class UwpPairs
        {
            const string FrameClass = "ApplicationFrameWindow";
            const string CoreClass = "Windows.UI.Core.CoreWindow";

            readonly HashSet<IntPtr> _frames = new HashSet<IntPtr>();
            readonly HashSet<IntPtr> _cores = new HashSet<IntPtr>();
            readonly HashSet<string> _framed = new HashSet<string>();
            readonly Dictionary<string, IntPtr> _coreByTitle = new Dictionary<string, IntPtr>();

            public UwpPairs(List<IntPtr> candidates)
            {
                for (int i = 0; i < candidates.Count; i++)
                {
                    IntPtr hwnd = candidates[i];
                    string cls = Native.GetClass(hwnd);
                    bool frame = cls == FrameClass;
                    if (!frame && cls != CoreClass) continue;

                    string title = Native.GetText(hwnd);
                    if (title.Length == 0) continue;

                    if (frame)
                    {
                        _frames.Add(hwnd);
                        _framed.Add(title);
                    }
                    else
                    {
                        _cores.Add(hwnd);
                        // Topmost wins, so the pairing follows the same z-order the list does.
                        if (!_coreByTitle.ContainsKey(title)) _coreByTitle[title] = hwnd;
                    }
                }
            }

            /// <summary>True for a CoreWindow whose frame is in the sweep too: the same
            /// window counted twice.</summary>
            public bool IsDuplicateCore(IntPtr hwnd, string title)
            {
                return _cores.Contains(hwnd) && _framed.Contains(title);
            }

            /// <summary>
            /// The window to read the owning app from. For a frame that is its CoreWindow,
            /// since the frame itself belongs to ApplicationFrameHost; everything else
            /// answers for itself.
            /// </summary>
            public IntPtr AppWindow(IntPtr hwnd, string title)
            {
                if (!_frames.Contains(hwnd)) return hwnd;

                IntPtr core;
                if (_coreByTitle.TryGetValue(title, out core)) return core;
                return hwnd;
            }
        }

        static int Total(Dictionary<Guid, List<WindowEntry>> map)
        {
            int n = 0;
            foreach (List<WindowEntry> windows in map.Values) n += windows.Count;
            return n;
        }
    }
}

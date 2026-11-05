using System;
using System.Collections.Generic;

namespace DesktopSwitcher
{
    /// <summary>
    /// One open window, as a tooltip wants to read it: the app that owns it, then what it
    /// is showing. Kept apart rather than pre-joined so the display format stays the
    /// caller's business.
    ///
    /// Path and Handle are for the modern panel's icon - the exe's own art first, the
    /// window's if the exe gives none. Path is "" when the process could not be opened.
    /// </summary>
    sealed class WindowEntry
    {
        public string App;
        public string Title;
        public string Path;
        public IntPtr Handle;

        public WindowEntry(string app, string title, string path, IntPtr handle)
        {
            App = app;
            Title = title;
            Path = path ?? string.Empty;
            Handle = handle;
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
    /// that asks, and this costs a COM call plus a process query per open window. The
    /// occupancy dot's cheaper question is asked on the reconcile tick, but answered from
    /// the last sweep unless a window has come, gone or moved since - see MarkChanged.
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

        // --- occupancy: swept when something changed, not on every tick ------------
        //
        // The occupancy sweep ran on every reconcile tick, every two seconds, and asked the
        // shell about each open window: one cross-process call apiece, and with the
        // reconcile beside it most of what the process did while idle. Nearly every one of
        // those sweeps found what the last had. Which desktops have windows only changes
        // when a window comes, goes or moves, and all three are announced - a WinEvent for
        // the first two, the shell's ViewVirtualDesktopChanged for the third - so the
        // controller marks the answer stale when one arrives (MarkChanged, WindowShown,
        // WindowGone) and the tick sweeps only then.
        //
        // OccupancyBackstopMs is for what is not announced: a window that was shown before
        // it had a title, which fails the alt-tab test when it is shown and passes it once
        // titled, with no show in between. Thirty seconds is how late such a window's dot
        // can be; everything announced is as prompt as the tick, as before.

        /// <summary>Longest an occupancy answer is reused when nothing has said it changed.</summary>
        public const int OccupancyBackstopMs = 30000;

        bool _occupancyStale = true;
        HashSet<Guid> _occupied;            // the last answer, reused while nothing has changed
        int _occupiedFor = -1;              // the desktop count it was computed for
        readonly System.Diagnostics.Stopwatch _occupiedAge = new System.Diagnostics.Stopwatch();

        // Every alt-tab window the last sweep saw. A hide or destroy can only change the
        // answer if it was one of these, and a destroyed window cannot be asked anything
        // else - by the time the event arrives it is gone.
        HashSet<IntPtr> _known = new HashSet<IntPtr>();

        public WindowInventory(VirtualDesktopApi api)
        {
            _api = api;
        }

        /// <summary>Forces the next query to re-sweep. Called when the desktop set changes.</summary>
        public void Invalidate()
        {
            _age.Reset();
            _occupancyStale = true;
        }

        /// <summary>
        /// Something has said which desktops have windows may be different now: a window
        /// moved between desktops, or a close-all finished. The next OccupiedDesktops sweeps.
        /// </summary>
        public void MarkChanged()
        {
            _occupancyStale = true;
        }

        /// <summary>
        /// A window was shown somewhere. Stale only if it is one the sweep would count; most
        /// shows are tooltips, menus and other tool windows, and those change nothing.
        /// </summary>
        public void WindowShown(IntPtr hwnd)
        {
            if (!_occupancyStale && Native.IsAltTabWindow(hwnd)) _occupancyStale = true;
        }

        /// <summary>
        /// A window was hidden or destroyed. Stale only if the last sweep counted it - which
        /// also covers an app hiding its window to the tray, since that window was counted.
        /// </summary>
        public void WindowGone(IntPtr hwnd)
        {
            if (!_occupancyStale && _known.Contains(hwnd)) _occupancyStale = true;
        }

        /// <summary>
        /// Whether OccupiedDesktops would sweep rather than hand back its last answer. For the
        /// tick to skip the call - and the strip's repaint - when there is nothing new.
        /// </summary>
        public bool OccupancyStale(int desktopCount)
        {
            return _occupancyStale || _occupied == null || desktopCount != _occupiedFor
                || !_occupiedAge.IsRunning || _occupiedAge.ElapsedMilliseconds >= OccupancyBackstopMs;
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
            string path = Native.GetProcessPath(Native.GetOwningProcessId(hwnd));
            string app = Native.AppNameFromPath(path);
            return new WindowEntry(app, TrimRepeat(title, app), path, hwnd);
        }

        /// <summary>
        /// Which desktops have at least one window, without the cost of describing what:
        /// no process query, no UWP frame/core pairing, no title work - a boolean needs
        /// none of it. What the strip's occupancy dot asks for, on the reconcile tick,
        /// where the full sweep - priced per hover in EnsureFresh's own doc - could not
        /// run every couple of seconds without cost.
        ///
        /// Reuses a still-fresh full sweep's keys rather than re-enumerating: they are
        /// already the answer, and a hover moments before a tick would otherwise pay for
        /// the same windows twice.
        ///
        /// Stops early once every desktop passed is accounted for, so a machine with one
        /// window per desktop pays for one GetWindowDesktopId, not the whole window list.
        ///
        /// Null when the shell is unavailable, mid-restart - the caller's cue to hold
        /// whatever dots are already on screen rather than blank them, the same contract
        /// Refresh() keeps for the full sweep.
        /// </summary>
        public HashSet<Guid> OccupiedDesktops(int desktopCount)
        {
            // Nothing has said the answer moved since it was worked out, so it has not. A
            // copy, because the caller keeps it and this one may be handed out again.
            if (!OccupancyStale(desktopCount))
                return new HashSet<Guid>(_occupied);

            // Cleared before the sweep rather than after it: an event arriving mid-sweep was
            // not necessarily seen by it, and must leave the answer stale for the next tick.
            _occupancyStale = false;

            if (_age.IsRunning && _age.ElapsedMilliseconds < CacheMs)
                return Remember(new HashSet<Guid>(_byDesktop.Keys), desktopCount);

            var occupied = new HashSet<Guid>();
            try
            {
                List<IntPtr> candidates = EnumerateAltTabWindows();
                _known = new HashSet<IntPtr>(candidates);
                for (int i = 0; i < candidates.Count && occupied.Count < desktopCount; i++)
                {
                    Guid desktop;
                    if (_api.TryGetWindowDesktop(candidates[i], out desktop))
                        occupied.Add(desktop);
                }
            }
            catch (ShellUnavailableException)
            {
                Log.Write("inventory: shell unavailable, occupancy sweep skipped");
                _occupancyStale = true;
                return null;
            }
            catch (Exception ex)
            {
                Log.Write("inventory: occupancy sweep failed - " + ex.Message);
                _occupancyStale = true;
                return null;
            }

            return Remember(occupied, desktopCount);
        }

        HashSet<Guid> Remember(HashSet<Guid> occupied, int desktopCount)
        {
            // Only a change is worth a line: it says when a dot came or went, which is the
            // one thing about the occupancy sweep nobody can otherwise see after the fact.
            if (Log.Enabled && (_occupied == null || !_occupied.SetEquals(occupied)))
                Log.Write("inventory: " + occupied.Count + " of " + desktopCount + " desktops have windows");

            _occupied = occupied;
            _occupiedFor = desktopCount;
            _occupiedAge.Restart();
            return new HashSet<Guid>(occupied);
        }

        void EnsureFresh()
        {
            if (_age.IsRunning && _age.ElapsedMilliseconds < CacheMs) return;
            Refresh();
        }

        /// <summary>
        /// GetTopWindow/GW_HWNDNEXT rather than EnumWindows: same set, but in z-order, so
        /// each desktop's list reads most-recently-used first.
        /// </summary>
        static List<IntPtr> EnumerateAltTabWindows()
        {
            var candidates = new List<IntPtr>();
            IntPtr hwnd = Native.GetTopWindow(IntPtr.Zero);
            while (hwnd != IntPtr.Zero)
            {
                if (Native.IsAltTabWindow(hwnd)) candidates.Add(hwnd);
                hwnd = Native.GetWindow(hwnd, Native.GW_HWNDNEXT);
            }
            return candidates;
        }

        void Refresh()
        {
            var map = new Dictionary<Guid, List<WindowEntry>>();

            // Ten Chrome windows are one process, so resolve each pid once. Local to the
            // sweep on purpose: a pid the kernel has since recycled can never hand back a
            // dead process's name. Holds each pid's exe path; the app name is read off it.
            var paths = new Dictionary<uint, string>();

            try
            {
                List<IntPtr> candidates = EnumerateAltTabWindows();
                _known = new HashSet<IntPtr>(candidates);
                var uwp = new UwpPairs(candidates);

                for (int i = 0; i < candidates.Count; i++)
                    AddIfReal(map, paths, uwp, candidates[i]);
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

        void AddIfReal(Dictionary<Guid, List<WindowEntry>> map, Dictionary<uint, string> paths,
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

            string path = PathOf(paths, uwp.AppWindow(hwnd, title));
            string app = Native.AppNameFromPath(path);

            List<WindowEntry> windows;
            if (!map.TryGetValue(desktop, out windows))
            {
                windows = new List<WindowEntry>();
                map[desktop] = windows;
            }
            windows.Add(new WindowEntry(app, TrimRepeat(title, app), path, hwnd));
        }

        static string PathOf(Dictionary<uint, string> paths, IntPtr hwnd)
        {
            uint pid = Native.GetOwningProcessId(hwnd);
            if (pid == 0) return string.Empty;

            string path;
            if (!paths.TryGetValue(pid, out path))
            {
                path = Native.GetProcessPath(pid);
                paths[pid] = path;
            }

            return path;
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
        /// shell keeps in step - for apps that set a title at all.
        ///
        /// An app that does set one breaks the exact match: Notepads' frame reads
        /// "Untitled.txt - Notepads" while its CoreWindow still reads "Notepads", and both
        /// were listed, the frame as "Windows app". So a frame whose caption ends in a
        /// CoreWindow's, after a separator, pairs with it too. That mattered beyond the
        /// double row: close-all posted WM_CLOSE to the CoreWindow as well as the frame,
        /// and the app vanished with its save prompt unanswered.
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
                if (!_cores.Contains(hwnd)) return false;
                if (_framed.Contains(title)) return true;

                foreach (string frame in _framed)
                    if (EndsWithTitle(frame, title)) return true;
                return false;
            }

            /// <summary>
            /// True when <paramref name="frame"/> is <paramref name="core"/> with a document
            /// name put in front: "Untitled.txt - Notepads" over "Notepads". The character
            /// before the match must be a separator - a space, a dash, or the invisible
            /// left-to-right mark (U+200E) some apps put before theirs - so "Mail" does not
            /// pair with "Voicemail".
            /// </summary>
            static bool EndsWithTitle(string frame, string core)
            {
                if (core.Length == 0 || frame.Length <= core.Length) return false;
                if (!frame.EndsWith(core, StringComparison.Ordinal)) return false;

                char before = frame[frame.Length - core.Length - 1];
                return before == ' ' || before == '-' || before == '\u2013' || before == '\u2014'
                    || before == '\u200E';
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

                // The longest CoreWindow title the frame ends with, so "Notepads" is not
                // beaten by some shorter, unrelated "pads".
                IntPtr best = IntPtr.Zero;
                int bestLength = 0;
                foreach (KeyValuePair<string, IntPtr> pair in _coreByTitle)
                {
                    if (pair.Key.Length > bestLength && EndsWithTitle(title, pair.Key))
                    {
                        best = pair.Value;
                        bestLength = pair.Key.Length;
                    }
                }
                return best != IntPtr.Zero ? best : hwnd;
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

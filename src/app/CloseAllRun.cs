using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace DesktopSwitcher
{
    /// <summary>
    /// One "Close all and remove" or "Close all windows", from the confirmed click to the
    /// desktop going away - or, for the second, to it standing empty.
    ///
    /// Asks every window to close at once, then watches on a short tick and hands each
    /// one's state to CloseAllWindow as it changes. There is no fixed wait anywhere: the
    /// desktop is removed on the tick the last window goes, and the window's buttons are
    /// live from the moment the run takes it over.
    ///
    /// Whatever is holding things up is dealt with on its own row:
    ///
    ///   Show       brings that window up and gives the keyboard to its question - after
    ///              switching to its desktop first, when it is on another one. A question
    ///              drawn inside a minimized window (Notepads' "Save?") is otherwise asked
    ///              where nobody can see it.
    ///   Force      ends that window's app, without saving. Asks for a second click when
    ///              the app has windows the user did not ask to close - see ForceRow.
    ///   Ask again  for a window whose prompt was answered Cancel: asks it to close once
    ///              more. Before, nothing ever asked again and the run sat there for good.
    ///
    /// and for all of them at once, Force all and Cancel. Cancel stops watching: what
    /// closed stays closed, nothing is removed.
    ///
    /// There used to be one Go there / Show button for the whole run, aimed at whichever
    /// window it judged most pressing, and one Force close for everything. A desktop with
    /// one stuck window in five had no way to deal with the one without the others.
    ///
    /// Never removes past an open window. Removing would move a window with an unanswered
    /// save prompt onto a neighbour, which is the opposite of the clean desktop asked for.
    ///
    /// A run that does not remove (Remove false) is the same run with the last step left
    /// off. It exists for the last desktop, which Windows will not remove, and so had no
    /// way to close everything on it at all; and for clearing a desktop while keeping its
    /// name, mark and place in the row. It is also the only honest outcome when the other
    /// desktops are removed mid-run and this one becomes the last - Finish checks the
    /// count again rather than trusting the flag alone.
    ///
    /// There used to be a Bring here, which moved the open windows to the user instead.
    /// Moving a window mid-close is not safe: Brave and Terminal moved, Notepads vanished
    /// with its save prompt unanswered. Going to the windows touches none of them.
    ///
    /// Lives in app/ because it needs the model, the closer and a window at once - the
    /// same reason the menu that starts it lives in Controller.
    /// </summary>
    sealed class CloseAllRun : IDisposable
    {
        /// <summary>
        /// How often the windows are looked at. Fast enough that a close reads as
        /// immediate; each look is a few user32 calls per window, nothing that reaches
        /// the shell.
        /// </summary>
        const int TickMs = 150;

        /// <summary>
        /// How long a forced window has to disappear before it is shown as refused.
        /// TerminateProcess is asynchronous, but not by much.
        /// </summary>
        const int ForceGraceMs = 1500;

        /// <summary>
        /// How many times the desktop is looked at again once the list empties, for windows
        /// opened during the run. Bounded, so an app that answers every close by opening a
        /// new window cannot keep the run going forever.
        /// </summary>
        const int MaxSweeps = 3;

        /// <summary>
        /// How long a window may sit at Closing before it reads as StillOpen. An ordinary
        /// app is gone well inside this; past it, a question drawn inside the window is the
        /// likelier story.
        /// </summary>
        const int StillOpenMs = 1000;

        /// <summary>
        /// How long after a Show's switch before the window is brought up. The switch's
        /// focus nudge fires 200 ms after it (VirtualDesktopApi.NudgeDelayMs) and Explorer's
        /// own activation lands a few ms after the switch; focusing before either would be
        /// undone by them. Two ticks clears both.
        /// </summary>
        const int ShowAfterSwitchMs = 280;

        sealed class Item
        {
            public IntPtr Handle;
            public WindowEntry Entry;
            public string Text;
            public CloseState State = CloseState.Closing;
            public bool Prompted;   // has shown a prompt at some point
            public bool Hung;
            public bool CanForce;
            public bool WasForced;
            public int StuckSeq;

            /// <summary>The prompt the question was read from, and what it said.</summary>
            public IntPtr QuestionPopup;
            public string Question;

            /// <summary>Ticks the current prompt has been seen for; reads wait for two.</summary>
            public int PromptTicks;

            /// <summary>A read that only found the caption may be tried once more, at this time.</summary>
            public int RetryAt;
            public bool Retried;

            /// <summary>When the prompt went away with the window still open, or null.</summary>
            public System.Diagnostics.Stopwatch Unprompted;

            /// <summary>
            /// When this window was asked, and when it was forced, or null. Per window, not
            /// per run: a window SweepForNew picks up three seconds in has only just been
            /// asked, and timed from the run's start it went straight to "still open" - or,
            /// after a Force it was never part of, straight to "can't close".
            /// </summary>
            public System.Diagnostics.Stopwatch Asked;
            public System.Diagnostics.Stopwatch Forced;
        }

        /// <summary>
        /// How long a window must stay open after its prompt goes before it reads as kept
        /// open. Answering Save or Close drops the dialog a moment before the window goes -
        /// 150 ms for Brave in one run - and without this the row flashed "kept open" in
        /// between, which is exactly the wrong thing to have just answered.
        /// </summary>
        const int KeptOpenMs = 500;

        readonly DesktopService _service;
        readonly WindowCloser _closer;
        readonly Guid _id;
        readonly string _name;
        readonly bool _remove;
        readonly Func<WindowEntry, string> _describe;
        readonly List<Item> _items = new List<Item>();
        readonly HashSet<IntPtr> _known = new HashSet<IntPtr>();

        CloseAllWindow _panel;
        Timer _timer;
        bool _forced;   // a Force has been sent - for the hint line only
        int _sweeps;
        int _stuckSeq;
        bool _ended;

        IntPtr _pendingShow;
        int _pendingShowAt;

        long _slowestTickTicks;
        System.Diagnostics.Stopwatch _clock;

        /// <summary>Raised once, however the run ends.</summary>
        public event EventHandler Ended;

        public Guid DesktopId { get { return _id; } }

        /// <summary>Whether the desktop is to be removed once it empties.</summary>
        public bool Remove { get { return _remove; } }

        public CloseAllRun(DesktopService service, WindowCloser closer, Desktop desktop,
                           IList<WindowEntry> windows, bool remove, Func<WindowEntry, string> describe)
        {
            _service = service;
            _closer = closer;
            _id = desktop.Id;
            _name = desktop.DisplayName;
            _remove = remove;
            _describe = describe;

            for (int i = 0; i < windows.Count; i++) Track(windows[i]);
        }

        void Track(WindowEntry window)
        {
            if (!_known.Add(window.Handle)) return;

            var item = new Item();
            item.Handle = window.Handle;
            item.Entry = window;
            item.Text = _describe(window);

            // Once, here, rather than every tick: it reads the process's path, and the
            // answer for a given window does not change while it lives.
            item.CanForce = WindowCloser.CanForce(window.Handle);
            _items.Add(item);
        }

        /// <summary>
        /// Opens a window of its own already running, by the strip. For --closeall, which
        /// skips the confirmation; the app always hands over the window it confirmed in.
        /// </summary>
        public void Start(IWin32Window owner, Color background, double dpiScale, Rectangle anchor)
        {
            var entries = new List<WindowEntry>();
            for (int i = 0; i < _items.Count; i++) entries.Add(_items[i].Entry);

            var window = new CloseAllWindow("Close all", "", entries, null, _remove, background,
                                            Color.FromArgb(0x3A, 0x96, 0xDD), dpiScale, anchor, true);
            window.Show(owner);
            Start(window);
        }

        /// <summary>Takes over the confirmed window, then asks everything to close.</summary>
        public void Start(CloseAllWindow window)
        {
            Log.Write(delegate
            {
                return "close-all: closing " + _items.Count + " windows on " + _name
                       + (_remove ? "" : ", keeping the desktop");
            });
            Perf.Log("close-all start");
            _clock = System.Diagnostics.Stopwatch.StartNew();

            _panel = window;
            _panel.RowPressed += OnRowPressed;
            _panel.ForceAllPressed += delegate(bool armed) { if (!_ended) ForceAll(armed); };
            _panel.StopRequested += delegate { if (!_ended) Cancel(); };
            _panel.BeginRunning(_name, _known);

            // The window foreground first, and only then the requests. When the apps start
            // answering, the window that has focus is this one - not whatever Windows picks
            // once the confirmation stops being looked at, which can be a window on another
            // desktop, and activating that switches the user over to it.
            //
            // Only when there is another desktop to be pulled to. Closing the one being
            // looked at, the prompts are already here, and the lock would only stop them
            // taking focus - leaving them unanswerable from the keyboard until the user
            // happened to click.
            Desktop current = _service.Current;
            if (current == null || current.Id != _id) LockForeground();

            AskAll();
            for (int i = 0; i < _items.Count; i++) LogItem(_items[i], "start");
            Refresh();

            _timer = new Timer();
            _timer.Interval = TickMs;
            _timer.Tick += delegate { Tick(); };
            _timer.Start();
        }

        bool _locked;

        /// <summary>
        /// Keeps the apps being closed from pulling the user to their desktop. Windows
        /// follows the foreground across desktops, and an app that answers WM_CLOSE by
        /// grabbing focus for its question - Terminal's "close all tabs?" does - takes the
        /// user with it: the log of one run shows Terminal's hwnd become foreground and the
        /// desktop switch 4 ms later, with nobody having pressed anything.
        ///
        /// LockSetForegroundWindow refuses those grabs, and only works for the process
        /// that holds the foreground, which is why the window is made foreground first.
        /// Lifted by a Show that switches, by the end of the run, and by Windows itself the
        /// moment the user clicks elsewhere or presses Alt - it never outlives their intent.
        /// </summary>
        void LockForeground()
        {
            if (_panel == null) return;
            Native.ForceForeground(_panel.Handle);
            _locked = Native.LockSetForegroundWindow(Native.LSFW_LOCK);
            Log.Write("close-all: foreground " + (_locked ? "locked" : "lock refused, error "
                      + System.Runtime.InteropServices.Marshal.GetLastWin32Error()));
        }

        void UnlockForeground()
        {
            if (!_locked) return;
            _locked = false;
            Native.LockSetForegroundWindow(Native.LSFW_UNLOCK);
        }

        /// <summary>Asked for a second run while this one is going: show this one.</summary>
        public void Activate()
        {
            if (_panel != null && !_panel.IsDisposed) _panel.Activate();
        }

        void AskAll()
        {
            for (int i = 0; i < _items.Count; i++)
            {
                Item item = _items[i];
                if (item.State != CloseState.Closing || item.Asked != null) continue;
                item.Asked = System.Diagnostics.Stopwatch.StartNew();
                if (!WindowCloser.Ask(item.Handle)) item.State = CloseState.Refused;
            }
        }

        void Tick()
        {
            if (_ended) return;
            var clock = System.Diagnostics.Stopwatch.StartNew();

            // Removed some other way while this was running - Task View, the strip's own
            // Remove, a middle click. Nothing left to do it for.
            if (!Exists())
            {
                Log.Write("close-all: desktop went away mid-run");
                End();
                return;
            }

            for (int i = 0; i < _items.Count; i++)
            {
                Item item = _items[i];
                if (item.State == CloseState.Closed) continue;
                CloseState was = item.State;

                if (WindowCloser.IsGone(item.Handle))
                {
                    item.State = CloseState.Closed;
                }
                else if (WindowCloser.IsPrompting(item.Handle))
                {
                    item.State = CloseState.Prompting;
                    item.Prompted = true;
                    item.Unprompted = null;
                }
                else if (item.State == CloseState.Refused
                         || (item.Forced != null && item.Forced.ElapsedMilliseconds > ForceGraceMs))
                {
                    item.State = CloseState.Refused;
                }
                else if (item.Prompted)
                {
                    // Enabled and open again after a prompt, and staying so: answered with
                    // Cancel. Until then it keeps whatever it showed.
                    if (item.Unprompted == null) item.Unprompted = System.Diagnostics.Stopwatch.StartNew();
                    else if (item.Unprompted.ElapsedMilliseconds >= KeptOpenMs) item.State = CloseState.KeptOpen;
                }
                else if (item.Asked != null && item.Asked.ElapsedMilliseconds > StillOpenMs)
                {
                    item.State = CloseState.StillOpen;
                }

                // Hung is read off a timestamp Windows keeps, so it is free to ask, and it
                // changes what the row offers: a frozen window shown is a white rectangle.
                item.Hung = item.State != CloseState.Closed && Native.IsHungAppWindow(item.Handle);

                if (item.State == CloseState.Prompting)
                {
                    item.PromptTicks++;
                    ReadQuestion(item);
                }
                else
                {
                    item.PromptTicks = 0;
                    item.Retried = false;
                    item.RetryAt = 0;
                    if (item.QuestionPopup != IntPtr.Zero)
                    {
                        item.QuestionPopup = IntPtr.Zero;
                        item.Question = null;
                    }
                }

                if (item.StuckSeq == 0 && IsStuck(item)) item.StuckSeq = ++_stuckSeq;

                if (item.State != was) LogItem(item, was.ToString());
            }

            // A Show that switched desktops, waiting out the switch's own focus handling.
            if (_pendingShow != IntPtr.Zero && ShowsHere
                && unchecked(Environment.TickCount - _pendingShowAt) >= ShowAfterSwitchMs)
            {
                IntPtr target = _pendingShow;
                _pendingShow = IntPtr.Zero;
                ShowNow(target);
            }

            if (IsEmpty())
            {
                // Before removing, one more look at the desktop itself: something may have
                // opened there since the list was taken, and removing would carry it off to
                // a neighbour unasked.
                //
                // Only before removing. A run that keeps the desktop carries nothing off, so
                // a window opened during it is safe where it is - and on the last desktop,
                // which is always the one being looked at, it is most likely one the user
                // just opened on purpose, which a sweep would close under them. What was
                // asked to close is the list that was confirmed.
                if (_remove && _sweeps < MaxSweeps && SweepForNew())
                {
                    Refresh();
                    return;
                }

                Finish();
                return;
            }

            Refresh();
            if (clock.ElapsedTicks > _slowestTickTicks) _slowestTickTicks = clock.ElapsedTicks;
        }

        static bool IsStuck(Item item)
        {
            if (item.State == CloseState.Closed) return false;
            return item.Hung || item.State == CloseState.Prompting || item.State == CloseState.StillOpen
                || item.State == CloseState.KeptOpen || item.State == CloseState.Refused;
        }

        /// <summary>
        /// Starts reading what a newly seen prompt asks. Once per prompt: the same popup is
        /// not read twice, and a different one - the app asking a second question after the
        /// first - is read afresh. The answer arrives on a pool thread and is marshalled back
        /// here through the window; by then the prompt may be gone or replaced, and the
        /// answer is dropped unless it is still about the prompt on screen.
        /// </summary>
        void ReadQuestion(Item item)
        {
            IntPtr popup = Native.GetLastActivePopup(item.Handle);
            if (popup == IntPtr.Zero || popup == item.Handle) return;

            // Not the instant the owner goes disabled. ShowDialog disables the owner before
            // the dialog has its contents, and a read then - --closeall's first runs - found
            // an empty tree and fell back to the caption. Two ticks, and visible.
            if (item.PromptTicks < 2 || !Native.IsWindowVisible(popup)) return;

            bool retry = popup == item.QuestionPopup && item.RetryAt != 0
                         && unchecked(Environment.TickCount - item.RetryAt) >= 0;
            if (popup == item.QuestionPopup && !retry) return;

            if (retry)
            {
                item.RetryAt = 0;
                item.Retried = true;
            }
            else
            {
                item.QuestionPopup = popup;
                item.Question = null;
                item.Retried = false;
            }

            CloseAllWindow panel = _panel;
            string app = item.Entry != null ? item.Entry.App : string.Empty;
            DialogText.Read(popup, app, delegate(string text, bool inside)
            {
                try
                {
                    if (panel == null || panel.IsDisposed || !panel.IsHandleCreated) return;
                    panel.BeginInvoke((MethodInvoker)delegate
                    {
                        if (_ended || item.QuestionPopup != popup) return;
                        if (text != null || item.Question == null) item.Question = text;

                        // Only the caption, or nothing: once more, a little later, in case the
                        // dialog was still filling in. Once - a dialog with no readable text
                        // will not grow one.
                        if (!inside && !item.Retried) item.RetryAt = Environment.TickCount + 600;
                        Refresh();
                    });
                }
                catch (InvalidOperationException)
                {
                    // The window closed between the check and the BeginInvoke. Nothing to tell.
                }
            });
        }

        bool IsEmpty()
        {
            for (int i = 0; i < _items.Count; i++)
                if (_items[i].State != CloseState.Closed) return false;
            return true;
        }

        /// <summary>
        /// One window's state change, with the raw readings it was decided from - what a
        /// "why did it think that" question about a run needs, and nothing a run does
        /// without diagnostics on pays for.
        /// </summary>
        void LogItem(Item item, string from)
        {
            if (!Log.Enabled) return;
            IntPtr h = item.Handle;
            Log.Write("close-all: \"" + item.Text + "\" " + from + " -> " + item.State
                      + (item.Hung ? " (hung)" : "")
                      + " (hwnd " + h.ToString("X") + ", class " + Native.GetClass(h)
                      + ", exists " + Native.IsWindow(h) + ", visible " + Native.IsWindowVisible(h)
                      + ", enabled " + Native.IsWindowEnabled(h)
                      + ", popup " + Native.GetLastActivePopup(h).ToString("X")
                      + ", foreground " + Native.GetForegroundWindow().ToString("X") + ")");
        }

        bool SweepForNew()
        {
            _sweeps++;
            _closer.Invalidate();

            List<WindowEntry> now = _closer.Closeable(_id);
            int before = _items.Count;
            for (int i = 0; i < now.Count; i++) Track(now[i]);
            if (_items.Count == before) return false;

            Log.Write(delegate { return "close-all: " + (_items.Count - before) + " new windows, asking those too"; });
            AskAll();
            return true;
        }

        /// <summary>The window's Cancel: stop watching, remove nothing.</summary>
        public void Cancel()
        {
            Log.Write("close-all: cancelled");
            End();
        }

        // --- read by --closeall, which drives runs against scripted windows ---------

        /// <summary>Each tracked window's state, in list order.</summary>
        public CloseState[] States
        {
            get
            {
                var states = new CloseState[_items.Count];
                for (int i = 0; i < _items.Count; i++) states[i] = _items[i].State;
                return states;
            }
        }

        /// <summary>The window each entry of States is about.</summary>
        public IntPtr[] Handles
        {
            get
            {
                var handles = new IntPtr[_items.Count];
                for (int i = 0; i < _items.Count; i++) handles[i] = _items[i].Handle;
                return handles;
            }
        }

        /// <summary>What the row for this window quotes its prompt as, or null.</summary>
        public string QuestionOf(IntPtr hwnd)
        {
            Item item = Find(hwnd);
            return item == null ? null : item.Question;
        }

        /// <summary>Whether this window reads as not responding.</summary>
        public bool HungOf(IntPtr hwnd)
        {
            Item item = Find(hwnd);
            return item != null && item.Hung;
        }

        /// <summary>The label the last Force that needed a second click armed with, or null.</summary>
        public string LastArmed { get; private set; }

        /// <summary>The handles in the order the window is showing them: stuck first.</summary>
        public IntPtr[] DisplayOrder
        {
            get
            {
                var order = new List<Item>(_items);
                order.Sort(delegate(Item a, Item b)
                {
                    int ra = Rank(a), rb = Rank(b);
                    if (ra != rb) return ra - rb;
                    if (ra == 0 && a.StuckSeq != b.StuckSeq) return a.StuckSeq - b.StuckSeq;
                    return _items.IndexOf(a) - _items.IndexOf(b);
                });
                var handles = new IntPtr[order.Count];
                for (int i = 0; i < order.Count; i++) handles[i] = order[i].Handle;
                return handles;
            }
        }

        /// <summary>CloseList.Rank, for the rows the run knows about.</summary>
        static int Rank(Item item)
        {
            if (item.State == CloseState.Closed) return 3;
            return IsStuck(item) ? 0 : 1;
        }

        public bool HasEnded { get { return _ended; } }

        /// <summary>Read by --closeall: the window a run that finished left showing its outcome.</summary>
        public CloseAllWindow Window { get; private set; }

        /// <summary>True once the run has removed the desktop itself.</summary>
        public bool RemovedDesktop { get; private set; }

        Item Find(IntPtr hwnd)
        {
            for (int i = 0; i < _items.Count; i++)
                if (_items[i].Handle == hwnd) return _items[i];
            return null;
        }

        void Finish()
        {
            _closer.Invalidate();

            // The count is checked again, not taken from the flag: the other desktops may
            // have been removed while this ran, and Windows will not remove the last one.
            // Before the flag existed this logged "removing" and set RemovedDesktop either
            // way, so --closeall and the log both claimed a removal that never happened.
            if (_remove && _service.Count > 1)
            {
                Log.Write("close-all: " + _name + " is empty, removing");
                RemovedDesktop = true;
                _service.Remove(_id);
            }
            else
            {
                Log.Write("close-all: " + _name + " is empty, keeping it"
                          + (_remove ? " - it is the last desktop now" : ""));
            }

            // The rows' last states first. The window takes no more updates once it is done,
            // and windows that all close inside one tick - five at once, in a real run - were
            // never told: the done window sat over five rows still spinning.
            Refresh();

            // The window stays a moment to say how it went, then goes by itself.
            int forced = 0;
            for (int i = 0; i < _items.Count; i++) if (_items[i].WasForced) forced++;
            string line = _items.Count + " closed" + (forced > 0 ? ", " + forced + " forced" : "");
            string title = "\"" + _name + "\"" + (RemovedDesktop ? " removed" : " is empty");
            if (_panel != null && !_panel.IsDisposed) _panel.ShowDone(title, line);
            Window = _panel;

            End(false);
        }

        // --- Show ----------------------------------------------------------------

        /// <summary>
        /// The window Show() with no target brings up: the one most worth answering - a real
        /// dialog, then a window that may be asking inside itself, then one kept open, then
        /// anything not yet closed. Zero when everything is closed.
        /// </summary>
        IntPtr ShowTarget()
        {
            CloseState[] order = { CloseState.Prompting, CloseState.StillOpen, CloseState.KeptOpen,
                                   CloseState.Closing, CloseState.Refused };
            for (int o = 0; o < order.Length; o++)
                for (int i = 0; i < _items.Count; i++)
                    if (_items[i].State == order[o] && Native.IsWindow(_items[i].Handle))
                        return _items[i].Handle;
            return IntPtr.Zero;
        }

        /// <summary>Show for whichever window is most worth answering. Read by --closeall.</summary>
        public void Show()
        {
            IntPtr target = ShowTarget();
            if (target != IntPtr.Zero) Show(target);
        }

        /// <summary>
        /// A row's Show. One click, wherever the window is - it used to take Go there, then
        /// Show.
        ///
        /// On another desktop, by activating the window itself: Windows follows the
        /// foreground to the desktop it is on, which is the behaviour the foreground lock
        /// exists to stop the apps using, used here on purpose. The click on the row is what
        /// gives this process the right to do it. Switching first and focusing after did not
        /// hold up: the switch hands activation to whatever Explorer picks on the new desktop,
        /// this process loses the foreground with it, and a SetForegroundWindow a few hundred
        /// ms later was refused - seen in --closeall, the dialog left without the keyboard.
        /// That order is kept only as the fallback, for when the activation is refused.
        ///
        /// Only on the button, never by itself. Popping a window up the moment it reads as
        /// still open would take the keyboard from whatever the user is typing in, for a
        /// window that is quite possibly just slow.
        /// </summary>
        public void Show(IntPtr target)
        {
            if (target == IntPtr.Zero || !Native.IsWindow(target)) return;

            if (!ShowsHere)
            {
                // The lock is this run's own, and would refuse its own activation's switch.
                UnlockForeground();
                if (ShowNow(target))
                {
                    Log.Write("close-all: shown across desktops by activation");
                    return;
                }
                _pendingShow = target;
                _pendingShowAt = Environment.TickCount;
                Go();
                return;
            }
            ShowNow(target);
        }

        /// <summary>
        /// Restores the window if it is minimized, and gives it the keyboard - or, when it
        /// is disabled behind a dialog of its own, gives the dialog the keyboard.
        ///
        /// The click on the window makes this process the foreground one, which is what
        /// lets the SetForegroundWindow through without the input-attach fallback.
        /// </summary>
        bool ShowNow(IntPtr target)
        {
            if (!Native.IsWindow(target)) return false;

            bool restored = false;
            if (Native.IsIconic(target))
                restored = Native.ShowWindow(target, Native.SW_RESTORE);

            IntPtr input = target;
            if (!Native.IsWindowEnabled(target))
            {
                IntPtr popup = Native.GetLastActivePopup(target);
                if (popup != IntPtr.Zero && popup != target && Native.IsWindowVisible(popup))
                    input = popup;
            }

            bool focused = Native.ForceForeground(input);
            LastShown = target;
            LastFocused = input;
            LastFocusGranted = focused;
            Log.Write(delegate
            {
                return "close-all: showing 0x" + target.ToString("X")
                    + (input != target ? " via its dialog 0x" + input.ToString("X") : "")
                    + (restored ? ", restored" : "") + (focused ? "" : ", focus refused");
            });
            return focused;
        }

        /// <summary>
        /// Read by --closeall: whether Windows let the last Show have the foreground. A
        /// harness started from a console beside an editor often holds no right to it, and
        /// then what can be checked is the choice, not the outcome.
        /// </summary>
        public bool LastFocusGranted { get; private set; }

        /// <summary>Read by --closeall: the window the last Show acted on.</summary>
        public IntPtr LastShown { get; private set; }

        /// <summary>
        /// Read by --closeall: the window the last Show gave the keyboard to - the dialog
        /// rather than its owner, when there was one. Whether Windows honoured that is a
        /// separate question; this is the choice, which can be checked every time.
        /// </summary>
        public IntPtr LastFocused { get; private set; }

        /// <summary>Whether the run's desktop is the one being looked at.</summary>
        public bool ShowsHere
        {
            get
            {
                Desktop current = _service.Current;
                return current != null && current.Id == _id;
            }
        }

        /// <summary>
        /// Switches to the run's desktop. The window is on every desktop, so it is still
        /// there on arrival and keeps ticking as the questions are answered; the desktop is
        /// removed under the user once the last window goes, and Windows lands them on its
        /// neighbour.
        /// </summary>
        public void Go()
        {
            Log.Write("close-all: going to " + _name);

            // The questions over there need to be able to take focus once the user is
            // looking at them.
            UnlockForeground();
            _service.SwitchTo(_id);
        }

        // --- Force -----------------------------------------------------------------

        void OnRowPressed(IntPtr handle, RowButton button, bool armed)
        {
            if (_ended) return;
            switch (button)
            {
                case RowButton.Show: Show(handle); break;
                case RowButton.Force: ForceRow(handle, armed); break;
                case RowButton.AskAgain: AskAgain(handle); break;
            }
        }

        /// <summary>
        /// A row's Force. There is no forcing one window - TerminateProcess ends the app, and
        /// every window it has goes with it, on every desktop. So when the app has windows
        /// beyond the ones this run is closing anyway - a browser's other windows, or a
        /// window of the same app the user unticked to keep - the first click only arms the
        /// button with what it will also end, and the second does it. With nothing else to
        /// lose, one click: asking twice for no reason teaches the second click to be
        /// automatic, which is the one thing it must not be.
        /// </summary>
        public void ForceRow(IntPtr handle, bool confirmed)
        {
            Item item = Find(handle);
            if (item == null || item.State == CloseState.Closed) return;

            if (!confirmed)
            {
                string app;
                int others = _closer.OthersOfProcess(handle, OpenHandles(), out app);
                if (others > 0)
                {
                    LastArmed = "End " + (app.Length > 0 ? app : "the app") + " + " + others
                                + (others == 1 ? " other?" : " others?");
                    Log.Write("close-all: force of \"" + item.Text + "\" would end " + others + " more windows, asking");
                    if (_panel != null) _panel.ArmRow(handle, LastArmed);
                    return;
                }
            }
            ForceItems(new List<Item> { item });
        }

        /// <summary>
        /// Force all, under ForceRow's rule: armed first when any of the apps it would end
        /// has windows outside the run.
        /// </summary>
        public void ForceAll(bool confirmed)
        {
            var open = new List<Item>();
            for (int i = 0; i < _items.Count; i++)
                if (_items[i].State != CloseState.Closed) open.Add(_items[i]);
            if (open.Count == 0) return;

            if (!confirmed)
            {
                HashSet<IntPtr> exclude = OpenHandles();
                var pids = new HashSet<uint>();
                int others = 0;
                foreach (Item item in open)
                {
                    uint pid = Native.GetOwningProcessId(item.Handle);
                    if (pid == 0 || !pids.Add(pid)) continue;
                    string app;
                    others += _closer.OthersOfProcess(item.Handle, exclude, out app);
                }
                if (others > 0)
                {
                    LastArmed = "Force all + " + others + (others == 1 ? " other?" : " others?");
                    Log.Write("close-all: force all would end " + others + " more windows, asking");
                    if (_panel != null) _panel.ArmForceAll(LastArmed);
                    return;
                }
            }
            ForceItems(open);
        }

        /// <summary>Force all, without the second click. What --closeall calls.</summary>
        public void Force()
        {
            ForceAll(true);
        }

        void ForceItems(List<Item> items)
        {
            var handles = new List<IntPtr>();
            foreach (Item item in items)
            {
                if (item.State == CloseState.Closed) continue;
                handles.Add(item.Handle);
                item.Forced = System.Diagnostics.Stopwatch.StartNew();
                item.WasForced = true;
            }
            if (handles.Count == 0) return;

            Log.Write("close-all: forcing " + handles.Count + " windows");
            WindowCloser.Force(handles);
            _forced = true;
            Refresh();
        }

        HashSet<IntPtr> OpenHandles()
        {
            var set = new HashSet<IntPtr>();
            for (int i = 0; i < _items.Count; i++)
                if (_items[i].State != CloseState.Closed) set.Add(_items[i].Handle);
            return set;
        }

        /// <summary>
        /// Asks a window whose prompt was answered Cancel to close again. The user may have
        /// cancelled to save first, or by mistake; either way the run had no way back to
        /// the question but Force.
        /// </summary>
        public void AskAgain(IntPtr handle)
        {
            Item item = Find(handle);
            if (item == null || item.State == CloseState.Closed) return;

            Log.Write("close-all: asking \"" + item.Text + "\" again");
            item.State = CloseState.Closing;
            item.Prompted = false;
            item.Unprompted = null;
            item.Asked = System.Diagnostics.Stopwatch.StartNew();
            if (!WindowCloser.Ask(item.Handle)) item.State = CloseState.Refused;
            Refresh();
        }

        bool Exists()
        {
            IList<Desktop> desktops = _service.Desktops;
            for (int i = 0; i < desktops.Count; i++)
                if (desktops[i].Id == _id) return true;
            return false;
        }

        /// <summary>Hands the window every row and the counts above them.</summary>
        void Refresh()
        {
            if (_panel == null || _panel.IsDisposed) return;

            var rows = new List<CloseRow>(_items.Count);
            int closed = 0, needYou = 0;

            for (int i = 0; i < _items.Count; i++)
            {
                Item item = _items[i];
                if (item.State == CloseState.Closed) closed++;
                else if (IsStuck(item)) needYou++;

                rows.Add(new CloseRow
                {
                    Handle = item.Handle,
                    Entry = item.Entry,
                    State = item.State,
                    Hung = item.Hung,
                    Question = item.Question,
                    Forced = item.Forced != null,
                    CanForce = item.CanForce,
                    CanShow = item.State != CloseState.Closed && Native.IsWindow(item.Handle),
                    StuckSeq = item.StuckSeq
                });
            }

            _panel.Update(rows, closed, _items.Count, needYou, _forced);
        }

        void End()
        {
            End(true);
        }

        /// <param name="closePanel">False when the window is showing the outcome and closes
        /// itself; true for every other ending - Cancel, the desktop vanishing, shutdown.</param>
        void End(bool closePanel)
        {
            if (_ended) return;
            _ended = true;

            UnlockForeground();

            if (_timer != null)
            {
                _timer.Stop();
                _timer.Dispose();
                _timer = null;
            }

            if (_panel != null)
            {
                int frames = _panel.Frames;
                int renders = _panel.Renders;
                string timings = _panel.Timings;
                double slowest = _slowestTickTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                long ms = _clock != null ? _clock.ElapsedMilliseconds : 0;
                Log.Write(delegate
                {
                    return string.Format("perf: close-all ran {0} ms, {1} windows, slowest tick {2:0.0} ms, {3} frames and {4} row renders so far ({5})",
                                         ms, _items.Count, slowest, frames, renders, timings);
                });

                // Closed, not disposed here: a modeless form disposes itself on close, and
                // this often runs from inside one of the window's own handlers.
                if (closePanel) _panel.Finish();
                _panel = null;
            }
            Perf.Log("close-all end");

            _closer.Invalidate();

            EventHandler handler = Ended;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            End();
        }
    }
}

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace DesktopSwitcher
{
    /// <summary>
    /// One "Close all and remove", from the confirmed click to the desktop going away.
    ///
    /// Asks every window to close at once, then watches on a short tick and shows each
    /// one's state in CloseProgressWindow as it changes. There is no fixed wait anywhere:
    /// the desktop is removed on the tick the last window goes, and the panel's buttons are
    /// live from the moment it opens.
    ///
    ///   Go there     switches to the desktop, where the questions are. The panel shows on
    ///                every desktop, so it comes along.
    ///   Force close  ends the apps behind whatever is still open, without saving.
    ///   Cancel       stops watching. What closed stays closed; nothing is removed.
    ///
    /// Never removes past an open window. Removing would move a window with an unanswered
    /// save prompt onto a neighbour, which is the opposite of the clean desktop asked for.
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

        sealed class Item
        {
            public IntPtr Handle;
            public string Text;
            public CloseState State = CloseState.Closing;
            public bool Prompted;   // has shown a prompt at some point

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
        readonly Func<WindowEntry, string> _describe;
        readonly List<Item> _items = new List<Item>();
        readonly HashSet<IntPtr> _known = new HashSet<IntPtr>();

        CloseProgressWindow _panel;
        Timer _timer;
        bool _forced;   // Force close has been pressed - for the hint line only
        int _sweeps;
        bool _ended;

        /// <summary>Raised once, however the run ends.</summary>
        public event EventHandler Ended;

        public Guid DesktopId { get { return _id; } }

        public CloseAllRun(DesktopService service, WindowCloser closer, Desktop desktop,
                           IList<WindowEntry> windows, Func<WindowEntry, string> describe)
        {
            _service = service;
            _closer = closer;
            _id = desktop.Id;
            _name = desktop.DisplayName;
            _describe = describe;

            for (int i = 0; i < windows.Count; i++) Track(windows[i]);
        }

        void Track(WindowEntry window)
        {
            if (!_known.Add(window.Handle)) return;

            var item = new Item();
            item.Handle = window.Handle;
            item.Text = _describe(window);
            _items.Add(item);
        }

        /// <summary>Asks everything to close and opens the panel by the strip.</summary>
        public void Start(IWin32Window owner, Color background, double dpiScale, Rectangle anchor)
        {
            Log.Write(delegate { return "close-all: closing " + _items.Count + " windows on " + _name; });

            // The panel first, and only then the requests. Showing it takes the foreground,
            // so when the apps start answering, the window that has focus is this one - not
            // whatever Windows picks once the confirmation closes, which can be a window on
            // another desktop, and activating that switches the user over to it.
            _panel = new CloseProgressWindow("Closing \"" + _name + "\"", background, dpiScale, anchor);
            _panel.GoRequested += delegate { Go(); };
            _panel.ForceRequested += delegate { Force(); };
            _panel.CancelRequested += delegate { Cancel(); };

            _panel.Show(owner);

            // Only when there is another desktop to be pulled to. Closing the one being
            // looked at, the prompts are already here, Go there is greyed out, and the
            // lock would only stop them taking focus - leaving them unanswerable from the
            // keyboard until the user happened to click.
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
        /// that holds the foreground, which is why the panel is made foreground first.
        /// Lifted by Go there, by the end of the run, and by Windows itself the moment the
        /// user clicks elsewhere or presses Alt - it never outlives their own intent.
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

                if (item.State != was) LogItem(item, was.ToString());
            }

            if (IsEmpty())
            {
                // Before removing, one more look at the desktop itself: something may have
                // opened there since the list was taken, and removing would carry it off to
                // a neighbour unasked.
                if (_sweeps < MaxSweeps && SweepForNew())
                {
                    Refresh();
                    return;
                }

                Finish();
                return;
            }

            Refresh();
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

        /// <summary>The panel's Cancel: stop watching, remove nothing.</summary>
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

        public bool HasEnded { get { return _ended; } }

        /// <summary>True once the run has removed the desktop itself.</summary>
        public bool RemovedDesktop { get; private set; }

        void Finish()
        {
            Log.Write("close-all: " + _name + " is empty, removing");
            RemovedDesktop = true;
            _closer.Invalidate();
            if (_service.Count > 1) _service.Remove(_id);
            End();
        }

        /// <summary>
        /// Takes the user to the desktop, where the questions are waiting. The panel is on
        /// every desktop, so it is still there when they arrive and keeps ticking as they
        /// answer; the desktop is removed under them once the last window goes, and Windows
        /// lands them on its neighbour.
        /// </summary>
        public void Go()
        {
            Log.Write("close-all: going to " + _name);

            // The questions over there need to be able to take focus once the user is
            // looking at them.
            UnlockForeground();
            _service.SwitchTo(_id);
        }

        public void Force()
        {
            var open = new List<IntPtr>();
            for (int i = 0; i < _items.Count; i++)
            {
                Item item = _items[i];
                if (item.State == CloseState.Closed) continue;
                open.Add(item.Handle);
                item.Forced = System.Diagnostics.Stopwatch.StartNew();
            }
            if (open.Count == 0) return;

            Log.Write("close-all: forcing " + open.Count + " windows");
            WindowCloser.Force(open);
            _forced = true;
        }

        bool Exists()
        {
            IList<Desktop> desktops = _service.Desktops;
            for (int i = 0; i < desktops.Count; i++)
                if (desktops[i].Id == _id) return true;
            return false;
        }

        /// <summary>Hands the panel its rows, the line above them, and which buttons apply.</summary>
        void Refresh()
        {
            if (_panel == null || _panel.IsDisposed) return;

            var texts = new List<string>(_items.Count);
            var states = new List<CloseState>(_items.Count);
            int closed = 0, prompting = 0, kept = 0, refused = 0;
            string firstPrompting = null;
            int stillOpen = 0;

            for (int i = 0; i < _items.Count; i++)
            {
                Item item = _items[i];
                texts.Add(item.Text);
                states.Add(item.State);

                switch (item.State)
                {
                    case CloseState.Closed: closed++; break;
                    case CloseState.Prompting:
                        prompting++;
                        if (firstPrompting == null) firstPrompting = item.Text;
                        break;
                    case CloseState.KeptOpen: kept++; break;
                    case CloseState.StillOpen: stillOpen++; break;
                    case CloseState.Refused: refused++; break;
                }
            }

            Desktop current = _service.Current;
            bool elsewhere = current != null && current.Id != _id;
            string there = elsewhere ? " - Go there to answer" : "";

            // The line answers "what is it waiting for", most pressing first.
            string status;
            if (prompting == 1)
                status = Short(firstPrompting) + " is asking to save" + there;
            else if (prompting > 1)
                status = prompting + " apps are asking to save" + there;
            else if (stillOpen > 0)
                status = (stillOpen == 1 ? "1 window is" : stillOpen + " windows are")
                         + " still open, maybe asking something" + (elsewhere ? " - Go there to see" : "");
            else if (closed < _items.Count - kept - refused)
                status = closed + " of " + _items.Count + " closed...";
            else if (refused > 0)
                status = refused == 1 ? "1 window could not be closed" : refused + " windows could not be closed";
            else if (kept > 0)
                status = kept == 1 ? "1 window was kept open" : kept + " windows were kept open";
            else
                status = "All closed";

            string hint = _forced
                ? "Force close was sent. What is left is probably running as administrator."
                : "Force close ends these apps without saving, on every desktop.";

            _panel.SetRows(texts, states, status, hint,
                           elsewhere && closed < _items.Count,
                           closed < _items.Count);
        }

        static string Short(string text)
        {
            const int Max = 28;
            if (text.Length <= Max) return text;
            return text.Substring(0, Max - 3).TrimEnd() + "...";
        }

        void End()
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

            // Closed, not disposed here: a modeless form disposes itself on close, and this
            // often runs from inside one of the panel's own button handlers.
            if (_panel != null)
            {
                _panel.Finish();
                _panel = null;
            }

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

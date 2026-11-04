using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DesktopSwitcher
{
    /// <summary>
    /// Pins a view to every desktop. Test-only: the app itself never pins anything, it only
    /// asks whether a window is pinned, and does that by visibility (VirtualDesktopApi.IsPinned)
    /// so as not to depend on this interface. --closeall uses it to make a pinned window to
    /// check that answer against.
    ///
    /// Win10 19041+ layout, as the virtual desktop tools in the wild declare it. Do not
    /// reorder. A wrong slot here can only take down the selftest process.
    /// </summary>
    [ComImport, Guid("4CE81583-1E4C-4632-A621-07A53543148F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IVirtualDesktopPinnedApps
    {
        bool IsAppIdPinned(string appId);
        void PinAppID(string appId);
        void UnpinAppID(string appId);
        bool IsViewPinned(IApplicationView view);
        void PinView(IApplicationView view);
        void UnpinView(IApplicationView view);
    }

    /// <summary>
    /// --closeall: "Close all and remove" driven end to end against scripted windows, on
    /// throwaway desktops, so nothing of the user's is ever asked to close.
    ///
    /// Each test window is its own process (--closetarget), started from this same exe, so
    /// a Force close ends that window and nothing else, and each behaves like one kind of
    /// real app: closes at once, hides to a tray, closes slowly, puts up a real modal
    /// "save?" dialog, ignores the request (an in-window question, as far as anything
    /// outside can tell), hangs, or opens a new window as it goes.
    ///
    /// The run is the real CloseAllRun, over the real DesktopService, WindowInventory and
    /// WindowCloser; only the confirmation is skipped, and the panel's buttons are pressed
    /// through the run's own Go, Force and Cancel. Every scenario creates its own desktop
    /// and is cleaned up in a finally - test processes killed, the desktop removed if the
    /// run did not, the user switched back - whether it passed, failed or threw.
    ///
    /// What it cannot cover: elevated windows (would need admin), a packaged app's
    /// unpaired CoreWindow (cannot be made on demand), and anything about how the panel
    /// looks.
    /// </summary>
    static class CloseAllStress
    {
        /// <summary>Tells a test window to close for real, or its dialog to answer OK.</summary>
        const int WM_ANSWER = Native.WM_APP + 0x51;

        const string TitlePrefix = "DSWTEST ";

        // --- the test window, in its own process ---------------------------------

        /// <summary>--closetarget behavior title: one scripted window, until it closes.</summary>
        public static int RunTarget(string[] args)
        {
            if (args.Length < 3) return 2;

            var form = new TargetForm(args[1], args[2], 0);
            form.Show();
            Application.Run(new ApplicationContext());
            return 0;
        }

        sealed class TargetForm : Form
        {
            static int _open;

            readonly string _behavior;
            bool _allow;

            public TargetForm(string behavior, string title, int offset)
            {
                _behavior = behavior;
                Text = title;
                ShowInTaskbar = true;
                StartPosition = FormStartPosition.Manual;
                Size = new Size(380, 120);
                Location = new Point(80 + offset, 80 + offset);

                var label = new Label();
                label.Text = "DesktopSwitcher close-all test window: " + behavior;
                label.Dock = DockStyle.Fill;
                Controls.Add(label);

                _open++;
                FormClosed += delegate
                {
                    if (--_open == 0) Application.ExitThread();
                };
            }

            // Never steal the foreground from the user while the test runs.
            protected override bool ShowWithoutActivation
            {
                get { return true; }
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WM_ANSWER)
                {
                    _allow = true;
                    Close();
                    return;
                }
                base.WndProc(ref m);
            }

            protected override void OnFormClosing(FormClosingEventArgs e)
            {
                if (_allow)
                {
                    base.OnFormClosing(e);
                    return;
                }

                switch (_behavior)
                {
                    case "hide":
                        // A tray app: the X hides it, the process lives on.
                        e.Cancel = true;
                        Hide();
                        return;

                    case "ignore":
                        // An in-window question: open, enabled, nothing to see from outside.
                        e.Cancel = true;
                        return;

                    case "hang":
                        System.Threading.Thread.Sleep(System.Threading.Timeout.Infinite);
                        return;

                    case "slow":
                    case "slow2":
                    {
                        e.Cancel = true;
                        var timer = new Timer();
                        timer.Interval = _behavior == "slow" ? 600 : 1800;
                        timer.Tick += delegate
                        {
                            timer.Stop();
                            _allow = true;
                            Close();
                        };
                        timer.Start();
                        return;
                    }

                    case "modal":
                    {
                        // A real "save?" dialog: ShowDialog disables this window while it is up.
                        e.Cancel = true;
                        DialogResult answer;
                        using (var dialog = new AnswerDialog()) answer = dialog.ShowDialog(this);
                        if (answer == DialogResult.OK)
                        {
                            _allow = true;
                            BeginInvoke((MethodInvoker)Close);
                        }
                        return;
                    }

                    case "respawn":
                    {
                        // An app that answers its close by opening another window.
                        var next = new TargetForm("ignore", Text + " respawned", 40);
                        next.Show();
                        base.OnFormClosing(e);
                        return;
                    }

                    default:   // "close"
                        base.OnFormClosing(e);
                        return;
                }
            }
        }

        /// <summary>WM_ANSWER is Save; WM_CLOSE is Cancel, as a dialog's X would be.</summary>
        sealed class AnswerDialog : Form
        {
            public AnswerDialog()
            {
                Text = TitlePrefix + "Save changes?";
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.CenterParent;
                Size = new Size(260, 110);
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WM_ANSWER)
                {
                    DialogResult = DialogResult.OK;
                    return;
                }
                base.WndProc(ref m);
            }
        }

        // --- the harness ------------------------------------------------------------

        [DllImport("dwmapi.dll")]
        static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

        const int DWMWA_CLOAKED = 14;

        static readonly Guid CLSID_PinnedApps = new Guid("B5A399E7-1C87-46B8-88E9-FC5747B171BD");
        static readonly Guid SVC_ViewCollection = new Guid("1841C6D7-4F9D-42C0-AF41-8747538F10E5");

        delegate void Scenario();

        static VirtualDesktopApi _api;
        static Form _pump;
        static DesktopService _service;
        static WindowCloser _closer;
        static Guid _original;
        static Guid _scratch;
        static CloseAllRun _run;
        static readonly List<Process> _procs = new List<Process>();
        static readonly List<string> _failures = new List<string>();
        static string _scenario;
        static int _pass, _fail, _counter;

        public static int Run(VirtualDesktopApi api)
        {
            _api = api;
            Console.WriteLine("Close-all stress test. Every window it closes is one of its own, on a");
            Console.WriteLine("desktop it creates; each scenario cleans up after itself.");
            Console.WriteLine();

            using (_pump = new Form())
            {
                IntPtr forceHandle = _pump.Handle;
                GC.KeepAlive(forceHandle);

                _service = new DesktopService(api, _pump);
                _service.Start();
                var inventory = new WindowInventory(api);
                _closer = new WindowCloser(api, inventory);
                _original = api.GetCurrentId();

                var total = Stopwatch.StartNew();

                Do("windows that close by themselves", PlainCloses);
                Do("a real save dialog, answered Save", DialogAnswered);
                Do("a real save dialog, answered Cancel", DialogCancelled);
                Do("Force close on a hung and an unresponsive window", ForceClose);
                Do("Go there, then answering", GoThere);
                Do("the desktop removed from under the run", RemovedUnderRun);
                Do("a pinned window is left alone", PinnedLeftAlone);
                Do("an app that opens a new window as it closes", Respawn);
                Do("a window gone before it is asked", GoneBeforeAsked);
                Do("a window that arrives mid-run", ArrivesMidRun);
                Do("the harness's own window is excluded", OwnWindowExcluded);
                Do("closing the desktop you are on", OnCurrentDesktop);
                Do("twenty windows at once", Many);

                _service.Dispose();

                Console.WriteLine();
                Console.WriteLine(string.Format("{0} checks passed, {1} failed, in {2:0.0} s",
                    _pass, _fail, total.Elapsed.TotalSeconds));
                foreach (string f in _failures) Console.WriteLine("  FAIL  " + f);
                Console.WriteLine();
            }

            return _fail == 0 ? 0 : 1;
        }

        static void Do(string name, Scenario scenario)
        {
            _scenario = name;
            Console.WriteLine("- " + name);
            var clock = Stopwatch.StartNew();
            try
            {
                NewScratch();
                scenario();
            }
            catch (Exception ex)
            {
                Check(false, "threw " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                Cleanup();
            }
            Console.WriteLine(string.Format("    ({0} ms)", clock.ElapsedMilliseconds));
        }

        // --- scenarios -------------------------------------------------------------

        static void PlainCloses()
        {
            IntPtr close = Spawn("close");
            IntPtr hide = Spawn("hide");
            IntPtr slow = Spawn("slow");
            IntPtr slow2 = Spawn("slow2");

            CloseAllRun run = StartRun();
            Check(run.Handles.Length == 4, "all four windows listed (" + run.Handles.Length + ")");

            bool slowWentStillOpen = false, slow2WentStillOpen = false;
            WaitFor(delegate
            {
                if (StateOf(slow) == CloseState.StillOpen) slowWentStillOpen = true;
                if (StateOf(slow2) == CloseState.StillOpen) slow2WentStillOpen = true;
                return run.HasEnded;
            }, 6000);

            Check(run.HasEnded && run.RemovedDesktop, "run ended and removed the desktop");
            Check(!DesktopExists(_scratch), "desktop is gone");
            Check(_api.GetCurrentId() == _original, "user stayed on their own desktop");
            Check(!Native.IsWindowVisible(hide), "the tray-style window counted as closed once hidden");
            Check(!slowWentStillOpen, "a 0.6 s close never read as still open");
            Check(slow2WentStillOpen, "a 1.8 s close read as still open before it went");
        }

        static void DialogAnswered()
        {
            IntPtr modal = Spawn("modal");
            IntPtr ignore = Spawn("ignore");

            CloseAllRun run = StartRun();
            bool seen = WaitFor(delegate
            {
                return StateOf(modal) == CloseState.Prompting && StateOf(ignore) == CloseState.StillOpen;
            }, 3000);
            Check(seen, "dialog read as asking to save, in-window question as still open ("
                        + StateOf(modal) + ", " + StateOf(ignore) + ")");
            Check(_api.GetCurrentId() == _original, "user was not pulled to the other desktop");

            IntPtr popup = Native.GetLastActivePopup(modal);
            Check(popup != modal && popup != IntPtr.Zero, "the dialog is findable from its window");

            Guid popupDesktop;
            bool known = _api.TryGetWindowDesktop(popup, out popupDesktop);
            Console.WriteLine("    note: the dialog opened on " + (known
                ? (popupDesktop == _scratch ? "the test desktop" : popupDesktop == _original ? "the user's desktop" : "another desktop")
                : "no desktop of its own (owned windows follow their owner)"));

            Native.PostMessage(popup, WM_ANSWER, IntPtr.Zero, IntPtr.Zero);
            Native.PostMessage(ignore, WM_ANSWER, IntPtr.Zero, IntPtr.Zero);

            WaitFor(delegate { return run.HasEnded; }, 4000);
            Check(run.RemovedDesktop && !DesktopExists(_scratch), "both closed, desktop removed");
        }

        static void DialogCancelled()
        {
            IntPtr modal = Spawn("modal");

            CloseAllRun run = StartRun();
            Check(WaitFor(delegate { return StateOf(modal) == CloseState.Prompting; }, 3000),
                  "dialog read as asking to save");

            IntPtr popup = Native.GetLastActivePopup(modal);
            Native.PostMessage(popup, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            var since = Stopwatch.StartNew();

            bool flashed = false;
            bool kept = WaitFor(delegate
            {
                if (StateOf(modal) == CloseState.KeptOpen && since.ElapsedMilliseconds < 400) flashed = true;
                return StateOf(modal) == CloseState.KeptOpen;
            }, 3000);
            Check(kept, "Cancel on the dialog read as kept open (after " + since.ElapsedMilliseconds + " ms)");
            Check(!flashed, "kept open did not show before the half-second settle");

            Pump(600);
            Check(!run.HasEnded && DesktopExists(_scratch), "a kept-open window holds the desktop");

            run.Cancel();
            Pump(200);
            Check(run.HasEnded && !run.RemovedDesktop, "Cancel ended the run without removing");
            Check(DesktopExists(_scratch) && Native.IsWindow(modal), "desktop and window are both still there");
        }

        static void ForceClose()
        {
            IntPtr hang = Spawn("hang");
            IntPtr ignore = Spawn("ignore");

            CloseAllRun run = StartRun();
            Check(WaitFor(delegate
            {
                return StateOf(hang) == CloseState.StillOpen && StateOf(ignore) == CloseState.StillOpen;
            }, 3000), "hung and unresponsive windows read as still open");

            run.Force();
            WaitFor(delegate { return run.HasEnded; }, 4000);
            Check(run.RemovedDesktop && !DesktopExists(_scratch), "Force closed both, desktop removed");
            Check(!Native.IsWindow(hang) && !Native.IsWindow(ignore), "both windows are gone");
        }

        static void GoThere()
        {
            IntPtr ignore = Spawn("ignore");

            CloseAllRun run = StartRun();
            WaitFor(delegate { return StateOf(ignore) == CloseState.StillOpen; }, 3000);

            run.Go();
            Check(WaitFor(delegate { return _api.GetCurrentId() == _scratch; }, 2000), "Go there switched to the desktop");
            Pump(300);

            IntPtr panel = Native.FindWindow(null, "Closing \"" + DisplayName(_scratch) + "\"");
            Check(panel != IntPtr.Zero, "the panel window exists");
            if (panel != IntPtr.Zero)
            {
                int cloaked;
                DwmGetWindowAttribute(panel, DWMWA_CLOAKED, out cloaked, 4);
                Check(Native.IsWindowVisible(panel) && cloaked == 0,
                      "the panel is visible on the desktop it went to (cloaked=" + cloaked + ")");
            }

            Native.PostMessage(ignore, WM_ANSWER, IntPtr.Zero, IntPtr.Zero);
            WaitFor(delegate { return run.HasEnded; }, 4000);
            Check(run.RemovedDesktop && !DesktopExists(_scratch), "answered there, desktop removed");
        }

        static void RemovedUnderRun()
        {
            IntPtr ignore = Spawn("ignore");

            CloseAllRun run = StartRun();
            WaitFor(delegate { return StateOf(ignore) == CloseState.StillOpen; }, 3000);

            _api.Remove(_scratch);
            Check(WaitFor(delegate { return run.HasEnded; }, 3000), "the run noticed and ended");
            Check(!run.RemovedDesktop, "it did not claim the removal");
            Check(Native.IsWindow(ignore), "the window it was waiting on was left alone");
        }

        static void PinnedLeftAlone()
        {
            IntPtr pinned = Spawn("close");
            IntPtr plain = Spawn("close");

            if (!Pin(pinned, true))
            {
                Check(false, "could not pin a window - the pinned-apps interface did not answer");
                return;
            }
            Pump(300);

            Check(_api.IsPinned(pinned), "IsPinned sees the pinned window");
            Check(!_api.IsPinned(plain), "IsPinned does not see the plain one");

            _closer.Invalidate();
            List<WindowEntry> list = _closer.Closeable(_scratch);
            bool hasPinned = false, hasPlain = false;
            foreach (WindowEntry w in list)
            {
                if (w.Handle == pinned) hasPinned = true;
                if (w.Handle == plain) hasPlain = true;
            }
            Check(hasPlain && !hasPinned, "Closeable lists the plain window and not the pinned one");

            CloseAllRun run = StartRun();
            WaitFor(delegate { return run.HasEnded; }, 4000);
            Check(run.RemovedDesktop, "desktop removed");
            Check(Native.IsWindow(pinned) && Native.IsWindowVisible(pinned), "the pinned window is still open");

            Pin(pinned, false);
        }

        static void Respawn()
        {
            Spawn("respawn");

            // Where the replacement opens decides what is right: on the user's desktop the
            // test desktop really is empty and should go; on the test desktop the run must
            // pick it up and wait. Read the moment it appears, before a removal could move it.
            CloseAllRun run = StartRun();
            IntPtr next = IntPtr.Zero;
            Guid where = Guid.Empty;
            WaitFor(delegate
            {
                if (next == IntPtr.Zero)
                {
                    next = FindTestWindow("respawned");
                    if (next != IntPtr.Zero) _api.TryGetWindowDesktop(next, out where);
                }
                return run.HasEnded || (next != IntPtr.Zero && Array.IndexOf(run.Handles, next) >= 0);
            }, 5000);

            Check(next != IntPtr.Zero, "the app's replacement window appeared");
            if (next == IntPtr.Zero) return;

            Console.WriteLine("    note: the replacement opened on " + (where == _scratch ? "the test desktop"
                : where == _original ? "the user's desktop" : "another desktop"));

            if (where == _scratch)
            {
                Check(!run.RemovedDesktop && Array.IndexOf(run.Handles, next) >= 0,
                      "the run picked it up instead of removing the desktop past it");
            }
            else
            {
                WaitFor(delegate { return run.HasEnded; }, 3000);
                Check(run.RemovedDesktop, "the test desktop, now empty, was removed");
                Check(Native.IsWindow(next), "the replacement elsewhere was not touched");
            }
        }

        static void GoneBeforeAsked()
        {
            IntPtr doomed = Spawn("close");
            IntPtr plain = Spawn("close");

            _closer.Invalidate();
            List<WindowEntry> list = _closer.Closeable(_scratch);

            // Killed between the listing and the asking.
            Process owner = OwnerOf(doomed);
            if (owner != null) { owner.Kill(); owner.WaitForExit(3000); }
            WaitFor(delegate { return !Native.IsWindow(doomed); }, 2000);

            CloseAllRun run = StartRun(list);
            WaitFor(delegate { return run.HasEnded; }, 4000);
            Check(StateOf(run, doomed) == CloseState.Closed, "the vanished window read as closed, not refused ("
                  + StateOf(run, doomed) + ")");
            Check(run.RemovedDesktop, "desktop removed");
        }

        static void ArrivesMidRun()
        {
            IntPtr first = Spawn("ignore");

            CloseAllRun run = StartRun();
            WaitFor(delegate { return StateOf(first) == CloseState.StillOpen; }, 3000);

            IntPtr late = Spawn("ignore");
            Native.PostMessage(first, WM_ANSWER, IntPtr.Zero, IntPtr.Zero);

            Check(WaitFor(delegate { return Array.IndexOf(run.Handles, late) >= 0; }, 3000),
                  "the late window was picked up before the desktop was removed");
            CloseState firstSeen = StateOf(late);
            Check(firstSeen == CloseState.Closing, "it started at closing, timed on its own (" + firstSeen + ")");
            Check(DesktopExists(_scratch), "the desktop was not removed past it");

            Check(WaitFor(delegate { return StateOf(late) == CloseState.StillOpen; }, 3000),
                  "it read as still open a second later");

            Native.PostMessage(late, WM_ANSWER, IntPtr.Zero, IntPtr.Zero);
            WaitFor(delegate { return run.HasEnded; }, 4000);
            Check(run.RemovedDesktop, "then the desktop was removed");
        }

        static void OwnWindowExcluded()
        {
            using (var own = new Form())
            {
                own.Text = TitlePrefix + "own window";
                own.ShowInTaskbar = true;
                own.StartPosition = FormStartPosition.Manual;
                own.Location = new Point(120, 120);
                own.Size = new Size(300, 100);
                own.Show();
                Pump(200);
                _api.MoveWindow(own.Handle, _scratch);
                IntPtr plain = Spawn("close");

                _closer.Invalidate();
                bool listed = false;
                foreach (WindowEntry w in _closer.Closeable(_scratch))
                    if (w.Handle == own.Handle) listed = true;
                Check(!listed, "Closeable leaves the closing process's own window out");

                CloseAllRun run = StartRun();
                WaitFor(delegate { return run.HasEnded; }, 4000);
                Check(run.RemovedDesktop, "desktop removed");
                Check(!own.IsDisposed && Native.IsWindow(own.Handle), "own window was not closed");
                own.Close();
            }
        }

        static void OnCurrentDesktop()
        {
            IntPtr modal = Spawn("modal");
            _api.SwitchTo(_scratch);
            WaitFor(delegate { return _service.Current != null && _service.Current.Id == _scratch; }, 2000);

            CloseAllRun run = StartRun();
            Check(WaitFor(delegate { return StateOf(modal) == CloseState.Prompting; }, 3000),
                  "dialog read as asking to save");

            IntPtr popup = Native.GetLastActivePopup(modal);
            bool focused = WaitFor(delegate { return Native.GetForegroundWindow() == popup; }, 1500);
            Check(focused, "with no other desktop to be pulled to, the dialog got focus (no lock)");

            Native.PostMessage(popup, WM_ANSWER, IntPtr.Zero, IntPtr.Zero);
            WaitFor(delegate { return run.HasEnded; }, 4000);
            Check(run.RemovedDesktop && !DesktopExists(_scratch), "desktop removed from under the user");
        }

        static void Many()
        {
            for (int i = 0; i < 20; i++) Spawn("close");

            var clock = Stopwatch.StartNew();
            CloseAllRun run = StartRun();
            Check(run.Handles.Length == 20, "all twenty listed (" + run.Handles.Length + ")");
            WaitFor(delegate { return run.HasEnded; }, 8000);
            Check(run.RemovedDesktop, "desktop removed after " + clock.ElapsedMilliseconds + " ms");
        }

        // --- helpers -----------------------------------------------------------------

        static void NewScratch()
        {
            var before = new HashSet<Guid>(_api.GetDesktopIds());
            _api.Create();

            foreach (Guid id in _api.GetDesktopIds())
                if (!before.Contains(id)) _scratch = id;

            if (_scratch == Guid.Empty) throw new InvalidOperationException("no scratch desktop appeared");
            WaitFor(delegate { return Find(_scratch) != null; }, 3000);
        }

        /// <summary>A scripted window in a process of its own, moved onto the scratch desktop.</summary>
        static IntPtr Spawn(string behavior)
        {
            _counter++;
            string title = TitlePrefix + behavior + " " + _counter;

            var psi = new ProcessStartInfo(System.Reflection.Assembly.GetExecutingAssembly().Location,
                                           "--closetarget " + behavior + " \"" + title + "\"");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            Process p = Process.Start(psi);
            _procs.Add(p);

            IntPtr hwnd = IntPtr.Zero;
            WaitFor(delegate
            {
                p.Refresh();
                hwnd = p.MainWindowHandle;
                return hwnd != IntPtr.Zero;
            }, 8000);
            if (hwnd == IntPtr.Zero) throw new InvalidOperationException("test window " + title + " never appeared");

            _api.MoveWindow(hwnd, _scratch);
            Guid where = Guid.Empty;
            WaitFor(delegate { return _api.TryGetWindowDesktop(hwnd, out where) && where == _scratch; }, 3000);
            if (where != _scratch) throw new InvalidOperationException("could not move " + title + " to the test desktop");
            return hwnd;
        }

        static CloseAllRun StartRun()
        {
            _closer.Invalidate();
            return StartRun(_closer.Closeable(_scratch));
        }

        static CloseAllRun StartRun(List<WindowEntry> windows)
        {
            Desktop desktop = Find(_scratch);
            if (desktop == null) throw new InvalidOperationException("scratch desktop not in the model");

            _run = new CloseAllRun(_service, _closer, desktop, windows,
                                   delegate(WindowEntry w) { return w.Title; });
            _run.Start(_pump, Color.FromArgb(0x20, 0x20, 0x20), 1.0, Rectangle.Empty);
            return _run;
        }

        static CloseState StateOf(IntPtr hwnd)
        {
            return StateOf(_run, hwnd);
        }

        static CloseState StateOf(CloseAllRun run, IntPtr hwnd)
        {
            IntPtr[] handles = run.Handles;
            CloseState[] states = run.States;
            int i = Array.IndexOf(handles, hwnd);
            return i < 0 ? (CloseState)(-1) : states[i];
        }

        static bool Pin(IntPtr hwnd, bool pin)
        {
            try
            {
                Guid svc = SVC_ViewCollection, iid = SVC_ViewCollection;
                var views = (IApplicationViewCollection)_api.Shell.QueryService(ref svc, ref iid);
                IApplicationView view;
                if (views.GetViewForHwnd(hwnd, out view) < 0 || view == null) return false;

                Guid clsid = CLSID_PinnedApps, iidPinned = typeof(IVirtualDesktopPinnedApps).GUID;
                var pinned = (IVirtualDesktopPinnedApps)_api.Shell.QueryService(ref clsid, ref iidPinned);
                if (pin) pinned.PinView(view);
                else pinned.UnpinView(view);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine("    note: pin failed - " + ex.Message);
                return false;
            }
        }

        static IntPtr FindTestWindow(string fragment)
        {
            IntPtr found = IntPtr.Zero;
            Native.EnumWindows(delegate(IntPtr h, IntPtr p)
            {
                string t = Native.GetText(h);
                if (t.StartsWith(TitlePrefix) && t.Contains(fragment)) { found = h; return false; }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        static Process OwnerOf(IntPtr hwnd)
        {
            uint pid = Native.GetOwningProcessId(hwnd);
            foreach (Process p in _procs)
                if ((uint)p.Id == pid) return p;
            return null;
        }

        static Desktop Find(Guid id)
        {
            foreach (Desktop d in _service.Desktops)
                if (d.Id == id) return d;
            return null;
        }

        static string DisplayName(Guid id)
        {
            Desktop d = Find(id);
            return d == null ? "" : d.DisplayName;
        }

        static bool DesktopExists(Guid id)
        {
            return Array.IndexOf(_api.GetDesktopIds(), id) >= 0;
        }

        static void Check(bool ok, string what)
        {
            if (ok) _pass++;
            else
            {
                _fail++;
                _failures.Add(_scenario + ": " + what);
            }
            Console.WriteLine((ok ? "    ok    " : "    FAIL  ") + what);
        }

        static void Pump(int ms)
        {
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < ms)
            {
                Application.DoEvents();
                System.Threading.Thread.Sleep(10);
            }
        }

        delegate bool Condition();

        static bool WaitFor(Condition condition, int ms)
        {
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < ms)
            {
                Application.DoEvents();
                if (condition()) return true;
                System.Threading.Thread.Sleep(10);
            }
            Application.DoEvents();
            return condition();
        }

        /// <summary>
        /// Whatever the scenario left behind, gone: the run stopped, every test process
        /// killed (only ever the ones this harness started), the scratch desktop removed if
        /// the run did not, and the user back where they started.
        /// </summary>
        static void Cleanup()
        {
            try
            {
                if (_run != null && !_run.HasEnded) _run.Cancel();
                _run = null;

                foreach (Process p in _procs)
                {
                    try
                    {
                        if (!p.HasExited) { p.Kill(); p.WaitForExit(3000); }
                    }
                    catch (Exception) { }
                }
                _procs.Clear();
                Pump(200);

                if (_scratch != Guid.Empty && DesktopExists(_scratch)) _api.Remove(_scratch);
                _scratch = Guid.Empty;

                if (_api.GetCurrentId() != _original && DesktopExists(_original)) _api.SwitchTo(_original);
                Pump(300);
            }
            catch (Exception ex)
            {
                Console.WriteLine("    cleanup: " + ex.Message);
            }
        }
    }
}

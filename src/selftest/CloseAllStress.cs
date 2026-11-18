using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DesktopSwitcher
{
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
    /// WindowCloser; only the confirmation is skipped, and the window's buttons are pressed
    /// through the run's own methods - Show, ForceRow, Force, AskAgain, Cancel - which are
    /// what the buttons call. Every scenario creates its own desktop
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

            if (args[1] == "twin")
            {
                // A second window in the same process, on whichever desktop is current - the
                // user's, since only the first is moved to the test desktop. Late, so the
                // harness's MainWindowHandle finds the first. What a browser with a window on
                // another desktop looks like to a Force.
                var later = new Timer();
                later.Interval = 300;
                later.Tick += delegate
                {
                    later.Stop();
                    new TargetForm("ignore", args[2] + " twin", 40).Show();
                };
                later.Start();
            }
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
                    case "twin":
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
                Size = new Size(300, 110);

                // The question as text inside the dialog, where a real one has it, so the
                // close-all row's quote is read off the dialog's contents, not its caption.
                // A WinForms label: read as a Static child (UI Automation does not reliably
                // see it - see DialogText.ByStatics).
                var label = new Label();
                label.Text = Question;
                label.Dock = DockStyle.Fill;
                Controls.Add(label);
            }

            public const string Question = "Do you want to save changes to the test window?";

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

        static string _filter;

        public static int Run(VirtualDesktopApi api)
        {
            return Run(api, null);
        }

        /// <param name="filter">
        /// Only the scenarios whose name contains this, ignoring case - for measuring one
        /// thing, or chasing one failure, without a minute of windows flashing up for the rest.
        /// </param>
        public static int Run(VirtualDesktopApi api, string filter)
        {
            _filter = filter;
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
                Do("Close all windows keeps the desktop", KeepsDesktop);
                Do("a checklist closes only the ticked windows", SelectedOnly);
                Do("the checklist after the desktop moved on under it", SelectionMovedOn);
                Do("removing takes every window, whatever was ticked", RemoveTakesAll);
                Do("Show brings up a minimized window asking inside itself", ShowMinimized);
                Do("Show gives a real dialog the keyboard", ShowDialogFocus);
                Do("Show from another desktop, to a real dialog", ShowFromElsewhere);
                Do("the row quotes the dialog's question", QuestionRead);
                Do("stuck rows come first", StuckFirst);
                Do("Ask again after a cancelled prompt", AskAgain);
                Do("a row's Force ends only that app", ForceOne);
                Do("Force asks twice when the app has windows elsewhere", ForceArms);
                Do("a hung window reads as not responding", HungRow);
                Do("the confirmation lays out its buttons before it is shown", ConfirmLayout);

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
            if (_filter != null && name.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) < 0) return;
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
            CheckDoneRows(run);
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
            Check(focused, "with no other desktop to be pulled to, the dialog got focus (no lock)"
                  + (focused ? "" : " - foreground is " + DescribeForeground()));

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
            Check(run.Window != null && run.Window.DonePending,
                  "the desktop went on time, and the done message waits for the cascade to tell it");

            // Twenty plain windows go together, mostly inside one tick - the case a real
            // desktop of five hit, where the done window was left showing them all closing.
            CheckDoneRows(run);
        }

        /// <summary>
        /// The menu's "Close all windows", the one the last desktop has. It cannot be run
        /// on the real last desktop - that would close the user's own windows - and need
        /// not be: what differs is the run's Remove flag, not the count, so a scratch
        /// desktop beside the user's proves it. Finish's count check is the same line the
        /// removing runs above already go through.
        /// </summary>
        static void KeepsDesktop()
        {
            IntPtr close = Spawn("close");
            IntPtr slow = Spawn("slow2");

            CloseAllRun run = StartRun(false);
            Check(!run.Remove, "the run was told to keep the desktop");
            Check(!run.ShowsHere, "the run's desktop is another one, so a Show would switch first");

            // One more window opened on the desktop while the run is going. A removing run
            // sweeps for these before it removes; this one must leave it, since nothing is
            // being carried off and on the last desktop it is likely the user's own.
            IntPtr late = Spawn("close");
            bool lateInTime = !run.HasEnded;

            WaitFor(delegate { return run.HasEnded; }, 6000);
            Check(run.HasEnded, "run ended");
            Check(!run.RemovedDesktop, "it did not claim a removal");
            Check(DesktopExists(_scratch), "desktop is still there");
            Check(WindowCloser.IsGone(close) && WindowCloser.IsGone(slow), "both listed windows closed");

            if (!lateInTime)
                Console.WriteLine("    note: the late window opened after the run ended; its check proves less");
            Check(Array.IndexOf(run.Handles, late) < 0, "the window opened mid-run was not picked up");
            Check(Native.IsWindow(late) && Native.IsWindowVisible(late), "and it is still open");
        }

        /// <summary>
        /// The close-all checklist with one row unticked: the controller hands
        /// CloseSelection the ticked handles and a fresh Closeable list, and runs what comes
        /// back with remove off - a partial selection never removes. The dialog itself is
        /// not driven; what it returns is a set of handles, and this is that set.
        /// </summary>
        static void SelectedOnly()
        {
            IntPtr a = Spawn("close");
            IntPtr b = Spawn("close");
            IntPtr keep = Spawn("close");

            var ticked = new HashSet<IntPtr> { a, b };
            _closer.Invalidate();
            List<WindowEntry> chosen = CloseSelection.Resolve(false, ticked, _closer.Closeable(_scratch));
            Check(chosen.Count == 2, "the two ticked windows resolve, and only those");

            CloseAllRun run = StartRun(chosen, false);
            WaitFor(delegate { return run.HasEnded; }, 6000);
            Check(run.HasEnded, "run ended");
            Check(WindowCloser.IsGone(a) && WindowCloser.IsGone(b), "both ticked windows closed");
            Check(Array.IndexOf(run.Handles, keep) < 0, "the unticked window was never in the run");
            Check(Native.IsWindow(keep) && Native.IsWindowVisible(keep), "and it is still open");
            Check(!run.RemovedDesktop && DesktopExists(_scratch), "the desktop stays");
        }

        /// <summary>
        /// The dialog pumps while it is up, so by the answer a ticked window may have closed
        /// and a new one may have opened. The ticked one that went is dropped; the new one
        /// was never on the list the user saw, so it is not closed either.
        /// </summary>
        static void SelectionMovedOn()
        {
            IntPtr a = Spawn("close");
            IntPtr gone = Spawn("close");
            IntPtr keep = Spawn("close");
            var ticked = new HashSet<IntPtr> { a, gone };

            // "While the dialog was up."
            Native.PostMessage(gone, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            WaitFor(delegate { return WindowCloser.IsGone(gone); }, 3000);
            IntPtr late = Spawn("close");

            _closer.Invalidate();
            List<WindowEntry> chosen = CloseSelection.Resolve(false, ticked, _closer.Closeable(_scratch));
            Check(chosen.Count == 1 && chosen[0].Handle == a, "only the surviving ticked window resolves");

            CloseAllRun run = StartRun(chosen, false);
            WaitFor(delegate { return run.HasEnded; }, 6000);
            Check(run.HasEnded, "run ended");
            Check(WindowCloser.IsGone(a), "the ticked window closed");
            Check(Native.IsWindow(keep) && Native.IsWindowVisible(keep), "the unticked window is still open");
            Check(Native.IsWindow(late) && Native.IsWindowVisible(late), "the window that arrived is still open");
            Check(DesktopExists(_scratch), "the desktop stays");
        }

        /// <summary>
        /// Remove is only enabled with every row ticked, but the rule does not lean on that:
        /// handed a partial set with removing on, it still returns every window, so a remove
        /// can never leave one behind for the shell to carry to a neighbour.
        /// </summary>
        static void RemoveTakesAll()
        {
            IntPtr a = Spawn("close");
            IntPtr b = Spawn("close");
            IntPtr c = Spawn("close");

            _closer.Invalidate();
            List<WindowEntry> current = _closer.Closeable(_scratch);
            List<WindowEntry> chosen = CloseSelection.Resolve(true, new HashSet<IntPtr> { a }, current);
            Check(chosen.Count == current.Count && current.Count == 3, "every window resolves when removing");

            CloseAllRun run = StartRun(chosen, true);
            WaitFor(delegate { return run.HasEnded; }, 6000);
            Check(run.HasEnded, "run ended");
            Check(WindowCloser.IsGone(a) && WindowCloser.IsGone(b) && WindowCloser.IsGone(c), "all three closed");
            Check(run.RemovedDesktop, "desktop removed");
        }

        const int SW_MINIMIZE = 6;

        /// <summary>Switches to the scratch desktop and waits out the switch's focus nudge.</summary>
        static void GoToScratch()
        {
            _api.SwitchTo(_scratch);
            WaitFor(delegate { return _service.Current != null && _service.Current.Id == _scratch; }, 2000);
            // The nudge fires 200 ms after a switch; anything this scenario sets up for
            // focus has to come after it, or it is testing the nudge instead.
            Pump(500);
        }

        /// <summary>
        /// What the screenshot that prompted Show caught: Notepads asking "Save?" inside a
        /// minimized window on the desktop being cleared, the panel saying "still open",
        /// and Go there greyed out because the user was already there.
        /// </summary>
        static void ShowMinimized()
        {
            IntPtr ignore = Spawn("ignore");
            GoToScratch();
            Native.ShowWindow(ignore, SW_MINIMIZE);
            Check(WaitFor(delegate { return Native.IsIconic(ignore); }, 1500), "the window is minimized");

            CloseAllRun run = StartRun(false);
            Check(WaitFor(delegate { return StateOf(ignore) == CloseState.StillOpen; }, 3000), "read as still open");
            Check(run.ShowsHere, "the run's desktop is the current one, so Show acts at once");

            run.Show();
            Check(run.LastShown == ignore, "Show picked the still-open window");
            Check(WaitFor(delegate { return !Native.IsIconic(ignore); }, 1500), "Show restored it");
            bool focused = WaitFor(delegate { return Native.GetForegroundWindow() == ignore; }, 1500);
            Check(focused, "and gave it the keyboard" + (focused ? "" : " - foreground is " + DescribeForeground()));

            Native.PostMessage(ignore, WM_ANSWER, IntPtr.Zero, IntPtr.Zero);
            WaitFor(delegate { return run.HasEnded; }, 4000);
            Check(run.HasEnded && !run.RemovedDesktop && DesktopExists(_scratch), "answered, run ended, desktop kept");
        }

        /// <summary>
        /// Show on a window disabled behind a real dialog: the keyboard goes to the dialog,
        /// not to the owner that cannot take input.
        ///
        /// The keyboard is taken off the dialog by focusing another window, the way a user
        /// clicking elsewhere would. Not by minimizing the owner: a first version did, and
        /// minimizing the owner of a WinForms ShowDialog dismissed the dialog outright - the
        /// log read Prompting -> KeptOpen 0.7 s later - so Show had no dialog left to find.
        /// </summary>
        static void ShowDialogFocus()
        {
            IntPtr modal = Spawn("modal");
            IntPtr other = Spawn("ignore");
            GoToScratch();

            // Only the modal window is closed; the other one is there to hold the keyboard.
            _closer.Invalidate();
            var only = new List<WindowEntry>();
            foreach (WindowEntry w in _closer.Closeable(_scratch))
                if (w.Handle == modal) only.Add(w);

            CloseAllRun run = StartRun(only, false);
            Check(WaitFor(delegate { return StateOf(modal) == CloseState.Prompting; }, 3000), "dialog read as asking to save");
            IntPtr popup = Native.GetLastActivePopup(modal);

            // Windows lets only the foreground process give the foreground away, and here
            // that is the test window's, so this can be refused. Then the dialog still has
            // the keyboard and the last check below proves less - but LastFocused, the
            // window Show chose, is checked either way.
            Native.ForceForeground(other);
            bool moved = WaitFor(delegate { return Native.GetForegroundWindow() == other; }, 1500);
            if (!moved)
                Console.WriteLine("    note: could not take the keyboard off the dialog first ("
                                  + DescribeForeground() + "); the focus check below proves less");

            run.Show();
            Check(run.LastShown == modal, "Show picked the window with the dialog");
            Check(run.LastFocused == popup, "and chose the dialog, not its disabled owner, for the keyboard");
            bool focused = WaitFor(delegate { return Native.GetForegroundWindow() == popup; }, 1500);
            Check(focused, "the dialog, not its disabled owner, got the keyboard"
                  + (focused ? "" : " - foreground is " + DescribeForeground()));

            Native.PostMessage(popup, WM_ANSWER, IntPtr.Zero, IntPtr.Zero);
            WaitFor(delegate { return run.HasEnded; }, 4000);
            Check(run.HasEnded && DesktopExists(_scratch), "answered, run ended, desktop kept");
        }

        /// <summary>
        /// A row's Show on a run whose desktop is another one: one click switches there and,
        /// once the switch has settled, gives the dialog the keyboard. It used to take Go
        /// there and then Show. Also watches the switch's focus nudge, which used to take
        /// the keyboard off a dialog and hand it to the dialog's own disabled owner - and
        /// which this Show now waits out rather than racing.
        /// </summary>
        static void ShowFromElsewhere()
        {
            IntPtr modal = Spawn("modal");

            // Read at once, before the dialog is up. If the foreground lock is refused - it
            // needs this process to hold the foreground, which a test run beside an editor
            // does not always - the dialog pulls the session over within ~70 ms, and read
            // after that this would be testing the lock, not the button.
            CloseAllRun run = StartRun();
            Check(!run.ShowsHere, "the run's desktop is another one");
            Check(WaitFor(delegate { return StateOf(modal) == CloseState.Prompting; }, 3000), "dialog read as asking to save");
            if (_api.GetCurrentId() == _scratch)
                Console.WriteLine("    note: the dialog pulled the session over - the foreground lock was refused here");
            IntPtr popup = Native.GetLastActivePopup(modal);

            // Windows only lets a process with the foreground give it away. Here that is the
            // harness, which has it only if nothing else took it since it started; the app
            // has it from the click. Taken here so the check below means what it says.
            Native.ForceForeground(_pump.Handle);

            run.Show(modal);
            Check(WaitFor(delegate { return _api.GetCurrentId() == _scratch; }, 2000), "Show took the session to the desktop");
            Check(WaitFor(delegate { return run.LastFocused == popup; }, 2000), "and chose the dialog for the keyboard");
            Check(run.LastShown == modal, "for the row it was pressed on");
            if (run.LastFocusGranted)
            {
                bool focused = WaitFor(delegate { return Native.GetForegroundWindow() == popup; }, 1500);
                Pump(400);   // past anything the switch does afterwards, which must not take it back
                focused = focused && Native.GetForegroundWindow() == popup;
                Check(focused, "the dialog has the keyboard, and kept it"
                      + (focused ? "" : " - foreground is " + DescribeForeground()));
            }
            else
            {
                Console.WriteLine("    note: Windows refused this process the foreground (" + DescribeForeground()
                                  + "), so the keyboard check proves nothing here and is skipped");
            }

            Native.PostMessage(popup, WM_ANSWER, IntPtr.Zero, IntPtr.Zero);
            WaitFor(delegate { return run.HasEnded; }, 4000);
            Check(run.RemovedDesktop && !DesktopExists(_scratch), "answered, desktop removed");
        }

        /// <summary>
        /// The dialog's question, read off the label inside it rather
        /// than off its caption - the caption here is different on purpose, so the check
        /// tells the two apart.
        /// </summary>
        static void QuestionRead()
        {
            IntPtr modal = Spawn("modal");

            CloseAllRun run = StartRun();
            Check(WaitFor(delegate { return StateOf(modal) == CloseState.Prompting; }, 3000), "dialog read as asking to save");
            // The caption may come first - a read the moment the dialog appears can find it
            // still empty - and is replaced once the retry reads the text inside.
            var clock = Stopwatch.StartNew();
            bool read = WaitFor(delegate { return run.QuestionOf(modal) == AnswerDialog.Question; }, 4000);
            Check(read, "the row quotes the text inside the dialog, after " + clock.ElapsedMilliseconds
                        + " ms: \"" + run.QuestionOf(modal) + "\"");

            IntPtr popup = Native.GetLastActivePopup(modal);
            Native.PostMessage(popup, WM_ANSWER, IntPtr.Zero, IntPtr.Zero);
            WaitFor(delegate { return run.HasEnded; }, 4000);
            Check(run.RemovedDesktop, "answered, desktop removed");
        }

        /// <summary>
        /// The order the window shows: a window that got stuck goes above the ones listed
        /// before it, and a window that closed stays where it was and folds there.
        /// </summary>
        static void StuckFirst()
        {
            IntPtr slow = Spawn("slow");
            IntPtr close = Spawn("close");
            IntPtr modal = Spawn("modal");

            CloseAllRun run = StartRun();
            Check(WaitFor(delegate { return StateOf(modal) == CloseState.Prompting; }, 3000), "dialog read as asking to save");
            IntPtr[] order = run.DisplayOrder;
            Check(order.Length == 3 && order[0] == modal, "the asking window is first, though it was listed last");
            Check(WaitFor(delegate { return StateOf(close) == CloseState.Closed; }, 2000), "the plain window closed");
            // Listed in the inventory's order, not the order they were made in, so what is
            // checked is that closing did not change it relative to the other below the top.
            order = run.DisplayOrder;
            IntPtr[] listed = run.Handles;
            bool closeFirst = Array.IndexOf(listed, close) < Array.IndexOf(listed, slow);
            Check(order.Length == 3 && order[0] == modal
                  && (Array.IndexOf(order, close) < Array.IndexOf(order, slow)) == closeFirst,
                  "a closed window keeps its place rather than sliding past one still closing");

            Native.PostMessage(Native.GetLastActivePopup(modal), WM_ANSWER, IntPtr.Zero, IntPtr.Zero);
            WaitFor(delegate { return run.HasEnded; }, 4000);
            Check(run.RemovedDesktop, "desktop removed");
        }

        /// <summary>
        /// A prompt answered Cancel leaves the window kept open; Ask again puts the question
        /// back, and answering it this time lets the run finish.
        /// </summary>
        static void AskAgain()
        {
            IntPtr modal = Spawn("modal");

            CloseAllRun run = StartRun();
            Check(WaitFor(delegate { return StateOf(modal) == CloseState.Prompting; }, 3000), "dialog read as asking to save");
            Native.PostMessage(Native.GetLastActivePopup(modal), Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            Check(WaitFor(delegate { return StateOf(modal) == CloseState.KeptOpen; }, 3000), "Cancel read as kept open");

            run.AskAgain(modal);
            Check(StateOf(modal) == CloseState.Closing, "Ask again set it closing");
            Check(WaitFor(delegate { return StateOf(modal) == CloseState.Prompting; }, 3000), "and the question came back");

            Native.PostMessage(Native.GetLastActivePopup(modal), WM_ANSWER, IntPtr.Zero, IntPtr.Zero);
            WaitFor(delegate { return run.HasEnded; }, 4000);
            Check(run.RemovedDesktop && !Native.IsWindow(modal), "answered, window closed, desktop removed");
        }

        /// <summary>A row's Force ends that row's app and leaves the other stuck one alone.</summary>
        static void ForceOne()
        {
            IntPtr hang = Spawn("hang");
            IntPtr ignore = Spawn("ignore");

            CloseAllRun run = StartRun();
            Check(WaitFor(delegate
            {
                return StateOf(hang) == CloseState.StillOpen && StateOf(ignore) == CloseState.StillOpen;
            }, 3000), "both read as still open");

            run.ForceRow(ignore, false);
            Check(run.LastArmed == null, "an app with no other windows was forced on one click");
            Check(WaitFor(delegate { return StateOf(ignore) == CloseState.Closed; }, 3000), "the forced window closed");
            Check(StateOf(hang) != CloseState.Closed && Native.IsWindow(hang), "the other is still there");

            run.Force();
            WaitFor(delegate { return run.HasEnded; }, 4000);
            Check(run.RemovedDesktop, "Force all finished it, desktop removed");
        }

        /// <summary>
        /// A process with a second window on the user's desktop: the first Force only arms,
        /// naming what else it would end, and nothing is forced; the second ends the app,
        /// and the twin with it - which is exactly why it asked.
        /// </summary>
        static void ForceArms()
        {
            IntPtr main = Spawn("twin");
            IntPtr twin = IntPtr.Zero;
            Check(WaitFor(delegate { twin = FindTestWindow("twin " + _counter + " twin"); return twin != IntPtr.Zero; }, 3000),
                  "the app's second window opened on the user's desktop");

            CloseAllRun run = StartRun();
            Check(WaitFor(delegate { return StateOf(main) == CloseState.StillOpen; }, 3000), "read as still open");

            run.ForceRow(main, false);
            Check(run.LastArmed != null && run.LastArmed.Contains("+ 1 other"), "the first Force armed: \"" + run.LastArmed + "\"");
            Pump(500);
            Check(Native.IsWindow(main) && Native.IsWindow(twin), "and nothing was forced");

            run.ForceRow(main, true);
            WaitFor(delegate { return run.HasEnded; }, 4000);
            Check(run.RemovedDesktop, "the second did it, desktop removed");
            Check(!Native.IsWindow(twin), "and the twin went with it, as the label said");
        }

        /// <summary>
        /// The confirmation as the app builds it - laid out in its constructor, before it is
        /// shown - with Cancel along the bottom edge on the right, not at 0,0 over the title.
        /// Never shown; built from a real window's entry, checked and disposed.
        /// </summary>
        static void ConfirmLayout()
        {
            IntPtr a = Spawn("close");
            _closer.Invalidate();
            List<WindowEntry> windows = _closer.Closeable(_scratch);

            foreach (bool remove in new[] { false, true })
            {
                using (var window = new CloseAllWindow("Close all windows on \"test\"?", "A sentence.", windows, null,
                                                       remove, Color.FromArgb(0x20, 0x20, 0x20),
                                                       Color.FromArgb(0x3A, 0x96, 0xDD), 1.0, Rectangle.Empty,
                                                       new ClosePacing { Animate = false }))
                {
                    Rectangle cancel = window.CancelBounds;
                    Size client = window.ClientSize;
                    Check(cancel.Left > client.Width / 2 && cancel.Bottom > client.Height - 60,
                          (remove ? "remove: " : "keep: ") + "Cancel is bottom right before showing (" + cancel + " in " + client + ")");
                }
            }
            Check(Native.IsWindow(a), "the test window was left alone");
        }

        /// <summary>
        /// A window whose thread stops pumping reads as hung once Windows calls it that - about
        /// five seconds - and the row offers only Force.
        /// </summary>
        static void HungRow()
        {
            IntPtr hang = Spawn("hang");

            CloseAllRun run = StartRun();
            var clock = Stopwatch.StartNew();
            bool hung = WaitFor(delegate { return run.HungOf(hang); }, 9000);
            Check(hung, "read as not responding after " + clock.ElapsedMilliseconds + " ms");
            Check(StateOf(hang) == CloseState.StillOpen, "its state is still StillOpen, as before");

            run.Force();
            WaitFor(delegate { return run.HasEnded; }, 4000);
            Check(run.RemovedDesktop, "forced, desktop removed");
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
            return StartRun(true);
        }

        static CloseAllRun StartRun(bool remove)
        {
            _closer.Invalidate();
            return StartRun(_closer.Closeable(_scratch), remove);
        }

        static CloseAllRun StartRun(List<WindowEntry> windows)
        {
            return StartRun(windows, true);
        }

        static CloseAllRun StartRun(List<WindowEntry> windows, bool remove)
        {
            Desktop desktop = Find(_scratch);
            if (desktop == null) throw new InvalidOperationException("scratch desktop not in the model");

            _run = new CloseAllRun(_service, _closer, desktop, windows, remove,
                                   delegate(WindowEntry w) { return w.Title; });
            _run.Start(_pump, Color.FromArgb(0x20, 0x20, 0x20), 1.0, Rectangle.Empty);
            return _run;
        }

        /// <summary>
        /// The done window shows every row closed. Its rows used to freeze at whatever the
        /// last tick before the end said, so windows closing together stayed "closing".
        /// </summary>
        static void CheckDoneRows(CloseAllRun run)
        {
            CloseAllWindow window = run.Window;
            if (window == null) { Check(false, "the run left a window showing its outcome"); return; }

            // With the cascade the list runs behind the run on purpose; what has to hold is
            // that it catches up, inside the pacing's own bound, and only then says done.
            var pacing = new ClosePacing();
            int bound = pacing.MinProgressMs + pacing.MaxCascadeMs + 1000;
            var clock = Stopwatch.StartNew();
            bool caughtUp = WaitFor(delegate { return window.OpenRows == 0; }, bound + 2000);
            long ms = clock.ElapsedMilliseconds;
            Check(caughtUp, "the window shows every row closed (" + window.OpenRows + " open, after " + ms + " ms)");
            Check(ms <= bound, "within the pacing's bound of " + bound + " ms (" + ms + ")");
            Check(WaitFor(delegate { return window.IsDone || window.IsDisposed; }, 3000), "and then says done");
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

        /// <summary>
        /// Through the app's own SetViewPinned, so the pinned scenario exercises the same
        /// call the pin shortcut makes. It used to carry a private copy of the interface.
        /// </summary>
        static bool Pin(IntPtr hwnd, bool pin)
        {
            try
            {
                if (_api.SetViewPinned(hwnd, pin)) return true;
                Console.WriteLine("    note: pin refused - see log.txt");
                return false;
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

        /// <summary>
        /// The window that has the foreground, said so a focus failure can be told apart:
        /// the run's own panel holding it is a bug in the run, while an editor or terminal
        /// outside the test holding it means Windows never let the test take focus at all.
        /// </summary>
        static string DescribeForeground()
        {
            IntPtr fg = Native.GetForegroundWindow();
            if (fg == IntPtr.Zero) return "nobody";

            uint pid = Native.GetOwningProcessId(fg);
            string who = pid == (uint)Process.GetCurrentProcess().Id ? "this process"
                : Native.GetAppName(pid);
            return "0x" + fg.ToString("X") + " \"" + Native.GetText(fg) + "\" (" + Native.GetClass(fg)
                   + ", " + (who.Length > 0 ? who : "pid " + pid) + ")";
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

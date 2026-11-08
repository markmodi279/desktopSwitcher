using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace DesktopSwitcher
{
    /// <summary>
    /// The two selftests for reordering desktops, VirtualDesktopApi.Reorder.
    ///
    /// --reorder N M moves a real desktop and checks the result against a snapshot taken
    /// first: the names land in the new order, every window that had a desktop is on the
    /// one its desktop became, hidden ones included, and the user is still on the desktop
    /// they were on. It prints what every call cost, since whether a drop feels instant is
    /// decided by those numbers and nothing else.
    ///
    /// --reordertest does the same against desktops it creates for the purpose, on the end
    /// of the row, so none of the user's own desktops is touched: Reorder only recreates
    /// desktops at or after the target position, and every move here stays inside the
    /// scratch run. It puts a test window on each scratch desktop and a hidden one beside
    /// the first, and closes everything it opened afterwards, whatever happened. The hidden
    /// one was expected to be the hard case and turned out not to be: on 19045 a hidden
    /// window belongs to no desktop at all, and comes back on whichever is current.
    ///
    /// Apart from SelfTest because it needs its own window processes and cleanup, the way
    /// CloseAllStress does.
    /// </summary>
    static class ReorderTest
    {
        const string TitlePrefix = "DSWREORDER ";
        const string NamePrefix = "reorder test ";

        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int cmd);
        const int SW_HIDE = 0;
        const int SW_SHOWNOACTIVATE = 4;

        // --- --reorder N M ------------------------------------------------------

        public static int RunLive(VirtualDesktopApi api, string[] args)
        {
            int from, to;
            if (args.Length < 3 || !int.TryParse(args[1], out from) || !int.TryParse(args[2], out to))
            {
                Console.WriteLine("Usage: --reorder N M   (move desktop N to position M, 1-based)");
                return 2;
            }

            int count = api.GetCount();
            if (from < 1 || from > count || to < 1 || to > count)
            {
                Console.WriteLine("Desktops are 1.." + count);
                return 2;
            }

            return MoveAndCheck(api, from - 1, to - 1) ? 0 : 1;
        }

        // --- --reordertest --------------------------------------------------------

        public static int RunScratch(VirtualDesktopApi api)
        {
            Guid home = api.GetCurrentId();
            int userCount = api.GetCount();
            var procs = new List<Process>();
            var windows = new List<IntPtr>();
            bool ok = true;

            Console.WriteLine("Scratch reorder test. Your " + userCount + " desktops are left in place;");
            Console.WriteLine("three scratch desktops go on the end and are removed afterwards.");
            Console.WriteLine();

            try
            {
                string[] names = { "A", "B", "C" };
                var scratch = new Guid[3];
                for (int i = 0; i < 3; i++)
                {
                    scratch[i] = api.Create();
                    api.SetName(scratch[i], NamePrefix + names[i]);
                }

                for (int i = 0; i < 3; i++)
                {
                    IntPtr w = Spawn(api, procs, scratch[i], names[i]);
                    windows.Add(w);
                }

                // The one that matters: a window with no taskbar button, no Alt+Tab entry,
                // nothing WindowInventory lists - a tray app's main window, in effect.
                IntPtr hidden = Spawn(api, procs, scratch[0], "A hidden");
                ShowWindow(hidden, SW_HIDE);
                windows.Add(hidden);
                Thread.Sleep(200);

                // A hidden window reports no desktop at all - GetWindowDesktopId fails for it
                // - so the snapshot cannot follow it. What matters is where it comes back
                // when shown. The control first: shown again with nothing reordered, while
                // the user is on another desktop, does it come back on its own?
                Guid hiddenHome;
                bool hiddenTracked = api.TryGetWindowDesktop(hidden, out hiddenHome);
                Console.WriteLine("Hidden window reports a desktop while hidden: " + (hiddenTracked ? "yes" : "no"));
                Console.WriteLine("Control, nothing reordered: shown again it lands on "
                                  + ShowAndLocate(api, hidden) + " (it was hidden on " + Quote(NamePrefix + "A") + ")");
                ShowWindow(hidden, SW_HIDE);
                Thread.Sleep(200);
                Console.WriteLine();

                int a = userCount, b = userCount + 1, c = userCount + 2;

                Console.WriteLine("== last scratch to first  (A B C -> C A B)");
                ok &= MoveAndCheck(api, c, a);

                Console.WriteLine("== first scratch to last  (C A B -> A B C)");
                ok &= MoveAndCheck(api, a, c);

                Console.WriteLine("== swap the last two      (A B C -> A C B)");
                ok &= MoveAndCheck(api, c, b);

                // The current desktop recreated under the user: removing it makes the shell
                // switch to its replacement, which carries its windows - so the user should
                // end up on the same windows, under a new GUID.
                Guid[] ids = api.GetDesktopIds();
                Console.WriteLine("== the current desktop recreated  (switch to scratch A, move it last: A C B -> C B A)");
                api.SwitchTo(ids[a]);
                Thread.Sleep(400);
                ok &= MoveAndCheck(api, a, c);

                // Measured on 19045, and the control above shows it every run: a hidden window
                // belongs to no desktop. It loses its view when hidden and gets a new one on
                // whichever desktop is current when it is shown. So a reorder cannot strand
                // one - there is nothing to carry - and the check is only that it still comes
                // back where the control says it should, on the current desktop, after A was
                // recreated around it three times.
                Guid now = api.GetCurrentId();
                string expected = VirtualDesktopApi.GetName(now) != null
                    ? Quote(VirtualDesktopApi.GetName(now))
                    : "desktop " + (Array.IndexOf(api.GetDesktopIds(), now) + 1);
                string landed = ShowAndLocate(api, hidden);
                bool fine = landed == expected;
                Console.WriteLine("== hidden window, shown after every reorder: lands on " + landed
                                  + (fine ? ", the current desktop  OK" : ", wanted the current one, " + expected));
                Console.WriteLine();
                ok &= fine;

                Console.WriteLine("== through DesktopService, as the app does it  (C B A -> A C B)");
                ok &= ServiceSeesOneChange(api, c, a);
            }
            catch (Exception ex)
            {
                Console.WriteLine("ERROR  " + ex.GetType().Name + ": " + ex.Message);
                ok = false;
            }
            finally
            {
                Cleanup(api, procs, windows, home);
            }

            Console.WriteLine(ok ? "PASS - every scenario held" : "FAIL - see above");
            return ok ? 0 : 1;
        }

        // --- the check ------------------------------------------------------------

        /// <summary>
        /// Snapshots the desktops and every window that has one, reorders, and checks the
        /// result against the snapshot. Prints each step's timing and every disagreement.
        /// </summary>
        static bool MoveAndCheck(VirtualDesktopApi api, int from, int to)
        {
            Guid[] before = api.GetDesktopIds();
            Guid current = api.GetCurrentId();
            int currentAt = Array.IndexOf(before, current);

            var names = new string[before.Length];
            for (int i = 0; i < before.Length; i++) names[i] = VirtualDesktopApi.GetName(before[i]);

            // Where each window should end up: the old position of its desktop, mapped
            // through the move. Pinned windows have no single desktop and are left out.
            Dictionary<IntPtr, int> where = SnapshotWindows(api, before);
            int hiddenCount = 0;
            foreach (IntPtr h in where.Keys) if (!Native.IsWindowVisible(h)) hiddenCount++;

            var order = new List<int>();
            for (int i = 0; i < before.Length; i++) order.Add(i);
            order.RemoveAt(from);
            order.Insert(to, from);

            Console.WriteLine("  move " + (from + 1) + " -> " + (to + 1) + ", " + where.Count + " windows tracked ("
                              + hiddenCount + " hidden)");

            var total = Stopwatch.StartNew();
            List<VirtualDesktopApi.ReorderStep> steps = api.Reorder(from, to);
            total.Stop();

            if (steps == null)
            {
                Console.WriteLine("  FAIL  Reorder refused the indices");
                return false;
            }

            foreach (VirtualDesktopApi.ReorderStep s in steps)
            {
                Console.WriteLine(string.Format("    step  {0,-22} create {1,6:F1} ms  remove {2,6:F1} ms  rename {3,6:F1} ms{4}",
                    Quote(s.Name), s.CreateMs, s.RemoveMs, s.RenameMs, s.WasCurrent ? "   (was current)" : ""));
            }
            Console.WriteLine(string.Format("    total {0:F1} ms for {1} step(s)", total.Elapsed.TotalMilliseconds, steps.Count));

            Guid[] after = api.GetDesktopIds();
            bool ok = true;

            if (after.Length != before.Length)
            {
                Console.WriteLine("  FAIL  desktop count " + before.Length + " -> " + after.Length);
                return false;
            }

            // Names, in the new order. An unnamed desktop stays unnamed.
            for (int i = 0; i < after.Length; i++)
            {
                string want = names[order[i]];
                string got = VirtualDesktopApi.GetName(after[i]);
                if (!SameName(want, got))
                {
                    Console.WriteLine("  FAIL  position " + (i + 1) + " is " + Quote(got) + ", wanted " + Quote(want));
                    ok = false;
                }
            }

            // Desktops that were not recreated must keep their GUIDs - the proof that the
            // minimum was touched.
            int kept = after.Length - steps.Count;
            for (int i = 0; i < kept; i++)
            {
                if (after[i] != before[order[i]])
                {
                    Console.WriteLine("  FAIL  position " + (i + 1) + " was recreated but should have been kept");
                    ok = false;
                }
            }

            // Every tracked window on the desktop its own became.
            int misplaced = 0;
            foreach (KeyValuePair<IntPtr, int> pair in where)
            {
                if (!Native.IsWindow(pair.Key)) continue;
                Guid now;
                if (!api.TryGetWindowDesktop(pair.Key, out now)) continue;

                int wantAt = order.IndexOf(pair.Value);
                int gotAt = Array.IndexOf(after, now);
                if (gotAt != wantAt)
                {
                    misplaced++;
                    if (misplaced <= 5)
                        Console.WriteLine("  FAIL  " + Describe(pair.Key) + " on " + (gotAt + 1) + ", wanted " + (wantAt + 1));
                }
            }
            if (misplaced > 5) Console.WriteLine("  FAIL  ... and " + (misplaced - 5) + " more");
            if (misplaced > 0) ok = false;

            // The user stays on the same desktop, wherever it now sits.
            int wantCurrent = order.IndexOf(currentAt);
            int gotCurrent = Array.IndexOf(after, api.GetCurrentId());
            if (gotCurrent != wantCurrent)
            {
                Console.WriteLine("  FAIL  current is " + (gotCurrent + 1) + ", wanted " + (wantCurrent + 1));
                ok = false;
            }

            // Whether Explorer forgets a removed desktop's name entry, or leaves it behind.
            int stale = 0;
            foreach (VirtualDesktopApi.ReorderStep s in steps)
                if (RegistryKeyExists(s.OldId)) stale++;
            if (steps.Count > 0)
                Console.WriteLine("    registry: " + stale + " of " + steps.Count + " removed desktops left a name key behind");

            Console.WriteLine(ok ? "  OK" : "  FAILED");
            Console.WriteLine();
            return ok;
        }

        /// <summary>
        /// The claim the strip's drag is built on: a reorder made on the UI thread reaches
        /// the strip as one DesktopsChanged carrying the final order, never as the run of
        /// intermediate rows - one desktop more, then one less, per step - that another
        /// process watching sees. The notifications for each step arrive on RPC threads and
        /// are posted to the UI thread, so they should queue behind the reorder and find
        /// nothing left to change. Unless COM's modal loop, which pumps messages while an
        /// STA thread waits on a cross-process call, dispatches them mid-run; this is what
        /// would catch that.
        /// </summary>
        static bool ServiceSeesOneChange(VirtualDesktopApi api, int from, int to)
        {
            using (var form = new System.Windows.Forms.Form())
            {
                IntPtr handle = form.Handle;   // the marshaller needs a real window
                var service = new DesktopService(api, form);
                var counts = new List<int>();
                service.DesktopsChanged += delegate { counts.Add(service.Count); };

                // What the marks are carried by: each recreated desktop, old GUID to new, and
                // whether it was reported before the new order reached DesktopsChanged.
                var carried = new Dictionary<Guid, Guid>();
                bool carriedLate = false;
                service.Recreated += delegate(Guid oldId, Guid newId)
                {
                    carried[oldId] = newId;
                    if (counts.Count > 0) carriedLate = true;
                };
                service.Start();
                Pump(300);
                counts.Clear();

                Guid[] before = api.GetDesktopIds();
                var names = new string[before.Length];
                for (int i = 0; i < before.Length; i++) names[i] = VirtualDesktopApi.GetName(before[i]);

                service.Reorder(from, to);
                Pump(800);

                var order = new List<int>();
                for (int i = 0; i < before.Length; i++) order.Add(i);
                order.RemoveAt(from);
                order.Insert(to, from);

                bool ok = true;
                IList<Desktop> model = service.Desktops;
                for (int i = 0; i < model.Count; i++)
                    if (!SameName(model[i].Name, names[order[i]])) ok = false;
                if (model.Count != before.Length) ok = false;

                // Recreated must account for every GUID: each desktop in the new row is either
                // one that stayed, or the one Recreated named as its replacement.
                bool mapped = carried.Count > 0;
                for (int i = 0; i < model.Count && i < order.Count; i++)
                {
                    Guid was = before[order[i]];
                    Guid expect;
                    if (!carried.TryGetValue(was, out expect)) expect = was;
                    if (model[i].Id != expect) mapped = false;
                }

                Console.WriteLine("  DesktopsChanged raised " + counts.Count + " time(s), with counts ["
                                  + string.Join(", ", counts.ConvertAll(n => n.ToString()).ToArray()) + "]");
                Console.WriteLine("  model order " + (ok ? "matches" : "DOES NOT match") + " the new order");
                Console.WriteLine("  Recreated named " + carried.Count + " replacement(s), "
                                  + (mapped ? "accounting for every desktop" : "NOT accounting for every desktop")
                                  + (carriedLate ? ", some AFTER DesktopsChanged" : ", all before DesktopsChanged"));

                bool one = counts.Count == 1 && counts[0] == before.Length;
                bool marksOk = mapped && !carriedLate;
                Console.WriteLine(one && ok && marksOk ? "  OK"
                                  : !marksOk ? "  FAILED - marks would not follow their desktops"
                                  : "  FAILED - the strip would see intermediate rows");
                Console.WriteLine();

                service.Dispose();
                return one && ok && marksOk;
            }
        }

        static void Pump(int ms)
        {
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < ms)
            {
                System.Windows.Forms.Application.DoEvents();
                Thread.Sleep(10);
            }
        }

        static Dictionary<IntPtr, int> SnapshotWindows(VirtualDesktopApi api, Guid[] ids)
        {
            var result = new Dictionary<IntPtr, int>();
            var all = new List<IntPtr>();
            Native.EnumWindows(delegate(IntPtr hwnd, IntPtr param) { all.Add(hwnd); return true; }, IntPtr.Zero);

            foreach (IntPtr hwnd in all)
            {
                Guid id;
                if (!api.TryGetWindowDesktop(hwnd, out id)) continue;
                int at = Array.IndexOf(ids, id);
                if (at < 0) continue;
                if (Native.IsWindowVisible(hwnd) && api.IsPinned(hwnd)) continue;
                result[hwnd] = at;
            }
            return result;
        }

        // --- scratch windows ------------------------------------------------------

        static IntPtr Spawn(VirtualDesktopApi api, List<Process> procs, Guid desktop, string label)
        {
            string title = TitlePrefix + label;
            var psi = new ProcessStartInfo(System.Reflection.Assembly.GetExecutingAssembly().Location,
                                           "--closetarget close \"" + title + "\"");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            Process p = Process.Start(psi);
            procs.Add(p);

            IntPtr hwnd = IntPtr.Zero;
            for (int i = 0; i < 80 && hwnd == IntPtr.Zero; i++)
            {
                Thread.Sleep(100);
                p.Refresh();
                hwnd = p.MainWindowHandle;
            }
            if (hwnd == IntPtr.Zero) throw new InvalidOperationException("test window " + title + " never appeared");

            api.MoveWindow(hwnd, desktop);
            for (int i = 0; i < 30; i++)
            {
                Guid at;
                if (api.TryGetWindowDesktop(hwnd, out at) && at == desktop) return hwnd;
                Thread.Sleep(100);
            }
            throw new InvalidOperationException("could not move " + title + " to its scratch desktop");
        }

        /// <summary>
        /// Closes every test window, removes every desktop wearing the scratch name, and
        /// goes back to the desktop the run started on - or, if that one was recreated,
        /// to whichever desktop the user's first window is on now. Whatever the run did.
        /// </summary>
        static void Cleanup(VirtualDesktopApi api, List<Process> procs, List<IntPtr> windows, Guid home)
        {
            try
            {
                foreach (IntPtr w in windows)
                    if (Native.IsWindow(w)) Native.PostMessage(w, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

                foreach (Process p in procs)
                {
                    try
                    {
                        if (!p.WaitForExit(3000)) p.Kill();
                    }
                    catch (Exception) { }
                }

                foreach (Guid id in api.GetDesktopIds())
                {
                    string name = VirtualDesktopApi.GetName(id);
                    if (name != null && name.StartsWith(NamePrefix)) api.Remove(id);
                }

                if (Array.IndexOf(api.GetDesktopIds(), home) >= 0 && api.GetCurrentId() != home)
                    api.SwitchTo(home);

                Console.WriteLine("Cleaned up: " + api.GetCount() + " desktops remain.");
                Console.WriteLine();
            }
            catch (Exception ex)
            {
                Console.WriteLine("CLEANUP ERROR  " + ex.Message + " - check Task View for leftover '" + NamePrefix + "' desktops");
            }
        }

        // --- helpers ------------------------------------------------------------

        /// <summary>Shows a window without activating it and names the desktop it is on.</summary>
        static string ShowAndLocate(VirtualDesktopApi api, IntPtr hwnd)
        {
            ShowWindow(hwnd, SW_SHOWNOACTIVATE);
            for (int i = 0; i < 20; i++)
            {
                Thread.Sleep(100);
                Guid at;
                if (api.TryGetWindowDesktop(hwnd, out at))
                {
                    int index = Array.IndexOf(api.GetDesktopIds(), at);
                    string name = VirtualDesktopApi.GetName(at);
                    return name != null ? Quote(name) : "desktop " + (index + 1);
                }
            }
            return "(no desktop)";
        }

        static bool RegistryKeyExists(Guid id)
        {
            string key = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops\Desktops\"
                         + id.ToString("B").ToUpperInvariant();
            using (var rk = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(key))
                return rk != null;
        }

        static bool SameName(string a, string b)
        {
            if (string.IsNullOrEmpty(a)) return string.IsNullOrEmpty(b);
            return a == b;
        }

        static string Quote(string name)
        {
            return name == null ? "(unnamed)" : "\"" + name + "\"";
        }

        static string Describe(IntPtr hwnd)
        {
            string title = Native.GetText(hwnd);
            if (title.Length > 40) title = title.Substring(0, 40);
            return "\"" + title + "\"" + (Native.IsWindowVisible(hwnd) ? "" : " (hidden)");
        }
    }
}
